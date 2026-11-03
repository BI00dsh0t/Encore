using System.Windows;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>Global settings with no page of their own. The tray's Start with Windows item binds here too.</summary>
internal sealed class GeneralSettingsPage(SettingsSender sender) : SettingsPage(sender)
{
    private bool _startWithWindows;

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

    protected override FrameworkElement CreateView() => new GeneralSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _startWithWindows = SettingsValues.Global(document, Keys.StartWithWindows);
        OnPropertyChanged(nameof(StartWithWindows));
    }
}
