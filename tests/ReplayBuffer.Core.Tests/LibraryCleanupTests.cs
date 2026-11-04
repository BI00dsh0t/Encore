using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Library;

namespace ReplayBuffer.Core.Tests;

/// <summary>Cleanup (story 7.8) in the LibraryActor, on a manual clock, over the test's own temp folder.</summary>
public sealed class LibraryCleanupTests : IDisposable
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ToPeriodic = TimeSpan.FromMinutes(9);
    private static readonly CleanupSettings Off = new(null, null);
    private static readonly CleanupSettings OneHour = new(null, 3600);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly Lock _lock = new();
    private readonly List<LibraryItemRemoved> _removed = [];
    private readonly List<string> _log = [];
    private readonly List<string> _unreachable = [];
    private CleanupSettings _limits = Off;
    private Action<string>? _onLog;
    private LibraryActor? _actor;

    public LibraryCleanupTests() => Directory.CreateDirectory(Output);

    public enum Gate
    {
        Early,
        Blocked,
        Rescan,
    }

    private string Output => Path.Combine(_folder, "out");

    private LibraryActor Actor => _actor!;

    private CleanupSettings Limits
    {
        get => Volatile.Read(ref _limits);
        set => Volatile.Write(ref _limits, value);
    }

    private List<string> Deleted
    {
        get
        {
            lock (_lock)
            {
                Assert.All(_removed, r => Assert.Equal(LibraryRemovalReasons.Cleanup, r.Reason));
                return [.. _removed.Select(r => r.Path)];
            }
        }
    }

    private List<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    public void Dispose()
    {
        _actor?.Dispose();

        // The link alone, so the recursive delete can't reach through it.
        var junction = Path.Combine(Output, "junc");
        if (Directory.Exists(junction))
        {
            Directory.Delete(junction);
        }

        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A directory junction at <paramref name="at"/> to <paramref name="target"/>; needs no admin rights.</summary>
    private static void Junction(string at, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{at}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Directory.Exists(at), "mklink /J didn't create the junction.");
    }

    [Fact]
    public async Task OverTheSizeLimitTheOldestNonFavouritesThePreviewNamedAreDeleted()
    {
        var items = new[] { Clip("a.mp4", TimeSpan.FromHours(4), favourite: true), Clip("b.mp4", TimeSpan.FromHours(3)), Clip("c.mp4", TimeSpan.FromHours(2)), Clip("d.mp4", TimeSpan.FromHours(1)) };
        Start(items, new CleanupSettings(96, null));

        var preview = await Actor.PreviewCleanup(new LibraryCleanupPreview(96, null));
        _time.Advance(StartDelay);
        WaitForPasses(1);

        Assert.Equal((2, 96L), (preview.Count, preview.Bytes));
        Assert.Equal(["b.mp4", "c.mp4"], preview.Paths);
        Assert.Equal(preview.Paths, Deleted);
        Assert.Equal(["a.mp4", "d.mp4"], OnDisk());
        Assert.Contains("Cleanup deleted b.mp4 (48 bytes).", Log);
    }

    [Fact]
    public void OverTheAgeLimitOnlyTheOlderItemsAreDeleted()
    {
        Start([Clip("a.mp4", TimeSpan.FromHours(2)), Clip("b.mp4", TimeSpan.FromHours(2)), Clip("c.mp4", TimeSpan.FromMinutes(10))], OneHour);

        _time.Advance(StartDelay);
        WaitForPasses(1);

        Assert.Equal(["a.mp4", "b.mp4"], Deleted);
        Assert.Equal(["c.mp4"], OnDisk());
    }

    [Fact]
    public void OnlyFavouritesOverTheSizeLimitAreKeptAndWarnedOfOnce()
    {
        Start([Clip("a.mp4", TimeSpan.FromHours(2), favourite: true), Clip("b.mp4", TimeSpan.FromHours(1), favourite: true)], new CleanupSettings(1, null));

        _time.Advance(StartDelay);
        WaitForPasses(1);
        _time.Advance(ToPeriodic);
        WaitForPasses(2);

        Assert.Empty(Deleted);
        Assert.Equal(["a.mp4", "b.mp4"], OnDisk());
        lock (_lock)
        {
            Assert.Single(_unreachable);
        }
    }

    [Fact]
    public void AFileBeingWrittenIsKept()
    {
        // 30 s is under the start delay, so the written item is old enough to be walked to.
        Start([Clip("a.mp4", TimeSpan.FromHours(2)), Clip("b.mp4", TimeSpan.FromHours(2))], new CleanupSettings(null, 30));
        var written = Path.Combine(Output, "w.mp4");
        Actor.BeginWrite(written, null);
        File.WriteAllBytes(written, LibraryActorTests.Mp4());

        _time.Advance(StartDelay);
        WaitForPasses(1);

        Assert.Equal(["a.mp4", "b.mp4"], Deleted);
        Assert.Equal(["w.mp4"], OnDisk());
    }

    [Fact]
    public async Task AnItemFavouritedAfterThePlanIsKept()
    {
        var b = Clip("b.mp4", TimeSpan.FromHours(2));
        Start([Clip("a.mp4", TimeSpan.FromHours(3)), b, Clip("c.mp4", TimeSpan.FromMinutes(10))], OneHour);
        Task? favourite = null;

        // Posted while the plan is logged, so it lands before the pass's first step.
        _onLog = line =>
        {
            if (line.StartsWith("Cleanup plan:", StringComparison.Ordinal))
            {
                favourite = Actor.SetFavourite(new LibrarySetFavourite(b.Path, b.Size, b.Modified, true));
            }
        };

        _time.Advance(StartDelay);
        WaitForPasses(1);
        await favourite!;

        Assert.Contains(Log, line => line.StartsWith("Cleanup plan: 2 files", StringComparison.Ordinal) && line.Contains("b.mp4", StringComparison.Ordinal));
        Assert.Equal(["a.mp4"], Deleted);
        Assert.Equal(["b.mp4", "c.mp4"], OnDisk());
    }

    [Theory]
    [InlineData(Gate.Early)]
    [InlineData(Gate.Blocked)]
    [InlineData(Gate.Rescan)]
    public async Task NothingIsDeletedUntilTheGateOpens(Gate gate)
    {
        var a = Clip("a.mp4", TimeSpan.FromHours(2));
        Start([a], gate == Gate.Rescan ? Off : OneHour, blocked: gate == Gate.Blocked);

        switch (gate)
        {
            case Gate.Early:
                Limits = new CleanupSettings(null, 1800);
                Actor.CleanupChanged();
                break;

            case Gate.Blocked:
                _time.Advance(StartDelay);
                break;

            case Gate.Rescan:
                _time.Advance(StartDelay);
                await Settled();

                // A folder created is reconciled by a rescan once the settle timer fires.
                Directory.CreateDirectory(Path.Combine(Output, "sub"));
                _time.WaitUntilArmed(TimeSpan.FromMilliseconds(250));
                Limits = OneHour;
                Actor.CleanupChanged();
                break;
        }

        await Settled();
        Assert.Empty(Deleted);
        Assert.DoesNotContain(Log, line => line.StartsWith("Cleanup plan:", StringComparison.Ordinal));

        switch (gate)
        {
            case Gate.Early:
                _time.Advance(StartDelay);
                break;

            case Gate.Blocked:
                Actor.AcknowledgeCorrupt();
                break;

            case Gate.Rescan:
                _time.Advance(TimeSpan.FromMilliseconds(250));
                break;
        }

        WaitForPasses(1);
        Assert.Equal(["a.mp4"], Deleted);
    }

    [Fact]
    public void AnItemOutsideTheOutputFolderIsNeverDeleted()
    {
        var outside = Path.Combine(_folder, "outside.mp4");
        File.WriteAllBytes(outside, LibraryActorTests.Mp4());
        var escaped = new LibraryItem(@"..\outside.mp4", null, 48, _time.GetUtcNow().AddHours(-3), false, Modified: Modified(outside));
        Start([escaped, Clip("a.mp4", TimeSpan.FromHours(2))], new CleanupSettings(1, 3600));

        _time.Advance(StartDelay);
        WaitForPasses(1);

        Assert.Equal(["a.mp4"], Deleted);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task AFileReachedThroughAJunctionIsNeverDeleted()
    {
        var other = Path.Combine(_folder, "other");
        Directory.CreateDirectory(other);
        var outside = Path.Combine(other, "outside.mp4");
        File.WriteAllBytes(outside, LibraryActorTests.Mp4());
        Junction(Path.Combine(Output, "junc"), other);
        var through = new LibraryItem(@"junc\outside.mp4", null, 48, _time.GetUtcNow().AddHours(-3), false, Modified: Modified(outside));
        Start([through, Clip("a.mp4", TimeSpan.FromHours(2))], OneHour);

        var preview = await Actor.PreviewCleanup(new LibraryCleanupPreview(null, 3600));
        _time.Advance(StartDelay);
        WaitForPasses(1);

        Assert.Equal(["a.mp4"], preview.Paths);
        Assert.Equal(["a.mp4"], Deleted);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ALockedFileIsSkippedThenDeletedOnTheNextPassAfterItsRelease()
    {
        var locked = Path.Combine(Output, "locked.mp4");
        Start([Clip("locked.mp4", TimeSpan.FromHours(3)), Clip("b.mp4", TimeSpan.FromHours(2))], OneHour);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _time.Advance(StartDelay);
            WaitForPasses(1);
        }

        Assert.Equal(["b.mp4"], Deleted);
        Assert.Contains(Log, line => line.StartsWith("Cleanup skipped locked.mp4:", StringComparison.Ordinal));

        _time.Advance(ToPeriodic);
        WaitForPasses(2);

        Assert.Equal(["b.mp4", "locked.mp4"], Deleted);
        Assert.Empty(OnDisk());
    }

    [Fact]
    public async Task WithBothLimitsOffNothingIsEverDeleted()
    {
        Start([Clip("a.mp4", TimeSpan.FromDays(365))], Off);

        _time.Advance(StartDelay);
        _time.Advance(ToPeriodic);
        await Settled();

        Assert.Empty(Deleted);
        Assert.Empty(Log);
        Assert.Equal(["a.mp4"], OnDisk());
    }

    private static DateTimeOffset Modified(string path) => new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

    /// <summary>A Clip on disk, indexed with a date <paramref name="age"/> before now.</summary>
    private LibraryItem Clip(string name, TimeSpan age, bool favourite = false)
    {
        var path = Path.Combine(Output, name);
        File.WriteAllBytes(path, LibraryActorTests.Mp4());
        return new LibraryItem(name, null, 48, _time.GetUtcNow() - age, false, Favourite: favourite, Modified: Modified(path));
    }

    /// <summary>Starts the actor and waits until it has started, so the 60 s timer runs from now.</summary>
    private void Start(IReadOnlyList<LibraryItem> items, CleanupSettings limits, bool blocked = false)
    {
        Limits = limits;
        _actor = new LibraryActor(
            items,
            () => Output,
            Path.Combine(_folder, "library.json"),
            _ => null,
            blocked,
            _ => { },
            _ => { },
            removed => Add(_removed, removed),
            _ => { },
            _ => { },
            _time,
            _ => { },
            createMissingFolder: false,
            cleanup: () => Limits,
            limitUnreachable: message => Add(_unreachable, message),
            log: line =>
            {
                Add(_log, line);
                _onLog?.Invoke(line);
            });
        _time.WaitUntilArmed(StartDelay);
        _time.WaitUntilArmed(TimeSpan.FromMinutes(10));
    }

    private void Add<T>(List<T> list, T value)
    {
        lock (_lock)
        {
            list.Add(value);
        }
    }

    // A round trip through the actor: everything posted before it has been handled.
    private Task Settled() => Actor.PreviewCleanup(new LibraryCleanupPreview(null, null));

    private void WaitForPasses(int count)
    {
        var until = DateTime.UtcNow + Within;
        while (Log.Count(line => line.StartsWith("Cleanup finished:", StringComparison.Ordinal)) < count)
        {
            Assert.True(DateTime.UtcNow < until, $"{count} Cleanup passes didn't finish within 2 s.");
            Thread.Sleep(10);
        }
    }

    private List<string> OnDisk() =>
        [.. Directory.EnumerateFiles(Output, "*.mp4", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(Output, p)).Order(StringComparer.Ordinal)];
}
