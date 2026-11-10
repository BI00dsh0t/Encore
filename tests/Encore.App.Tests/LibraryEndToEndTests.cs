using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Encore.App.Engine;
using Encore.App.Library;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Engine;
using Encore.Engine.Supervision;

namespace Encore.App.Tests;

/// <summary>The Library end to end, unattended and without the GPU: the whole engine in-process and the App's Library page.</summary>
[Collection(nameof(WpfCollection))]
public sealed class LibraryEndToEndTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-e2e-library-").FullName;
    private readonly List<string> _recycled = [];

    private static ITestOutputHelper? Log => TestContext.Current.TestOutputHelper;

    public void Dispose()
    {
        foreach (var path in _recycled)
        {
            Restore(path);
        }

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
    public void TheLibraryAndCleanupWorkEndToEndOnTheRealEngineAndPages() => Sta.Run(() =>
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")), "native/ffmpeg is not fetched");
        var output = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_folder, "outside")).FullName;
        var two = Generate("two.mp4", 2);
        var three = Generate("three.mp4", 3);
        var now = DateTime.Now;
        Place(two, output, @"Alpha\a-old.mp4", now.AddDays(-40));
        Place(three, output, @"Alpha\a-fav.mp4", now.AddDays(-20));
        Place(two, output, @"Beta\b-new.mp4", now);
        Place(two, output, @"Beta\b-rename.mp4", now.AddDays(-3));
        Place(two, output, @"Beta\b-gone.mp4", now.AddDays(-3));
        var victim = Place(two, outside, "victim.mp4", now.AddDays(-100));
        Junction(Path.Combine(output, "Linked"), outside);

        // A Guid name: a dotted temp-folder name isn't a test instance name, and the engine would start Rewind for real.
        var instance = new Instance(Instance.TestPrefix + "library-" + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        Assert.True(instance.IsTest);
        var dispatcher = Dispatcher.CurrentDispatcher;
        Action<Action> post = action => dispatcher.BeginInvoke(action);

        // The Output Folder chosen on the settings page; the scan lists the five files and not the junction's.
        using (var first = Session.Start(instance, _folder, post, new ShiftedTime()))
        {
            var settings = first.Main.LibrarySettings;
            var prompts = new List<string>();
            settings.Confirm = text =>
            {
                prompts.Add(text);
                return true;
            };
            settings.ClipsFolder.Choose(output);
            Assert.Contains("Existing files aren't moved.", Assert.Single(prompts), StringComparison.Ordinal);
            Until(() => first.Engine.State!.LibraryFolder == output && first.Engine.State.Library.Count == 5, "the five files listed from the chosen folder");
            Assert.Equal(output, settings.ClipsFolder.Value);
            Assert.DoesNotContain(first.Engine.State!.Library, i => i.Path.Contains("victim", StringComparison.OrdinalIgnoreCase));
        }

        // Neither copy of the index parses; it is rebuilt from the folder and Cleanup is off until acknowledged.
        var libraryFile = Path.Combine(instance.RoamingDataFolder, "library.json");
        File.WriteAllText(libraryFile, "{ not json");
        File.WriteAllText(libraryFile + ".bak", "{ not json either");
        var time = new ShiftedTime();
        using var session = Session.Start(instance, _folder, post, time);
        var engine = session.Engine;
        var viewModel = session.ViewModel;
        var shell = session.Shell;
        Until(() => engine.State!.Library.Count == 5 && viewModel.Rows.Count == 5, "the rebuilt index");
        Assert.True(engine.State!.CleanupBlocked);
        Assert.Contains(engine.State.Warnings, w => w.Code == ErrorCodes.LibraryCorrupt);
        Assert.True(viewModel.IsCleanupBlocked);
        Assert.NotNull(session.Main.LibrarySettings.CleanupBlockedText);

        // Each row's length from the engine and its thumbnail from the shell.
        Until(() => viewModel.Rows.All(r => r.Length != "" && r.HasThumbnail), "every row's length and thumbnail");
        Assert.Equal("0:03", Row(viewModel, @"Alpha\a-fav.mp4").Length);
        Assert.Equal("0:02", Row(viewModel, @"Beta\b-new.mp4").Length);
        Log?.WriteLine("rows: " + string.Join("; ", viewModel.Rows.Select(r => $"{r.Path} {r.Game} {r.Date} {r.Length} {r.Size}")));

        // Grouped by game and by day, and filtered by game, date and Favourite.
        viewModel.GroupBy = LibraryGrouping.Game;
        Assert.Equal(["Alpha", "Beta"], Headers(viewModel));
        viewModel.SelectedGame = viewModel.Games.Single(g => g.Name == "Alpha");
        Assert.Equal([@"Alpha\a-fav.mp4", @"Alpha\a-old.mp4"], Shown(viewModel));
        viewModel.SelectedGame = viewModel.Games[0];
        viewModel.DateFilter = LibraryDateFilter.Today;
        Assert.Equal([@"Beta\b-new.mp4"], Shown(viewModel));
        viewModel.DateFilter = LibraryDateFilter.Last7Days;
        Assert.Equal(3, Shown(viewModel).Count);
        viewModel.DateFilter = LibraryDateFilter.Last30Days;
        Assert.Equal(4, Shown(viewModel).Count);
        viewModel.DateFilter = LibraryDateFilter.All;
        viewModel.GroupBy = LibraryGrouping.Day;
        Assert.Equal(4, Headers(viewModel).Count);
        Assert.Equal("Today", Headers(viewModel)[0]);

        // Favourite kept in the index; the Favourites filter shows only it.
        Row(viewModel, @"Alpha\a-fav.mp4").FavouriteCommand.Execute(null);
        Row(viewModel, @"Beta\b-rename.mp4").FavouriteCommand.Execute(null);
        Until(() => engine.State!.Library.Count(i => i.Favourite) == 2 && viewModel.Rows.Count(r => r.IsFavourite) == 2, "two Favourites");
        viewModel.FavouritesOnly = true;
        Assert.Equal([@"Beta\b-rename.mp4", @"Alpha\a-fav.mp4"], Shown(viewModel));
        viewModel.FavouritesOnly = false;

        // A rename outside the app keeps the Favourite; a delete outside removes; a copy in adds.
        File.Move(Path.Combine(output, @"Beta\b-rename.mp4"), Path.Combine(output, @"Beta\b-moved.mp4"));
        File.Delete(Path.Combine(output, @"Beta\b-gone.mp4"));
        File.Copy(two, Path.Combine(output, @"Beta\b-copied.mp4"));
        Until(
            () => Paths(engine) is var p && p.Contains(@"Beta\b-moved.mp4") && p.Contains(@"Beta\b-copied.mp4") && !p.Contains(@"Beta\b-rename.mp4") && !p.Contains(@"Beta\b-gone.mp4"),
            "the outside rename, delete and copy reflected");
        Assert.True(Item(engine, @"Beta\b-moved.mp4").Favourite);
        Until(() => viewModel.Rows.Count == 5, "the rows following");

        // Open, Show in Explorer and Copy pass the absolute path to the shell; Copy's data is one file drop.
        viewModel.Click(Row(viewModel, @"Alpha\a-old.mp4"), ctrl: false, shift: false);
        var oldPath = Path.Combine(output, @"Alpha\a-old.mp4");
        viewModel.OpenCommand.Execute(null);
        viewModel.ShowInExplorerCommand.Execute(null);
        viewModel.CopyCommand.Execute(null);
        Assert.Equal([("open", oldPath), ("show", oldPath), ("copy", oldPath)], shell.Calls);
        Assert.Equal([oldPath], LibraryClipboard.Create([oldPath]).GetFileDropList().Cast<string>());
        Assert.Null(viewModel.ActionError);

        // Rename in place (the selection follows it); delete asks, No keeps the file, Yes sends it to the Recycle Bin.
        var copied = Row(viewModel, @"Beta\b-copied.mp4");
        viewModel.Click(copied, ctrl: false, shift: false);
        viewModel.RenameCommand.Execute(null);
        copied.RenameText = "b-best";
        copied.CommitRenameCommand.Execute(null);
        Until(() => Paths(engine).Contains(@"Beta\b-best.mp4") && viewModel.Rows.Any(r => r.Path == @"Beta\b-best.mp4"), "the rename");
        Assert.True(Row(viewModel, @"Beta\b-best.mp4").IsSelected);
        var best = Path.Combine(output, @"Beta\b-best.mp4");
        Assert.True(File.Exists(best));
        session.Answer = false;
        viewModel.DeleteCommand.Execute(null);
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.True(File.Exists(best));
        session.Answer = true;
        _recycled.Add(best);
        viewModel.DeleteCommand.Execute(null);
        Until(() => !Paths(engine).Contains(@"Beta\b-best.mp4") && !File.Exists(best), "the delete to the Recycle Bin");
        Assert.Equal(["Move “b-best.mp4” to the Recycle Bin?", "Move “b-best.mp4” to the Recycle Bin?"], session.Asked);
        Assert.Null(viewModel.ActionError);

        // A growing file being written is locked: no Copy, Rename or Delete, and the engine refuses a rename.
        var recording = Path.Combine(output, @"Beta\rec.mp4");
        using var growing = new FileStream(recording, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        session.Host.Library.BeginWrite(recording, null);
        growing.Write(new byte[4096]);
        growing.Flush();
        Until(() => viewModel.Rows.FirstOrDefault(r => r.Path == @"Beta\rec.mp4") is { IsLocked: true }, "the locked row");
        viewModel.Click(Row(viewModel, @"Beta\rec.mp4"), ctrl: false, shift: false);
        Assert.False(viewModel.CopyCommand.CanExecute(null));
        Assert.False(viewModel.RenameCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCommand.CanExecute(null));
        var recItem = Item(engine, @"Beta\rec.mp4");
        var refused = Assert.Throws<ErrorCodeException>(() => Run(engine.SendAsync(LibraryMessages.Rename, new LibraryRename(recItem.Path, recItem.Size, recItem.Modified, "x"), TestContext.Current.CancellationToken)));
        Assert.Equal(ErrorCodes.FileBeingWritten, refused.Code);
        growing.Write(new byte[4096]);
        growing.Flush();

        // Past the 60 s start delay, a size limit is previewed and saved, but nothing goes while the index is unacknowledged.
        time.Offset = TimeSpan.FromSeconds(61);
        var settingsViewModel = session.Main.LibrarySettings;
        var limitPrompts = new List<string>();
        settingsViewModel.Confirm = text =>
        {
            limitPrompts.Add(text);
            return true;
        };
        var total = engine.State!.Library.Sum(i => i.Size);
        var oldSize = Item(engine, @"Alpha\a-old.mp4").Size;
        settingsViewModel.SizeLimitText = Gb(total - oldSize + 1000);
        settingsViewModel.SizeLimitEnabled = true;
        Until(() => engine.State!.Settings.Global.ContainsKey(Contracts.Settings.Keys.Cleanup.Name) && settingsViewModel.SizeLimitEnabled, "the size limit saved");
        Assert.Contains("Cleanup would delete 1 file", Assert.Single(limitPrompts), StringComparison.Ordinal);
        Pump(TimeSpan.FromSeconds(1.5));
        Assert.True(File.Exists(oldPath), "Cleanup ran while library-corrupt was unacknowledged.");

        // Acknowledged: Cleanup deletes the oldest non-Favourite only, and logs its plan and each delete.
        viewModel.AcknowledgeCommand.Execute(null);
        Until(() => !engine.State!.CleanupBlocked && !viewModel.IsCleanupBlocked, "Cleanup unblocked");
        Until(() => !File.Exists(oldPath) && !Paths(engine).Contains(@"Alpha\a-old.mp4"), "the oldest non-Favourite cleaned up");
        Assert.True(File.Exists(Path.Combine(output, @"Beta\b-new.mp4")));
        Assert.True(File.Exists(Path.Combine(output, @"Alpha\a-fav.mp4")));
        Until(() => session.Logs.Any(l => l.Contains(@"Cleanup deleted Alpha\a-old.mp4", StringComparison.Ordinal)), "the Cleanup log");
        Assert.Contains(session.Logs, l => l.StartsWith("Cleanup plan: 1 files", StringComparison.Ordinal));

        // Lowered to one byte: everything goes except Favourites, the file being written and the junction's target.
        settingsViewModel.SizeLimitText = Gb(1);
        Until(() => limitPrompts.Count == 2, "the second preview prompt");
        Assert.Contains("Cleanup would delete 1 file", limitPrompts[^1], StringComparison.Ordinal);
        Until(() => !File.Exists(Path.Combine(output, @"Beta\b-new.mp4")), "the newer non-Favourite cleaned up");
        Until(() => engine.State!.Warnings.Any(w => w.Code == ErrorCodes.CleanupLimitUnreachable), "cleanup-limit-unreachable");
        Assert.True(File.Exists(Path.Combine(output, @"Alpha\a-fav.mp4")));
        Assert.True(File.Exists(Path.Combine(output, @"Beta\b-moved.mp4")));
        Assert.True(File.Exists(recording));
        Assert.True(File.Exists(victim));
        Assert.Equal([@"Alpha\a-fav.mp4", @"Beta\b-moved.mp4", @"Beta\rec.mp4"], Paths(engine).Order(StringComparer.Ordinal));
        Log?.WriteLine("engine log:\n" + string.Join("\n", session.Logs));
        Assert.DoesNotContain(session.Logs, l => l.StartsWith("Capturing", StringComparison.Ordinal) || l.StartsWith("Detected", StringComparison.Ordinal));

        // The folder size follows the snapshot.
        Assert.Contains("in 3 files", settingsViewModel.FolderSizeText, StringComparison.Ordinal);
        Assert.Null(viewModel.ActionError);
    });

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);

    private static LibraryRow Row(LibraryViewModel viewModel, string path) => viewModel.Rows.Single(r => r.Path == path);

    private static IReadOnlyList<string> Shown(LibraryViewModel viewModel) => [.. viewModel.Entries.OfType<LibraryRow>().Select(r => r.Path)];

    private static IReadOnlyList<string> Headers(LibraryViewModel viewModel) => [.. viewModel.Entries.OfType<LibraryGroupHeader>().Select(h => h.Name)];

    private static HashSet<string> Paths(EngineConnection engine) => engine.State!.Library.Select(i => i.Path).ToHashSet(StringComparer.Ordinal);

    private static LibraryItem Item(EngineConnection engine, string path) => engine.State!.Library.Single(i => i.Path == path);

    private string Generate(string name, int seconds)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "made")).FullName, name);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-movflags", "+faststart", path,
        })
        {
            start.ArgumentList.Add(arg);
        }

        using var ffmpeg = Process.Start(start)!;
        var errors = ffmpeg.StandardError.ReadToEnd();
        ffmpeg.WaitForExit();
        Assert.True(ffmpeg.ExitCode == 0, "ffmpeg failed: " + errors);
        return path;
    }

    private static string Place(string source, string root, string relative, DateTime created)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(source, path);
        File.SetCreationTime(path, created);
        File.SetLastWriteTime(path, created);
        return path;
    }

    private static void Junction(string at, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{at}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Directory.Exists(at), "mklink /J didn't create the junction.");
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

    private static T Run<T>(Task<T> task)
    {
        Until(() => task.IsCompleted, "a reply");
        return task.GetAwaiter().GetResult();
    }

    private static void Run(Task task)
    {
        Until(() => task.IsCompleted, "a reply");
        task.GetAwaiter().GetResult();
    }

    // Takes a file recycled from path back out of the user's real Recycle Bin (as RecycleBinTests does).
    private static void Restore(string path)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", sid);
            foreach (var info in Directory.EnumerateFiles(bin, "$I*").Where(i => string.Equals(OriginalPath(i), path, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var data = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(data, path, overwrite: true);
                File.Delete(info);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.WriteLine($"Couldn't restore {path} from the Recycle Bin: {e.Message}");
        }
    }

    // 8-byte version, 8-byte size, 8-byte FILETIME, 4-byte char count, then the UTF-16 path.
    private static string? OriginalPath(string info)
    {
        try
        {
            var bytes = File.ReadAllBytes(info);
            if (bytes.Length < 28 || BitConverter.ToInt64(bytes, 0) != 2)
            {
                return null;
            }

            var chars = BitConverter.ToInt32(bytes, 24);
            return Encoding.Unicode.GetString(bytes, 28, Math.Min(chars * 2, bytes.Length - 28)).TrimEnd('\0');
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The engine, the App's connection, a Library page on a recording shell, and the main view model.</summary>
    private sealed class Session : IDisposable
    {
        private Session(EngineHost host, EngineConnection engine, LibraryViewModel viewModel, MainViewModel main, RecordingShell shell, ListLoggers logs)
        {
            Host = host;
            Engine = engine;
            ViewModel = viewModel;
            Main = main;
            Shell = shell;
            LogsSource = logs;
        }

        public EngineHost Host { get; }

        public EngineConnection Engine { get; }

        public LibraryViewModel ViewModel { get; }

        public MainViewModel Main { get; }

        public RecordingShell Shell { get; }

        public bool Answer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public IReadOnlyList<string> Logs => LogsSource.Messages;

        private ListLoggers LogsSource { get; }

        public static Session Start(Instance instance, string folder, Action<Action> post, TimeProvider time)
        {
            var logs = new ListLoggers();
            var host = EngineHost.Start(
                instance,
                logs,
                hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AppLauncher = new StayingLauncher(), Rewind = false, Time = time });
            var engine = new EngineConnection(new EngineConnectionOptions
            {
                InstanceName = instance.Name,
                EnginePath = Path.Combine(folder, "no-engine.exe"),
                TrustedEnginePath = Environment.ProcessPath!,
            });
            Run(engine.ConnectAsync());
            Assert.Equal(EngineStatus.Connected, engine.Status);

            var shell = new RecordingShell();
            Session? session = null;
            var viewModel = new LibraryViewModel(engine, LibraryThumbnails.Shell(post), shell: shell, confirmDelete: question =>
            {
                session!.Asked.Add(question);
                return session.Answer;
            });
            var main = new MainViewModel(engine, post);
            session = new Session(host, engine, viewModel, main, shell, logs);
            engine.Changed += () => post(() =>
            {
                if (engine.ConnectedState() is { } state)
                {
                    viewModel.Render(state);
                }
            });
            viewModel.Render(engine.State!);
            return session;
        }

        public void Dispose()
        {
            try
            {
                Run(Engine.SendAsync(EngineMessages.Shutdown, Empty.Value));
            }
            catch (Exception e) when (e is ErrorCodeException or IOException or OperationCanceledException)
            {
                // The engine may close the pipe before it replies.
            }

            Run(Engine.DisposeAsync().AsTask());
            Run(Host.DisposeAsync().AsTask());
        }
    }

    /// <summary>The system clock moved on by <see cref="Offset"/>, so Cleanup's 60 s start delay can pass at once.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    /// <summary>Records each shell call; launches nothing.</summary>
    private sealed class RecordingShell : ILibraryShell
    {
        public List<(string Action, string Path)> Calls { get; } = [];

        public void Open(string path) => Calls.Add(("open", path));

        public void ShowInExplorer(string folder, IReadOnlyList<string> files) => Calls.Add(("show", string.Join('|', files)));

        public void OpenFolder(string folder) => Calls.Add(("folder", folder));

        public void Copy(IReadOnlyList<string> paths) => Calls.Add(("copy", string.Join('|', paths)));

        public void CopyToFolder(IReadOnlyList<string> paths, string folder) => Calls.Add(("copy to " + folder, string.Join('|', paths)));
    }

    /// <summary>Keeps every message the engine's Library category logs.</summary>
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
