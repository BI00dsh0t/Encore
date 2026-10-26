using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using H.NotifyIcon;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App;

/// <summary>
/// The App: a tray icon, the Main App window and the engine connection. It starts in the tray and
/// runs until Quit (AD-16). <c>--tray</c> starts it with no window, as Start with Windows will.
/// </summary>
internal sealed class ShellApp : Application
{
    public const string TrayArgument = "--tray";

    /// <summary>How long Quit waits for the engine to exit, so a pending save can finish (AD-16).</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private EngineConnection? _engine;
    private MainWindow? _window;
    private TaskbarIcon? _tray;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var instance = Instance.Current;
        _engine = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = instance.Name,
            EnginePath = Path.Combine(AppContext.BaseDirectory, "ReplayBuffer.Engine.exe"),
        });
        var model = new MainViewModel(_engine, action => Dispatcher.BeginInvoke(action));
        _window = new MainWindow { DataContext = model };
        if (instance.Name != Instance.InstalledName)
        {
            _window.Title += $" ({instance.Name})";
        }

        _tray = CreateTray(model, _window.Title);
        if (!StartsInTray(e.Args))
        {
            _window.Open();
        }

        _ = _engine.ConnectAsync();
    }

    /// <summary>Whether the command line asks for no window at start.</summary>
    internal static bool StartsInTray(IEnumerable<string> args) => args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tray's menu: Open, Start with Windows and Quit (epic Decision, 2026-10-25).</summary>
    internal static ContextMenu CreateTrayMenu(MainViewModel model, Action open, Action quit)
    {
        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => open();

        var startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startWithWindows.SetBinding(
            MenuItem.IsCheckedProperty,
            new Binding(nameof(model.General.StartWithWindows)) { Source = model.General, Mode = BindingMode.TwoWay });

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu { Items = { openItem, startWithWindows, new Separator(), quitItem } };

        // The menu is a popup with a window of its own; exclude it as soon as it has one (AD-15).
        PresentationSource.AddSourceChangedHandler(menu, (_, e) =>
        {
            if (e.NewSource is HwndSource source)
            {
                CaptureExclusion.Apply(source.Handle);
            }
        });
        return menu;
    }

    private TaskbarIcon CreateTray(MainViewModel model, string title)
    {
        var menu = CreateTrayMenu(model, () => _window!.Open(), async () => await QuitAsync());
        var tray = new TaskbarIcon
        {
            ToolTipText = title,
            IconSource = new GeneratedIconSource
            {
                Text = "R",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                FontWeight = FontWeights.Bold,
            },
            ContextMenu = menu,
            LeftClickCommand = new RelayCommand(() => _window!.Open()),
            NoLeftClickDelay = true,
        };
        tray.ForceCreate(enablesEfficiencyMode: false);
        return tray;
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        await _engine!.ShutdownAsync(QuitTimeout);
        await _engine.DisposeAsync();
        _tray!.Dispose();
        _window!.CloseForQuit();
        Shutdown();
    }
}
