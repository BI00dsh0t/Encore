using System.Windows;
using Encore.App.Overlay;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Global settings with no page of their own. The tray's Start with Windows item binds here too.</summary>
internal sealed class GeneralSettingsPage(SettingsSender sender) : SettingsPage(sender)
{
    private bool _startWithWindows;
    private OverlayDotMode _overlayDot = Keys.OverlayDot.Default;
    private AppTheme _appTheme = Keys.AppTheme.Default;
    private bool _captureNewGames = Keys.CaptureNewGames.Default;

    private static readonly IReadOnlyList<OverlayDotChoice> Choices =
    [
        new(OverlayDotMode.Auto, "Automatic: always, unless it would cost a borderless game FPS"),
        new(OverlayDotMode.Always, "Always, while capture runs"),
        new(OverlayDotMode.OnChange, $"For {(int)OverlayViewModel.DotOnChangeDuration.TotalSeconds} seconds after each change"),
    ];

    private static readonly IReadOnlyList<AppThemeChoice> Themes =
    [
        new(AppTheme.Fluent, "Fluent (follows Windows)"),
        new(AppTheme.GamingDark, "Gaming dark"),
    ];

    public override string Title => "General";

    /// <summary>
    /// <c>startWithWindows</c>. Setting it sends the change; <see cref="StartupRegistration"/> writes the
    /// Run value once the engine commits it, in the installed instance only (AD-16).
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value))
            {
                _ = SendAsync([SettingsValues.SetGlobal(Keys.StartWithWindows, value)]);
            }
        }
    }

    /// <summary>The <c>overlayDot</c> choices, as the page lists them.</summary>
    public IReadOnlyList<OverlayDotChoice> OverlayDotChoices => Choices;

    /// <summary><c>overlayDot</c>: when the Overlay's dot shows. Setting it sends the change.</summary>
    public OverlayDotMode OverlayDot
    {
        get => _overlayDot;
        set
        {
            if (Set(ref _overlayDot, value))
            {
                _ = SendAsync([SettingsValues.SetGlobal(Keys.OverlayDot, value)]);
            }
        }
    }

    /// <summary>The <c>appTheme</c> choices, as the page lists them.</summary>
    public IReadOnlyList<AppThemeChoice> AppThemeChoices => Themes;

    /// <summary>
    /// <c>appTheme</c>. Setting it sends the change at once; the App restyles when the engine's
    /// <c>settings.changed</c> comes back (AD-23), so a refused update leaves the theme as it was.
    /// </summary>
    public AppTheme AppTheme
    {
        get => _appTheme;
        set
        {
            if (Set(ref _appTheme, value))
            {
                _ = SendAsync([SettingsValues.SetGlobal(Keys.AppTheme, value)]);
            }
        }
    }

    /// <summary>
    /// <c>captureNewGames</c> (story 13.1): whether a game found for the first time is captured.
    /// Setting it sends the change; existing games keep their own Capture this game.
    /// </summary>
    public bool CaptureNewGames
    {
        get => _captureNewGames;
        set
        {
            if (Set(ref _captureNewGames, value))
            {
                _ = SendAsync([SettingsValues.SetGlobal(Keys.CaptureNewGames, value)]);
            }
        }
    }

    protected override FrameworkElement CreateView() => new GeneralSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _startWithWindows = SettingsValues.Global(document, Keys.StartWithWindows);
        OnPropertyChanged(nameof(StartWithWindows));
        _overlayDot = SettingsValues.Global(document, Keys.OverlayDot);
        OnPropertyChanged(nameof(OverlayDot));
        _appTheme = SettingsValues.Global(document, Keys.AppTheme);
        OnPropertyChanged(nameof(AppTheme));
        _captureNewGames = SettingsValues.Global(document, Keys.CaptureNewGames);
        OnPropertyChanged(nameof(CaptureNewGames));
    }
}

/// <summary>One <c>overlayDot</c> choice on the General page.</summary>
internal sealed record OverlayDotChoice(OverlayDotMode Mode, string Label);

/// <summary>One <c>appTheme</c> choice on the General page.</summary>
internal sealed record AppThemeChoice(AppTheme Theme, string Label);
