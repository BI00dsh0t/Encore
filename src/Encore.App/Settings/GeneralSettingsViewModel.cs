using System.IO;
using System.Windows;
using Encore.App.Overlay;
using Encore.App.Updates;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Global settings with no page of their own. The tray's Start with Windows item binds here too.</summary>
internal sealed class GeneralSettingsViewModel(SettingsSender sender, AppUpdates? updates = null) : SettingsViewModel(sender)
{
    private bool _startWithWindows;
    private OverlayDotMode _overlayDot = Keys.OverlayDot.Default;
    private AppTheme _appTheme = Keys.AppTheme.Default;
    private bool _captureNewGames = Keys.CaptureNewGames.Default;
    private bool _captureFullscreenApps = Keys.CaptureFullscreenApps.Default;

    public override string Title => "General";

    /// <summary>The About and updates section's update half: the version, the update switches and the restart button.</summary>
    public AppUpdates Updates { get; } = updates ?? AppUpdates.Disabled(Path.Combine(Path.GetTempPath(), "encore-updates.json"));

    /// <summary>The About and updates section's links.</summary>
    public AboutLinks About { get; } = AboutLinks.ForThisApp();

    /// <summary><c>startWithWindows</c>: setting it sends the change; the Run value is written once the engine commits it.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetAndSend(ref _startWithWindows, value, Keys.StartWithWindows);
    }

    /// <summary>The <c>overlayDot</c> choices, as the page lists them.</summary>
    public IReadOnlyList<OverlayDotChoice> OverlayDotChoices { get; } =
    [
        new(OverlayDotMode.Auto, "Automatic: always, unless it would cost a borderless game FPS"),
        new(OverlayDotMode.Always, "Always, while capture runs"),
        new(OverlayDotMode.OnChange, $"For {(int)OverlayViewModel.DotOnChangeDuration.TotalSeconds} seconds after each change"),
    ];

    /// <summary><c>overlayDot</c>: when the Overlay's dot shows. Setting it sends the change.</summary>
    public OverlayDotMode OverlayDot
    {
        get => _overlayDot;
        set => SetAndSend(ref _overlayDot, value, Keys.OverlayDot);
    }

    /// <summary>The <c>appTheme</c> choices, as the page lists them.</summary>
    public IReadOnlyList<AppThemeChoice> AppThemeChoices { get; } =
    [
        new(AppTheme.Fluent, "Fluent (follows Windows)"),
        new(AppTheme.GamingDark, "Gaming dark"),
    ];

    /// <summary><c>appTheme</c>: setting it sends the change; the App restyles only when the engine's commit comes back.</summary>
    public AppTheme AppTheme
    {
        get => _appTheme;
        set => SetAndSend(ref _appTheme, value, Keys.AppTheme);
    }

    /// <summary><c>captureNewGames</c>: whether a game found for the first time is captured; existing games keep their choice.</summary>
    public bool CaptureNewGames
    {
        get => _captureNewGames;
        set => SetAndSend(ref _captureNewGames, value, Keys.CaptureNewGames);
    }

    /// <summary><c>captureFullscreenApps</c>: whether a new app found only in fullscreen is captured at once.</summary>
    public bool CaptureFullscreenApps
    {
        get => _captureFullscreenApps;
        set => SetAndSend(ref _captureFullscreenApps, value, Keys.CaptureFullscreenApps);
    }

    protected override FrameworkElement CreateView() => new GeneralSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        ShowGlobal(ref _startWithWindows, Keys.StartWithWindows, nameof(StartWithWindows));
        ShowGlobal(ref _overlayDot, Keys.OverlayDot, nameof(OverlayDot));
        ShowGlobal(ref _appTheme, Keys.AppTheme, nameof(AppTheme));
        ShowGlobal(ref _captureNewGames, Keys.CaptureNewGames, nameof(CaptureNewGames));
        ShowGlobal(ref _captureFullscreenApps, Keys.CaptureFullscreenApps, nameof(CaptureFullscreenApps));
    }
}
