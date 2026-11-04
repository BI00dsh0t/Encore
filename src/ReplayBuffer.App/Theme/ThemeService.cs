using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Theme;

/// <summary>
/// Applies <c>appTheme</c> (AD-23). Fluent is WPF's <c>ThemeMode = System</c>, which follows the Windows
/// mode and accent live; Gaming dark is <c>ThemeMode = Dark</c> with <see cref="GamingDarkTheme"/> merged
/// after Fluent's dictionary. A switch swaps the merged dictionary in place, on the UI thread, so every
/// window, popup and menu taking its resources from the host restyles with nothing recreated. It also
/// sets each window's frame: Mica behind the Main App under Fluent, the Gaming caption colours otherwise.
/// </summary>
internal sealed class ThemeService
{
    /// <summary>
    /// WPF's Fluent theme puts Mica behind every window by itself. The App wants it on the Main App only
    /// (DESIGN.md), and never under Gaming dark, so it turns that off and sets the backdrop itself.
    /// </summary>
    private const string DisableFluentBackdropSwitch = "Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop";

    private readonly ResourceDictionary _resources;
    private readonly Action<AppTheme> _setThemeMode;
    private readonly Func<IEnumerable<Window>> _windows;
    private readonly SharedTheme _shared = new();
    private readonly FluentTheme _fluent = new();
    private readonly GamingDarkTheme _gaming = new();
    // Each window with Mica on, and the composition background it had before.
    private readonly ConditionalWeakTable<Window, object> _mica = [];

    /// <summary>
    /// Fluent's built-in TextBox, ListBoxItem and ListViewItem keep full-contrast text when disabled, so the
    /// App layers a disabled foreground over Fluent's own style for each (R4).
    /// </summary>
    private static readonly Type[] FluentDimsWhenDisabled = [typeof(TextBox), typeof(ListBoxItem), typeof(ListViewItem)];

    static ThemeService() => AppContext.SetSwitch(DisableFluentBackdropSwitch, true);

    private ThemeService(ResourceDictionary resources, Action<AppTheme> setThemeMode, Func<IEnumerable<Window>> windows)
    {
        _resources = resources;
        _setThemeMode = setThemeMode;
        _windows = windows;
    }

    /// <summary>
    /// Every token key of DESIGN.md (Colors, Shapes and Typography), which both themes resolve: Fluent
    /// through its own keys plus <see cref="FluentTheme"/>, Gaming dark through <see cref="GamingDarkTheme"/>.
    /// </summary>
    public static IReadOnlyList<string> TokenKeys { get; } =
    [
        "SolidBackgroundFillColorBaseBrush", "LayerFillColorDefaultBrush", "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush", "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush",
        "ControlFillColorTertiaryBrush", "ControlFillColorDisabledBrush", "ControlFillColorInputActiveBrush",
        "SubtleFillColorTransparentBrush", "SubtleFillColorSecondaryBrush", "SubtleFillColorTertiaryBrush",
        "SolidBackgroundFillColorQuarternaryBrush", "ControlStrongFillColorDefaultBrush", "ControlOnImageFillColorDefaultBrush",
        "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "CardStrokeColorDefaultBrush",
        "SurfaceStrokeColorFlyoutBrush", "DividerStrokeColorDefaultBrush", "ControlStrongStrokeColorDefaultBrush",
        "ControlStrongStrokeColorDisabledBrush", "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush",
        "TextFillColorDisabledBrush", "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush", "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush", "AccentFillColorDisabledBrush", "RbAccentSubtleBrush",
        "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCautionBrush",
        "SystemFillColorCautionBackgroundBrush", "SystemFillColorSuccessBrush", "RbRecordBrush",
        "FocusStrokeColorOuterBrush", "FocusStrokeColorInnerBrush", "RbOverlayCardBrush",
        "ControlCornerRadius", "OverlayCornerRadius", "RbSmallCornerRadius", "RbNavItemCornerRadius",
        "CaptionTextBlockStyle", "BodyTextBlockStyle", "BodyStrongTextBlockStyle", "SubtitleTextBlockStyle",
        "RbSectionHeaderStyle", "RbPageTitleStyle", "RbBrandStyle",
        "AccentButtonStyle", "DefaultControlFocusVisualStyle",
    ];

    /// <summary>The App's service, which every <see cref="ShellWindow"/> asks to set its frame as it gets a handle.</summary>
    public static ThemeService? Current { get; set; }

    /// <summary>The theme in use; null until the first <see cref="Apply"/>.</summary>
    public AppTheme? Theme { get; private set; }

    /// <summary>The theme's own dictionary, merged after <see cref="Shared"/>.</summary>
    public ResourceDictionary ThemeDictionary => Theme == AppTheme.GamingDark ? _gaming : _fluent;

    /// <summary>Spacing and the App's generic styles, merged after Fluent's dictionary in both themes.</summary>
    public ResourceDictionary Shared => _shared;

    /// <summary>Gaming dark's dictionary, whichever theme is in use.</summary>
    public ResourceDictionary GamingDark => _gaming;

    /// <summary>The App's: the theme goes in <see cref="Application.Resources"/>, so every window and menu follows it.</summary>
    public static ThemeService ForApplication(Application app) =>
#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10 WPF; AD-23 builds both themes on it.
        new(app.Resources, theme => app.ThemeMode = ModeFor(theme), () => app.Windows.OfType<Window>());
#pragma warning restore WPF0001

    /// <summary>One window's own: the same switch, scoped to the window's resources, for tests and previews.</summary>
    public static ThemeService ForWindow(Window window) =>
#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10 WPF; AD-23 builds both themes on it.
        new(window.Resources, theme => window.ThemeMode = ModeFor(theme), () => [window]);
#pragma warning restore WPF0001

    /// <summary>Applies <paramref name="theme"/> on the UI thread. False when it is already in use.</summary>
    public bool Apply(AppTheme theme)
    {
        if (Theme == theme)
        {
            return false;
        }

        // WPF puts its Fluent dictionary first in the list; the App's go after it, so they win.
        _setThemeMode(theme);
        var merged = _resources.MergedDictionaries;
        if (!merged.Contains(_shared))
        {
            merged.Add(_shared);
        }

        if (theme == AppTheme.Fluent)
        {
            DimFluentDisabledText(merged);
        }

        ResourceDictionary next = theme == AppTheme.GamingDark ? _gaming : _fluent;
        ResourceDictionary previous = theme == AppTheme.GamingDark ? _fluent : _gaming;
        var at = merged.IndexOf(previous);
        if (at >= 0)
        {
            merged[at] = next;
        }
        else
        {
            merged.Insert(merged.IndexOf(_shared) + 1, next);
        }

        Theme = theme;
        foreach (var window in _windows())
        {
            Decorate(window);
        }

        return true;
    }

    /// <summary>
    /// Follows the engine's committed <c>appTheme</c>: the snapshot's, then each <c>settings.changed</c>'s,
    /// applied through <paramref name="post"/> on the UI thread. A refused update never changes it.
    /// </summary>
    /// <param name="applied">Called with each theme newly applied.</param>
    public void Follow(IEngineSession engine, Action<Action> post, Action<AppTheme>? applied = null)
    {
        engine.Changed += () => post(() =>
        {
            if (engine.State?.Settings is { } settings && Apply(SettingsValues.Global(settings, Keys.AppTheme)))
            {
                applied?.Invoke(Theme!.Value);
            }
        });
    }

    /// <summary>
    /// Sets the window's frame for the theme in use: Mica behind the Main App under Fluent where Windows
    /// supports it, and under Gaming dark the title bar in <c>bg-base</c> and <c>text-primary</c>.
    /// Transparent windows (the Overlay) are left alone. A window with no handle yet is set when it gets one.
    /// </summary>
    public void Decorate(Window window)
    {
        if (Theme is not { } theme || window.AllowsTransparency || new WindowInteropHelper(window).Handle is var hwnd && hwnd == 0)
        {
            return;
        }

        var mica = theme == AppTheme.Fluent && window is ShellWindow { UsesMica: true } && WindowFrame.SupportsMica;
        if (mica != _mica.TryGetValue(window, out _))
        {
            var target = HwndSource.FromHwnd(hwnd).CompositionTarget;
            if (mica)
            {
                // Mica shows only through a transparent window and composition background.
                _mica.Add(window, target.BackgroundColor);
                window.Background = Brushes.Transparent;
                target.BackgroundColor = Colors.Transparent;
            }
            else
            {
                target.BackgroundColor = _mica.TryGetValue(window, out var before) ? (Color)before : target.BackgroundColor;
                _mica.Remove(window);
                window.ClearValue(Control.BackgroundProperty);
            }

            WindowFrame.SetMica(hwnd, mica);
        }

        WindowFrame.SetCaptionColors(hwnd, theme == AppTheme.GamingDark
            ? (Rgb(_gaming, "SolidBackgroundFillColorBaseBrush"), Rgb(_gaming, "TextFillColorPrimaryBrush"))
            : null);
    }

    /// <summary>The theme the App last applied, kept so the next start applies it before its first window shows.</summary>
    public static AppTheme ReadLast(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return Keys.AppTheme.TryRead(json.RootElement, out var theme) ? theme : Keys.AppTheme.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Keys.AppTheme.Default;
        }
    }

    /// <summary>Records <paramref name="theme"/> for <see cref="ReadLast"/>. Best effort.</summary>
    public static void WriteLast(string path, AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Keys.AppTheme.ToJson(theme).GetRawText());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine's settings stay the record; this only saves a restyle at the next start.
        }
    }

    // Based on the style in WPF's Fluent dictionary, which is first in the list once ThemeMode is set.
    private void DimFluentDisabledText(IList<ResourceDictionary> merged)
    {
        var fluent = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.Ordinal) == true);
        foreach (var type in FluentDimsWhenDisabled)
        {
            if (fluent?[type] is not Style basis)
            {
                continue;
            }

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorDisabledBrush")));
            var style = new Style(type, basis) { Triggers = { disabled } };
            _fluent[type] = style;
        }
    }

#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10 WPF; AD-23 builds both themes on it.
    private static ThemeMode ModeFor(AppTheme theme) => theme == AppTheme.GamingDark ? ThemeMode.Dark : ThemeMode.System;
#pragma warning restore WPF0001

    private static uint Rgb(ResourceDictionary dictionary, string key) =>
        ((SolidColorBrush)dictionary[key]).Color is var c ? (uint)((c.R << 16) | (c.G << 8) | c.B) : 0;
}
