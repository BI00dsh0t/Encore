using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using H.NotifyIcon;
using Encore.App.Engine;
using Encore.App.Library;
using Encore.App.Overlay;
using Encore.App.Theme;
using Encore.Contracts;
using Encore.Platform.Desktop;

namespace Encore.App;

/// <summary>The App: a tray icon, the Main App window and the engine connection. Switches: docs/code-notes.md.</summary>
internal sealed class ShellApp(Instance instance) : Application
{
    public const string TrayArgument = "--tray";
    public const string InstanceArgument = "--instance";
    public const string RewindArgument = "--rewind";

    /// <summary>A dev switch: the player reports that codec as undecodable, to check the missing-extension page.</summary>
    public const string PlayerUnsupportedArgument = "--player-unsupported=";

    /// <summary>The tray menu's Restart item, shown only in <see cref="EngineStatus.CrashLoop"/>.</summary>
    public const string RestartItemName = "RestartItem";

    /// <summary>The tray menu's Control gallery item, which only dev and test instances have.</summary>
    public const string GalleryItemName = "GalleryItem";

    /// <summary>The tray menu's live status item.</summary>
    public const string StatusItemName = "StatusItem";

    /// <summary>How long Quit waits for the engine to exit, so a pending save can finish.</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private EngineConnection? _engine;
    private StartupRegistration? _startup;
    private MainWindow? _window;
    private OverlayWindow? _overlay;
    private ControlGallery? _gallery;
    private ThemeService? _theme;
    private TaskbarIcon? _tray;
    private MenuItem? _restartItem;
    private OverlayDot _trayDot;
    private bool _trayCrashLoop;
    private bool _quitting;
    private readonly Lock _logLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Before anything reads its data folders, an Encore instance's first start copies its old ReplayBuffer data once.
        LegacyData.CopyIfFirstStart(instance);

        // The theme is in place before the first window shows, using the theme last applied until the settings arrive.
        _theme = ThemeService.ForApplication(this);
        ThemeService.Current = _theme;
        _theme.Apply(ThemeService.ReadLast(ThemeFile));

        _engine = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = instance.Name,
            EnginePath = Path.Combine(AppContext.BaseDirectory, EngineConnectionOptions.EngineFileName),
            EngineArguments = !instance.IsTest
                ? ["serve"]
                : WantsRewind(e.Args)
                    ? ["serve", InstanceArgument, instance.Name, RewindArgument]
                    : ["serve", InstanceArgument, instance.Name],
            Log = Log,
        });
        _engine.EngineEnded += () => Dispatcher.BeginInvoke(async () => await QuitAsync());

        // The Run value follows the engine's committed settings: the snapshot, then each settings.changed.
        _startup = new StartupRegistration(
            instance,
            RunKey.Startup,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Encore.App.exe"));
        _engine.Changed += () => Dispatcher.BeginInvoke(SyncStartup);
        _theme.Follow(_engine, action => Dispatcher.BeginInvoke(action), theme => ThemeService.WriteLast(ThemeFile, theme));
        var unsupported = PlayerUnsupported(e.Args, instance);
        var model = new MainViewModel(
            _engine,
            action => Dispatcher.BeginInvoke(action),
            Path.Combine(instance.RoamingDataFolder, "library-view.json"),
            Path.Combine(instance.LocalDataFolder, "ring"),
            player: unsupported is null ? null : () => new WindowsMediaPlayer(unsupported),
            log: Log,
            clipThumbnailCacheFolder: Path.Combine(instance.LocalDataFolder, "cache", "clip-thumbnails", "v1"));
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

    /// <summary>The codec the player-unsupported switch names ("HEVC" or "AV1"), except on the installed instance; else null.</summary>
    internal static string? PlayerUnsupported(IEnumerable<string> args, Instance instance)
    {
        var codec = args.FirstOrDefault(a => a.StartsWith(PlayerUnsupportedArgument, StringComparison.OrdinalIgnoreCase))?[PlayerUnsupportedArgument.Length..];
        return instance.Name == Instance.InstalledName || CodecExtension.For(codec) is not { } extension ? null : extension.Codec;
    }

    /// <summary>Whether the command line passes <c>--rewind</c> on to the engine; valid on a test instance only.</summary>
    internal static bool WantsRewind(IEnumerable<string> args) => args.Contains(RewindArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>The App's command line: the instance and <c>--rewind</c>, both for test instances only. False means exit 2.</summary>
    internal static bool TryParseArguments(IReadOnlyList<string> args, [NotNullWhen(true)] out Instance? instance)
    {
        if (TryParseInstance(args, out instance) && (instance.IsTest || !WantsRewind(args)))
        {
            return true;
        }

        instance = null;
        return false;
    }

    /// <summary>The instance <c>--instance</c> names, which must be a test instance, or the process's own; false when malformed.</summary>
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

    /// <summary>The tray's menu: the live status, the Open items, Start with Windows, a Restart item for a crash loop, and Quit.</summary>
    internal static ContextMenu CreateTrayMenu(MainViewModel model, Action open, Action quit, Action? restart = null, Action? gallery = null, Action<string>? openFolder = null)
    {
        var restartItem = new MenuItem { Header = "Restart", Name = RestartItemName, Visibility = Visibility.Collapsed };
        restartItem.Click += (_, _) => restart?.Invoke();

        // The status header's text, live while the menu is open.
        var statusItem = new MenuItem { Name = StatusItemName, IsEnabled = false };
        statusItem.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(nameof(model.StatusLine.State)) { Source = model.StatusLine, Mode = BindingMode.OneWay });

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => open();

        var libraryItem = new MenuItem { Header = "Open Library" };
        libraryItem.Click += (_, _) =>
        {
            model.SelectedNavItem = model.LibraryNav;
            open();
        };

        var folderItem = new MenuItem { Header = "Open the Clips folder" };
        folderItem.Click += (_, _) =>
        {
            if (model.ClipsFolder is { } folder)
            {
                (openFolder ?? OpenInExplorer)(folder);
            }
        };

        var startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startWithWindows.SetBinding(
            MenuItem.IsCheckedProperty,
            new Binding(nameof(model.General.StartWithWindows)) { Source = model.General, Mode = BindingMode.TwoWay });

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu { Items = { statusItem, new Separator(), openItem, libraryItem, folderItem, new Separator(), startWithWindows, restartItem } };
        if (gallery is not null)
        {
            var galleryItem = new MenuItem { Header = "Control gallery", Name = GalleryItemName };
            galleryItem.Click += (_, _) => gallery();
            menu.Items.Add(galleryItem);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        return menu;
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Nothing to open: the tray has nowhere to say so, and the Library shows the folder.
        }
    }

    /// <summary>Shows the tray menu's Restart item only while the engine is in a crash loop.</summary>
    internal static void ShowCrashLoop(MenuItem restartItem, bool crashLoop) =>
        restartItem.Visibility = crashLoop ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Where <paramref name="instance"/>'s App keeps the theme it last applied, for its next start.</summary>
    internal static string ThemeFileFor(Instance instance) => Path.Combine(instance.LocalDataFolder, "app-theme.json");

    private string ThemeFile => ThemeFileFor(instance);

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

    // The tray carries the capture state in every overlayDot mode, and the engine's crash loop.
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
