using System.Windows;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>Global settings with no page of their own. The tray's Start with Windows item binds here too.</summary>
internal sealed class GeneralSettingsPage(SettingsSender sender) : SettingsPage(sender)
{
    private bool _startWithWindows;
    private OverlayDotMode _overlayDot = Keys.OverlayDot.Default;

    private static readonly IReadOnlyList<OverlayDotChoice> Choices =
    [
        new(OverlayDotMode.Auto, "Automatic: always, unless it would cost a borderless game FPS"),
        new(OverlayDotMode.Always, "Always, while capture runs"),
        new(OverlayDotMode.OnChange, $"For {(int)OverlayViewModel.DotOnChangeDuration.TotalSeconds} seconds after each change"),
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

    protected override FrameworkElement CreateView() => new GeneralSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _startWithWindows = SettingsValues.Global(document, Keys.StartWithWindows);
        OnPropertyChanged(nameof(StartWithWindows));
        _overlayDot = SettingsValues.Global(document, Keys.OverlayDot);
        OnPropertyChanged(nameof(OverlayDot));
    }
}

/// <summary>One <c>overlayDot</c> choice on the General page.</summary>
internal sealed record OverlayDotChoice(OverlayDotMode Mode, string Label);
