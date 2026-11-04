using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using H.NotifyIcon;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App;

/// <summary>
/// The App: a tray icon, the Main App window and the engine connection, which supervises the
/// engine. It starts in the tray and runs until Quit, or until the engine ends cleanly on its own
/// (AD-16). <c>--tray</c> starts it with no window, as Start with Windows and the engine's relaunch
/// do; <c>--instance ReplayBuffer-Test-*</c> runs it on a test instance, and <c>--replay-buffer</c> (test
/// instances only) passes the engine's own switch on, so a test instance's engine buffers and saves.
/// An engine crash loop puts the tray in its error state, with a Restart item, and goes to
/// <c>logs\app.log</c> in the instance's local data folder.
/// </summary>
internal sealed class ShellApp(Instance instance) : Application
{
    public const string TrayArgument = "--tray";
    public const string InstanceArgument = "--instance";
    public const string ReplayBufferArgument = "--replay-buffer";

    /// <summary>The tray menu's Restart item, shown only in <see cref="EngineStatus.CrashLoop"/>.</summary>
    public const string RestartItemName = "RestartItem";

    /// <summary>The tray menu's Control gallery item, which only dev and test instances have.</summary>
    public const string GalleryItemName = "GalleryItem";

    /// <summary>How long Quit waits for the engine to exit, so a pending save can finish (AD-16).</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private EngineConnection? _engine;
    private StartupRegistration? _startup;
    private MainWindow? _window;
    private OverlayWindow? _overlay;
    private ControlGallery? _gallery;
    private ThemeService? _theme;
    private TaskbarIcon? _tray;
    private MenuItem? _restartItem;
    private IDisposable? _captureExclusion;
    private OverlayDot _trayDot;
    private bool _trayCrashLoop;
    private bool _quitting;
    private readonly Lock _logLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before any window exists: every window of the process, popups and native dialogs included,
        // is excluded from capture as it appears (AD-15).
        _captureExclusion = CaptureExclusion.ExcludeNewWindows();
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // AD-23: the theme is in place before the first window shows. The engine's settings come later, so
        // the start uses the theme last applied, and each settings.changed applies the committed one.
        _theme = ThemeService.ForApplication(this);
        ThemeService.Current = _theme;
        _theme.Apply(ThemeService.ReadLast(ThemeFile));

        _engine = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = instance.Name,
            EnginePath = Path.Combine(AppContext.BaseDirectory, EngineConnectionOptions.EngineFileName),
            EngineArguments = !instance.IsTest
                ? ["serve"]
                : WantsReplayBuffer(e.Args)
                    ? ["serve", InstanceArgument, instance.Name, ReplayBufferArgument]
                    : ["serve", InstanceArgument, instance.Name],
            Log = Log,
        });
        _engine.EngineEnded += () => Dispatcher.BeginInvoke(async () => await QuitAsync());

        // The Run value follows the engine's committed settings: the snapshot, then each settings.changed.
        _startup = new StartupRegistration(
            instance,
            RunKey.Startup,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ReplayBuffer.App.exe"));
        _engine.Changed += () => Dispatcher.BeginInvoke(SyncStartup);
        _theme.Follow(_engine, action => Dispatcher.BeginInvoke(action), theme => ThemeService.WriteLast(ThemeFile, theme));
        var model = new MainViewModel(
            _engine,
            action => Dispatcher.BeginInvoke(action),
            Path.Combine(instance.RoamingDataFolder, "library-view.json"));
        _window = new MainWindow { DataContext = model };
        if (instance.Name != Instance.InstalledName)
        {
            _window.Title += $" ({instance.Name})";
        }

        _tray = CreateTray(model, _window.Title);
        _engine.Changed += () => Dispatcher.BeginInvoke(SyncTray);
        var overlay = new OverlayViewModel(_engine, action => Dispatcher.BeginInvoke(action), overlayPlanes: Monitors.OverlayPlanes, background: action => Task.Run(action));
        _overlay = new OverlayWindow(overlay);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(overlay.DisplaysChanged);
        if (!StartsInTray(e.Args))
        {
            _window.Open();
        }

        _engine.StartSupervising();
    }

    /// <summary>Whether the command line asks for no window at start.</summary>
    internal static bool StartsInTray(IEnumerable<string> args) => args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the command line passes <c>--replay-buffer</c> on to the engine; valid on a test instance only.</summary>
    internal static bool WantsReplayBuffer(IEnumerable<string> args) => args.Contains(ReplayBufferArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The App's command line: <see cref="TryParseInstance"/>, and <c>--replay-buffer</c>, which, like the
    /// engine's, is for test instances only. False means exit 2.
    /// </summary>
    internal static bool TryParseArguments(IReadOnlyList<string> args, [NotNullWhen(true)] out Instance? instance)
    {
        if (TryParseInstance(args, out instance) && (instance.IsTest || !WantsReplayBuffer(args)))
        {
            return true;
        }

        instance = null;
        return false;
    }

    /// <summary>
    /// The instance the command line names with <c>--instance</c>, which must be a test instance (the
    /// engine's rule), or the process's own. False when <c>--instance</c> is malformed.
    /// </summary>
    internal static bool TryParseInstance(IReadOnlyList<string> args, [NotNullWhen(true)] out Instance? instance)
    {
        instance = Instance.Current;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], InstanceArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count || !Instance.IsTestName(args[i + 1]))
            {
                instance = null;
                return false;
            }

            instance = Instance.ForTest(args[++i]);
        }

        return true;
    }

    /// <summary>
    /// The tray's menu: Open, Start with Windows and Quit (epic Decision, 2026-10-25), and a hidden
    /// Restart item, named <see cref="RestartItemName"/>, shown only while the engine is in a crash loop.
    /// With <paramref name="gallery"/>, which dev and test instances pass, it has a Control gallery item too.
    /// </summary>
    internal static ContextMenu CreateTrayMenu(MainViewModel model, Action open, Action quit, Action? restart = null, Action? gallery = null)
    {
        var restartItem = new MenuItem { Header = "Restart", Name = RestartItemName, Visibility = Visibility.Collapsed };
        restartItem.Click += (_, _) => restart?.Invoke();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => open();

        var startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startWithWindows.SetBinding(
            MenuItem.IsCheckedProperty,
            new Binding(nameof(model.General.StartWithWindows)) { Source = model.General, Mode = BindingMode.TwoWay });

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu { Items = { restartItem, openItem, startWithWindows } };
        if (gallery is not null)
        {
            var galleryItem = new MenuItem { Header = "Control gallery", Name = GalleryItemName };
            galleryItem.Click += (_, _) => gallery();
            menu.Items.Add(galleryItem);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

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

    /// <summary>Shows the tray menu's Restart item only while the engine is in a crash loop.</summary>
    internal static void ShowCrashLoop(MenuItem restartItem, bool crashLoop) =>
        restartItem.Visibility = crashLoop ? Visibility.Visible : Visibility.Collapsed;

    private string ThemeFile => Path.Combine(instance.LocalDataFolder, "app-theme.json");

    private void OpenGallery()
    {
        if (_gallery is null)
        {
            _gallery = new ControlGallery();
            _gallery.Closed += (_, _) => _gallery = null;
        }

        _gallery.Show();
        _gallery.Activate();
    }

    private void SyncStartup()
    {
        if (_engine?.State?.Settings is { } settings)
        {
            _startup!.Sync(settings);
        }
    }

    private TaskbarIcon CreateTray(MainViewModel model, string title)
    {
        var menu = CreateTrayMenu(
            model,
            () => _window!.Open(),
            async () => await QuitAsync(),
            () => _engine!.RestartAfterCrashLoop(),
            instance.Name != Instance.InstalledName ? OpenGallery : null);
        _restartItem = menu.Items.OfType<MenuItem>().Single(i => i.Name == RestartItemName);
        var tray = new TaskbarIcon
        {
            ToolTipText = $"{title}: {TrayCapture.Describe(OverlayDot.None)}",
            IconSource = TrayCapture.Icon(OverlayDot.None),
            ContextMenu = menu,
            LeftClickCommand = new RelayCommand(() => _window!.Open()),
            NoLeftClickDelay = true,
        };
        tray.ForceCreate(enablesEfficiencyMode: false);
        return tray;
    }

    // The tray carries the capture state in every overlayDot mode (story 5.15), and the engine's crash loop.
    private void SyncTray()
    {
        var dot = TrayCapture.For(_engine!);
        var crashLoop = _engine!.Status == EngineStatus.CrashLoop;
        if (_tray is null || (dot == _trayDot && crashLoop == _trayCrashLoop))
        {
            return;
        }

        _trayDot = dot;
        _trayCrashLoop = crashLoop;
        ShowCrashLoop(_restartItem!, crashLoop);
        _tray.IconSource = crashLoop ? TrayCapture.CrashLoopIcon() : TrayCapture.Icon(dot);
        _tray.ToolTipText = $"{_window!.Title}: {(crashLoop ? TrayCapture.CrashLoopText : TrayCapture.Describe(dot))}";
    }

    /// <summary>Writes a timestamped line to <c>logs\app.log</c> in the instance's local data folder. Any thread.</summary>
    private void Log(string message)
    {
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}";
        try
        {
            var folder = Path.Combine(instance.LocalDataFolder, "logs");
            Directory.CreateDirectory(folder);
            lock (_logLock)
            {
                File.AppendAllText(Path.Combine(folder, "app.log"), line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is best effort; the status line carries the same text.
        }
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
        _overlay!.Close();
        _gallery?.Close();
        _window!.CloseForQuit();
        _captureExclusion?.Dispose();
        Shutdown();
    }
}
