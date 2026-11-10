using System.IO;
using System.Windows;
using Encore.App.Engine;
using Encore.App.Library;
using Encore.App.Overlay;
using Encore.App.Theme;
using Encore.Contracts;
using Encore.Platform.Desktop;

namespace Encore.App;

/// <summary>The App: it builds the engine connection, the Main App window, the tray icon and the Overlay, and ends them on Quit.</summary>
internal sealed class ShellApp(Instance instance) : Application
{
    /// <summary>How long Quit waits for the engine to exit, so a pending save can finish.</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private readonly AppLog _log = new(instance.LocalDataFolder);
    private EngineConnection? _engine;
    private StartupRegistration? _startup;
    private MainWindow? _window;
    private OverlayWindow? _overlay;
    private ControlGallery? _gallery;
    private AppTray? _tray;
    private bool _quitting;

    private string ThemeFile => ThemeFileFor(instance);

    /// <summary>Where <paramref name="instance"/>'s App keeps the theme it last applied, for its next start.</summary>
    internal static string ThemeFileFor(Instance instance) => Path.Combine(instance.LocalDataFolder, "app-theme.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Before anything reads its data folders, an Encore instance's first start copies its old ReplayBuffer data once.
        LegacyData.CopyIfFirstStart(instance);

        var theme = ApplyLastTheme();
        _engine = CreateEngine(e.Args);
        FollowStartWithWindows(_engine);
        theme.Follow(_engine, Post, applied => ThemeService.WriteLast(ThemeFile, applied));
        var model = CreateMainViewModel(_engine, e.Args);
        _window = CreateWindow(model);
        _tray = CreateTray(model, _window, _engine);
        _overlay = CreateOverlay(_engine);
        if (!AppArguments.StartsInTray(e.Args))
        {
            _window.Open();
        }

        _engine.StartSupervising();
    }

    // The theme is in place before the first window shows, using the theme last applied until the settings arrive.
    private ThemeService ApplyLastTheme()
    {
        var theme = ThemeService.ForApplication(this);
        ThemeService.Current = theme;
        theme.Apply(ThemeService.ReadLast(ThemeFile));
        return theme;
    }

    private EngineConnection CreateEngine(string[] args)
    {
        var engine = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = instance.Name,
            EnginePath = Path.Combine(AppContext.BaseDirectory, EngineConnectionOptions.EngineFileName),
            EngineArguments = AppArguments.ForEngine(instance, args),
            Log = _log.Write,
        });
        engine.EngineEnded += () => Dispatcher.BeginInvoke(async () => await QuitAsync());
        return engine;
    }

    // The Run value follows the engine's committed settings: the snapshot, then each settings.changed.
    private void FollowStartWithWindows(EngineConnection engine)
    {
        _startup = new StartupRegistration(
            instance,
            RunKey.Startup,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Encore.App.exe"));
        engine.Changed += () => Post(SyncStartup);
    }

    private MainViewModel CreateMainViewModel(EngineConnection engine, string[] args)
    {
        var unsupported = AppArguments.PlayerUnsupported(args, instance);
        return new MainViewModel(
            engine,
            Post,
            Path.Combine(instance.RoamingDataFolder, "library-view.json"),
            Path.Combine(instance.LocalDataFolder, "ring"),
            player: unsupported is null ? null : () => new WindowsMediaPlayer(unsupported),
            log: _log.Write,
            clipThumbnailCacheFolder: Path.Combine(instance.LocalDataFolder, "cache", "clip-thumbnails", "v1"));
    }

    private MainWindow CreateWindow(MainViewModel model)
    {
        var window = new MainWindow { DataContext = model };
        if (instance.Name != Instance.InstalledName)
        {
            window.Title += $" ({instance.Name})";
        }

        return window;
    }

    private AppTray CreateTray(MainViewModel model, MainWindow window, EngineConnection engine)
    {
        var menu = TrayMenu.Create(
            model,
            window.Open,
            async () => await QuitAsync(),
            engine.RestartAfterCrashLoop,
            instance.Name != Instance.InstalledName ? OpenGallery : null);
        var tray = new AppTray(menu, window.Title, window.Open);
        engine.Changed += () => Post(() => tray.Show(engine));
        return tray;
    }

    private OverlayWindow CreateOverlay(EngineConnection engine)
    {
        var overlay = new OverlayViewModel(engine, Post, overlayPlanes: Monitors.OverlayPlanes, background: action => Task.Run(action));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Post(overlay.DisplaysChanged);
        return new OverlayWindow(overlay);
    }

    // Runs an action on the UI thread; the engine's events arrive on background threads.
    private void Post(Action action) => Dispatcher.BeginInvoke(action);

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

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;

        // The Library's player page closes first, so the player stops and the file is released.
        (_window!.DataContext as MainViewModel)?.Library.DrillIn.Close();
        await _engine!.ShutdownAsync(QuitTimeout);
        await _engine.DisposeAsync();
        _tray!.Dispose();
        _overlay!.Close();
        _gallery?.Close();
        _window!.CloseForQuit();
        Shutdown();
    }
}
