using System.Windows.Controls;
using H.NotifyIcon;
using Encore.App.Engine;
using Encore.App.Overlay;

namespace Encore.App;

/// <summary>The App's tray icon: its menu, and the capture state and crash loop it shows.</summary>
internal sealed class AppTray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _restartItem;
    private readonly string _title;
    private OverlayDot _dot;
    private bool _crashLoop;

    /// <summary>The title starts the tooltip; open is the left click, as it is the menu's Open item.</summary>
    public AppTray(ContextMenu menu, string title, Action open)
    {
        _title = title;
        _restartItem = menu.Items.OfType<MenuItem>().Single(item => item.Name == TrayMenu.RestartItemName);
        _icon = new TaskbarIcon
        {
            ToolTipText = $"{title}: {TrayCapture.Describe(OverlayDot.None)}",
            IconSource = TrayCapture.Icon(OverlayDot.None),
            ContextMenu = menu,
            LeftClickCommand = new RelayCommand(open),
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
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
        _icon.IconSource = crashLoop ? TrayCapture.CrashLoopIcon() : TrayCapture.Icon(dot);
        _icon.ToolTipText = $"{_title}: {(crashLoop ? TrayCapture.CrashLoopText : TrayCapture.Describe(dot))}";
    }

    public void Dispose() => _icon.Dispose();
}
