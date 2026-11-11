using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Encore.App.Engine;
using Encore.App.Settings;
using Encore.Contracts.Settings;
using Encore.Platform.Desktop;

namespace Encore.App.Theme;

/// <summary>Applies <c>appTheme</c>: swaps the merged dictionary in place on the UI thread, and sets each window's frame.</summary>
internal sealed class ThemeService
{
    /// <summary>Turns off Fluent's automatic Mica on every window; it must be set in runtimeconfig. See docs/code-notes.md.</summary>
    public const string DisableFluentBackdropSwitch = "Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop";

    private readonly ResourceDictionary _resources;
    private readonly Action<AppTheme> _setThemeMode;
    private readonly Func<IEnumerable<Window>> _windows;
    private readonly SharedTheme _shared = new();
    private readonly FluentTheme _fluent = new();
    private readonly GamingDarkTheme _gaming = new();
    // Each window with Mica on, and the composition background it had before.
    private readonly ConditionalWeakTable<Window, object> _mica = [];

    /// <summary>Fluent keeps full-contrast text on these controls when disabled, so the App layers a disabled colour on each.</summary>
    private static readonly Type[] FluentDimsWhenDisabled = [typeof(TextBox), typeof(ListBoxItem), typeof(ListViewItem), typeof(TabItem)];

    private ThemeService(ResourceDictionary resources, Action<AppTheme> setThemeMode, Func<IEnumerable<Window>> windows)
    {
        _resources = resources;
        _setThemeMode = setThemeMode;
        _windows = windows;
    }

    /// <summary>Every token key of the design system, which both themes resolve.</summary>
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
        "AccentFillColorTertiaryBrush", "AccentFillColorDisabledBrush", "EncoreAccentSubtleBrush",
        "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCautionBrush",
        "SystemFillColorCautionBackgroundBrush", "SystemFillColorSuccessBrush", "EncoreRecordBrush",
        "FocusStrokeColorOuterBrush", "FocusStrokeColorInnerBrush", "EncoreOverlayCardBrush", "EncoreBrandMintBrush",
        "EncoreMarkGeometry",
        "ControlCornerRadius", "OverlayCornerRadius", "EncoreSmallCornerRadius", "EncoreNavItemCornerRadius",
        "CaptionTextBlockStyle", "BodyTextBlockStyle", "BodyStrongTextBlockStyle", "SubtitleTextBlockStyle",
        "EncoreSectionHeaderStyle", "EncorePageTitleStyle", "EncoreBrandStyle",
        "AccentButtonStyle", "DefaultControlFocusVisualStyle",
    ];

    /// <summary>The App's service, which every <see cref="ShellWindow"/> asks to set its frame as it gets a handle.</summary>
    public static ThemeService? Current { get; set; }

    /// <summary>The theme in use; null until the first <see cref="Apply"/>.</summary>
    public AppTheme? Theme { get; private set; }

    /// <summary>The theme's own dictionary, merged after <see cref="SharedTheme"/>.</summary>
    public ResourceDictionary ThemeDictionary => Theme == AppTheme.GamingDark ? _gaming : _fluent;

    /// <summary>Gaming dark's dictionary, whichever theme is in use.</summary>
    public ResourceDictionary GamingDark => _gaming;

    /// <summary>The App's: the theme goes in <see cref="Application.Resources"/>, so every window and menu follows it.</summary>
    public static ThemeService ForApplication(Application app) =>
#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10 WPF; both themes are built on it.
        new(app.Resources, theme => app.ThemeMode = ModeFor(theme), () => app.Windows.OfType<Window>());
#pragma warning restore WPF0001

    /// <summary>One window's own: the same switch, scoped to the window's resources, for tests and previews.</summary>
    public static ThemeService ForWindow(Window window) =>
#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10 WPF; both themes are built on it.
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

        ShellTheme.Place(merged, theme);
        AddOnce<SettingsLayout>(merged);
        AddOnce<LibraryTheme>(merged);
        AddOnce<AudioTracksTheme>(merged);
        AddOnce<GamesTheme>(merged);
        Theme = theme;
        foreach (var window in _windows())
        {
            Decorate(window);
        }

        return true;
    }

    /// <summary>Follows the engine's committed <c>appTheme</c>, applied on the UI thread; a refused update never changes it.</summary>
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

    /// <summary>Sets the window's frame for the theme: Mica under Fluent, the title bar colours under Gaming dark.</summary>
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

    /// <summary>Adds one dictionary of a type unless it is there, before the last entry so the theme's dictionary stays last.</summary>
    private static void AddOnce<T>(IList<ResourceDictionary> merged)
        where T : ResourceDictionary, new()
    {
        if (!merged.Any(dictionary => dictionary is T))
        {
            merged.Insert(Math.Max(0, merged.Count - 1), new T());
        }
    }

    // Based on the style in WPF's Fluent dictionary, which is first in the list once ThemeMode is set.
    private void DimFluentDisabledText(IList<ResourceDictionary> merged)
    {
        const string FluentAssembly = "PresentationFramework.Fluent";
        var fluent = merged.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.Contains(FluentAssembly, StringComparison.Ordinal) == true);
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

#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10 WPF; both themes are built on it.
    private static ThemeMode ModeFor(AppTheme theme) => theme == AppTheme.GamingDark ? ThemeMode.Dark : ThemeMode.System;
#pragma warning restore WPF0001

    private static uint Rgb(ResourceDictionary dictionary, string key) =>
        ((SolidColorBrush)dictionary[key]).Color is var c ? (uint)((c.R << 16) | (c.G << 8) | c.B) : 0;
}
