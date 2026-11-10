using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Encore.App.Engine;
using Encore.App.Overlay;
using Encore.App.Settings;
using Encore.App.Theme;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Engine;
using Encore.Engine.Supervision;

namespace Encore.App.Tests;

/// <summary>The theme end to end, unattended and without the GPU: every surface restyles in place and the theme is kept.</summary>
[Collection(nameof(WpfCollection))]
public sealed partial class ModernUiEndToEndTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly Color GamingBase = Color.FromRgb(0x0A, 0x0B, 0x0E);

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-e2e-theme-").FullName;

    private static ITestOutputHelper? Log => TestContext.Current.TestOutputHelper;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine's log may be released a moment late; the temp folder is left behind.
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TheThemeSwitchesLiveOnEverySurfaceAndSurvivesAnAppRestart() => Sta.Run(() =>
    {
        // A Guid name: a dotted temp-folder name isn't a test instance name, and the engine would start Rewind for real.
        var instance = new Instance(Instance.TestPrefix + "theme-" + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        Assert.True(instance.IsTest);
        var output = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var keys = AppResourceKeys();
        var dispatcher = Dispatcher.CurrentDispatcher;
        Action<Action> post = action => dispatcher.BeginInvoke(action);

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var logs = new List<string>();
        try
        {
            using (var first = Session.Start(instance, _folder, post))
            {
                // A fresh instance opens in Fluent, the default, on every surface.
                Assert.Equal(AppTheme.Fluent, first.Main.General.AppTheme);
                AssertTheme(first, AppTheme.Fluent, keys);

                // A Clip to delete: the smallest file the Library takes as a complete MP4, an ftyp box then an empty moov.
                File.WriteAllBytes(Path.Combine(output, "clip.mp4"), [0, 0, 0, 16, .. "ftypisom"u8, 0, 0, 2, 0, 0, 0, 0, 8, .. "moov"u8]);
                first.Main.LibrarySettings.Confirm = _ => true;
                first.Main.LibrarySettings.ClipsFolder.Choose(output);
                Until(() => first.Engine.State!.LibraryFolder == output, "the chosen Output Folder");
                Until(() => first.Engine.State!.Library.Count == 1, "the Clip listed from the chosen folder");
                Until(() => first.Main.Library.List.Rows.Count == 1, "the Clip's Library row");
                Pump(TimeSpan.FromMilliseconds(300));

                // The row's thumbnail brush traces a binding error while its container is prepared; that trace is filtered below.
                var brush = Assert.Single(Descendants(first.Main.Library.View).OfType<Border>().Select(b => b.Background).OfType<ImageBrush>());
                Assert.Equal(BindingStatus.Active, BindingOperations.GetBindingExpression(brush, ImageBrush.ImageSourceProperty)!.Status);

                // The switch with each page open restyles every surface in place.
                var expected = AppTheme.Fluent;
                foreach (var item in first.Main.NavItems.Where(i => !i.IsSection))
                {
                    first.Main.SelectedNavItem = item;
                    Pump(TimeSpan.FromMilliseconds(50));
                    var view = first.Main.CurrentView;
                    first.OpenTrayMenu();
                    Pump(TimeSpan.FromMilliseconds(200));
                    Assert.True(first.TrayMenu.IsOpen, $"the tray menu closed before the switch on {item.Title}");
                    expected = expected == AppTheme.Fluent ? AppTheme.GamingDark : AppTheme.Fluent;
                    first.Main.General.AppTheme = expected;
                    Until(() => first.Themes.All(t => t.Theme == expected), $"{expected} with {item.Title} open");
                    Pump(TimeSpan.FromMilliseconds(50));
                    AssertTheme(first, expected, keys);
                    Assert.Same(item, first.Main.SelectedNavItem);
                    Assert.Same(view, first.Main.CurrentView);
                    Assert.True(view.IsLoaded, $"{item.Title} didn't stay loaded after the switch to {expected}");
                    Log?.WriteLine($"{item.Title}: {expected}");
                }

                // The switch with the delete confirmation open restyles it in place; No keeps the file.
                first.Main.SelectedNavItem = first.Main.LibraryNav;
                Pump(TimeSpan.FromMilliseconds(50));
                var before = expected;
                var after = before == AppTheme.Fluent ? AppTheme.GamingDark : AppTheme.Fluent;
                var seen = new List<(AppTheme Before, AppTheme After, string Title, bool Visible)>();
                void OnShowing(PromptDialog dialog)
                {
                    var theme = ThemeService.ForWindow(dialog);
                    theme.Apply(first.MainTheme.Theme!.Value);
                    theme.Follow(first.Engine, post);
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    (dialog.Left, dialog.Top, dialog.ShowActivated) = (-30000, -30000, false);
                    dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                    {
                        var was = Shown(dialog);
                        first.Main.General.AppTheme = after;
                        Until(() => theme.Theme == after && first.Themes.All(t => t.Theme == after), "the switch with the dialog open");
                        Pump(TimeSpan.FromMilliseconds(50));
                        AssertSurface(dialog, after, keys, "the dialog");
                        seen.Add((was, Shown(dialog), dialog.Title, dialog.IsVisible));
                        dialog.Choose(false);
                    });
                }

                PromptDialog.Showing += OnShowing;
                try
                {
                    first.Main.Library.Selection.Click(first.Main.Library.List.Rows[0], ctrl: false, shift: false);
                    first.Main.Library.DeleteCommand.Execute(null);
                }
                finally
                {
                    PromptDialog.Showing -= OnShowing;
                }

                Assert.Equal([(before, after, "Delete", true)], seen);
                Assert.True(File.Exists(Path.Combine(output, "clip.mp4")));
                Pump(TimeSpan.FromMilliseconds(300)); // The Main App takes activation back as the dialog closes.
                first.OpenTrayMenu();
                AssertTheme(first, after, keys);
                if (after != AppTheme.GamingDark)
                {
                    first.Main.General.AppTheme = AppTheme.GamingDark;
                    Until(() => first.Themes.All(t => t.Theme == AppTheme.GamingDark), "Gaming dark to keep");
                }

                // No restart: the same connection stayed up, and the App recorded the theme for its next start.
                Assert.Equal(EngineStatus.Connected, first.Engine.Status);
                Assert.Equal(1, first.Connects);
                Assert.Equal(AppTheme.GamingDark, ThemeService.ReadLast(ShellApp.ThemeFileFor(instance)));
                logs.AddRange(first.Logs);
            }

            // Restarted, the App opens in Gaming dark before the engine answers, and the snapshot keeps it.
            using var second = Session.Start(instance, _folder, post);
            Assert.Equal(AppTheme.GamingDark, second.StartTheme);
            Assert.Equal(AppTheme.GamingDark, SettingsValues.Global(second.Engine.State!.Settings, Keys.AppTheme));
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(AppTheme.GamingDark, second.Main.General.AppTheme);
            AssertTheme(second, AppTheme.GamingDark, keys);
            logs.AddRange(second.Logs);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        // Fluent's own container alignment binding is the framework's (see ThemeTests); the thumbnail's is above.
        var found = string.Join("", errors.Messages)
            .Split("System.Windows.Data Error", StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !FluentContainerAlignment().IsMatch(e) && !ThumbnailBeforeAttach().IsMatch(e))
            .ToList();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
        Log?.WriteLine("engine log:\n" + string.Join("\n", logs));
        Assert.DoesNotContain(logs, l => l.StartsWith("Detected", StringComparison.Ordinal) || l.StartsWith("Capturing", StringComparison.Ordinal) || l.Contains("Recording started", StringComparison.Ordinal));
    });

    private static void AssertTheme(Session session, AppTheme theme, IReadOnlySet<string> keys)
    {
        AssertSurface(session.Window, theme, keys, "the Main App");
        AssertSurface(session.Gallery, theme, keys, "the Control gallery");
        AssertSurface(session.Overlay, theme, keys, "the Overlay");
        AssertSurface(session.TrayMenu, theme, keys, "the tray menu");
        Assert.True(session.TrayMenu.IsOpen, "the tray menu closed");
        Assert.True(session.Window.IsVisible && session.Gallery.IsVisible, "a window closed");
        Assert.Equal(theme == AppTheme.GamingDark, ((SolidColorBrush)session.TrayMenu.Background).Color == Color.FromRgb(0x1E, 0x22, 0x2A));
        Assert.Equal(theme, Shown(session.Main.CurrentView));
    }

    private static void AssertSurface(FrameworkElement surface, AppTheme theme, IReadOnlySet<string> keys, string what)
    {
        Assert.True(Shown(surface) == theme, $"{what} isn't in {theme}");
        Assert.All(keys, key => Assert.True(surface.TryFindResource(key) is not null, $"{what} has no {key} under {theme}"));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child))
            {
                yield return d;
            }
        }
    }

    private static AppTheme Shown(FrameworkElement element) =>
        ((SolidColorBrush)element.FindResource("SolidBackgroundFillColorBaseBrush")).Color == GamingBase ? AppTheme.GamingDark : AppTheme.Fluent;

    // Every DynamicResource key in the App's XAML, as ThemeTests scans it.
    private static HashSet<string> AppResourceKeys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Encore.slnx")))
        {
            dir = dir.Parent;
        }

        return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Encore.App"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => DynamicResource().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Where(key => !key.StartsWith('{'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds.</summary>
    private static void Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, $"Timed out waiting for {what}.");
            Pump(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Run(Task task)
    {
        Until(() => task.IsCompleted, "a reply");
        task.GetAwaiter().GetResult();
    }

    private static T Offscreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    [GeneratedRegex(@"AncestorType='System\.Windows\.Controls\.ItemsControl'.*Path=(Horizontal|Vertical)ContentAlignment;.*target element is '(ListBoxItem|ComboBoxItem|MenuItem)'")]
    private static partial Regex FluentContainerAlignment();

    [GeneratedRegex(@"Cannot find governing FrameworkElement.*Path=Thumbnail;.*target element is 'ImageBrush'")]
    private static partial Regex ThumbnailBeforeAttach();

    [GeneratedRegex(@"\{DynamicResource\s+([^}\s]+)\s*\}")]
    private static partial Regex DynamicResource();

    /// <summary>The engine and the App as the real App starts them: the last theme first, then the engine's committed one.</summary>
    private sealed class Session : IDisposable
    {
        private readonly EngineHost _host;
        private readonly ListLoggers _logs;
        private Window _trayHost = null!;

        private Session(EngineHost host, ListLoggers logs, EngineConnection engine)
        {
            _host = host;
            _logs = logs;
            Engine = engine;
        }

        public EngineConnection Engine { get; }

        public MainViewModel Main { get; private set; } = null!;

        public MainWindow Window { get; private set; } = null!;

        public ControlGallery Gallery { get; private set; } = null!;

        public OverlayWindow Overlay { get; private set; } = null!;

        public ContextMenu TrayMenu { get; private set; } = null!;

        public ThemeService MainTheme { get; private set; } = null!;

        public List<ThemeService> Themes { get; } = [];

        public AppTheme StartTheme { get; private set; }

        public int Connects { get; private set; }

        public IReadOnlyList<string> Logs => _logs.Messages;

        public static Session Start(Instance instance, string folder, Action<Action> post)
        {
            var logs = new ListLoggers();
            var host = EngineHost.Start(
                instance,
                logs,
                hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AppLauncher = new StayingLauncher(), Rewind = false });
            var engine = new EngineConnection(new EngineConnectionOptions
            {
                InstanceName = instance.Name,
                EnginePath = Path.Combine(folder, "no-engine.exe"),
                TrustedEnginePath = Environment.ProcessPath!,
            });
            var session = new Session(host, logs, engine);
            var themeFile = ShellApp.ThemeFileFor(instance);
            session.StartTheme = ThemeService.ReadLast(themeFile);

            var main = new MainViewModel(engine, post, Path.Combine(instance.RoamingDataFolder, "library-view.json"));
            var window = Offscreen(new MainWindow { DataContext = main, Width = 960, Height = 600 });
            var gallery = Offscreen(new ControlGallery());
            var overlay = new OverlayWindow(new OverlayViewModel(engine, post));
            // The tray menu's host: in the App it is H.NotifyIcon's, outside the Main App, on the App's resources.
            var trayTarget = new Border { Width = 10, Height = 10 };
            var trayHost = Offscreen(new Window { Content = trayTarget, Width = 100, Height = 100 });
            (session.Main, session.Window, session.Gallery, session.Overlay, session._trayHost) = (main, window, gallery, overlay, trayHost);
            foreach (var surface in new Window[] { window, gallery, overlay, trayHost })
            {
                var theme = ThemeService.ForWindow(surface);
                theme.Apply(session.StartTheme);
                theme.Follow(engine, post, surface == window ? applied => ThemeService.WriteLast(themeFile, applied) : null);
                session.Themes.Add(theme);
            }

            session.MainTheme = session.Themes[0];
            // Counts each time the connection comes up: a second would be a reconnect, not a live restyle.
            var connected = false;
            engine.Changed += () =>
            {
                var now = engine.Status == EngineStatus.Connected;
                if (now && !connected)
                {
                    session.Connects++;
                }

                connected = now;
            };
            window.Show();
            gallery.Show();
            trayHost.Show();
            Run(engine.ConnectAsync());
            Assert.Equal(EngineStatus.Connected, engine.Status);
            Until(() => main.SettingsViewModels.All(p => p.View is not null), "the pages");

            var menu = ShellApp.CreateTrayMenu(main, () => { }, () => { }, gallery: () => { });
            menu.PlacementTarget = trayTarget;
            menu.Placement = PlacementMode.AbsolutePoint;
            (menu.HorizontalOffset, menu.VerticalOffset) = (-30000, -30000);
            session.TrayMenu = menu;
            session.OpenTrayMenu();
            return session;
        }

        /// <summary>Opens the tray menu again: like any menu, it closes when the focus moves, as a nav click moves it.</summary>
        public void OpenTrayMenu()
        {
            TrayMenu.IsOpen = true;
            Pump(TimeSpan.FromMilliseconds(100));
        }

        public void Dispose()
        {
            TrayMenu.IsOpen = false;
            _trayHost.Close();
            Overlay.Close();
            Gallery.Close();
            Window.CloseForQuit();
            try
            {
                Run(Engine.SendAsync(EngineMessages.Shutdown, Empty.Value));
            }
            catch (Exception e) when (e is ErrorCodeException or IOException or OperationCanceledException)
            {
                // The engine may close the pipe before it replies.
            }

            Run(Engine.DisposeAsync().AsTask());
            Run(_host.DisposeAsync().AsTask());
        }
    }

    /// <summary>Keeps every message the engine logs.</summary>
    private sealed class ListLoggers : ILoggerFactory
    {
        private readonly Lock _lock = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _messages];
                }
            }
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(ListLoggers owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lock)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class StayingLauncher : IAppLauncher
    {
        public IWatchedProcess? Open(int pid) => new Staying(pid);

        public IWatchedProcess Start() => new Staying(0);

        private sealed class Staying(int id) : IWatchedProcess
        {
            public int Id { get; } = id;

            public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;

            public void Dispose()
            {
            }
        }
    }
}
