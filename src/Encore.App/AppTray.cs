using System.Drawing;
using System.Windows.Controls;
using H.NotifyIcon;
using Microsoft.Win32;
using Encore.App.Engine;
using Encore.App.Overlay;
using Encore.Platform.Desktop;

namespace Encore.App;

/// <summary>The App's tray icon: its menu, and the capture state and crash loop it shows.</summary>
internal sealed class AppTray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _restartItem;
    private readonly string _title;
    private OverlayDot _dot;
    private bool _crashLoop;
    private string _iconName = "";
    private int _iconSize;

    /// <summary>The title starts the tooltip; open is the left click, as it is the menu's Open item.</summary>
    public AppTray(ContextMenu menu, string title, Action open)
    {
        _title = title;
        _restartItem = menu.Items.OfType<MenuItem>().Single(item => item.Name == TrayMenu.RestartItemName);
        _icon = new TaskbarIcon
        {
            ToolTipText = $"{title}: {TrayCapture.Describe(OverlayDot.None)}",
            ContextMenu = menu,
            LeftClickCommand = new RelayCommand(open),
            NoLeftClickDelay = true,
        };
        ShowIcon();
        _icon.ForceCreate(enablesEfficiencyMode: false);
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
    }

    /// <summary>Shows the engine's capture state, in every overlayDot mode, and its crash loop. UI thread.</summary>
    public void Show(IEngineSession engine)
    {
        var dot = TrayCapture.For(engine);
        var crashLoop = engine.Status == EngineStatus.CrashLoop;
        if (dot == _dot && crashLoop == _crashLoop)
        {
            return;
        }

        _dot = dot;
        _crashLoop = crashLoop;
        TrayMenu.ShowCrashLoop(_restartItem, crashLoop);
        ShowIcon();
        _icon.ToolTipText = $"{_title}: {(crashLoop ? TrayCapture.CrashLoopText : TrayCapture.Describe(dot))}";
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
        var icon = _icon.Icon;
        _icon.Dispose();
        icon?.Dispose();
    }

    // A taskbar theme, high contrast or scaling change can need another icon; SystemEvents may raise off the UI thread.
    private void OnSystemChanged(object? sender, EventArgs e) => _icon.Dispatcher.BeginInvoke(ShowIcon);

    private void ShowIcon()
    {
        var name = TrayCapture.IconName(_dot, _crashLoop, TaskbarLook.Current());
        var size = TrayIconSize.Current();
        if (name == _iconName && size == _iconSize)
        {
            return;
        }

        _iconName = name;
        _iconSize = size;
        Icon? previous = _icon.Icon;
        _icon.Icon = TrayIcons.Load(name, size);
        previous?.Dispose();
    }
}
