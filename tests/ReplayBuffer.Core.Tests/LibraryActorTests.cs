using System.Text.Json;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Library;

namespace ReplayBuffer.Core.Tests;

public sealed class LibraryActorTests : IDisposable
{
    private const string ProfileId = @"c:\games\a.exe";
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-library-" + Guid.NewGuid().ToString("N"));

    public LibraryActorTests() => Directory.CreateDirectory(Path.Combine(_folder, "out", "Game"));

    private string Output => Path.Combine(_folder, "out");

    private string LibraryFile => Path.Combine(_folder, "library.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A minimal complete MP4: <c>ftyp</c>, an empty <c>moov</c>, and an <c>mdat</c> of <paramref name="payload"/> bytes.</summary>
    internal static byte[] Mp4(int payload = 16)
    {
        var bytes = new byte[16 + 8 + 8 + payload];
        Box(bytes, 0, 16, "ftyp");
        "isom"u8.CopyTo(bytes.AsSpan(8));
        Box(bytes, 16, 8, "moov");
        Box(bytes, 24, 8 + payload, "mdat");
        return bytes;

        static void Box(byte[] bytes, int at, int size, string type)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at), size);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, at + 4);
        }
    }

    [Fact]
    public void LoadMergesTheJsonWithTheScan()
    {
        File.WriteAllBytes(Path.Combine(Output, "Game", "listed.mp4"), Mp4(10));
        File.WriteAllBytes(Path.Combine(Output, "Game", "unlisted.mp4"), Mp4(20));
        File.WriteAllBytes(Path.Combine(Output, "Game", "reserved.mp4"), []);
        File.WriteAllText(LibraryFile, """
            {"version":1,"items":[
              {"path":"Game\\listed.mp4","profileId":"c:\\games\\a.exe","date":"2026-11-01T10:00:00+00:00"},
              {"path":"Game\\gone.mp4","profileId":"c:\\games\\a.exe","date":"2026-11-01T11:00:00+00:00"}]}
            """);
        var failures = new List<Exception>();

        var load = LibraryActor.Load(Output, LibraryFile, _ => null, failures.Add);
        var items = load.Items.OrderBy(i => i.Path, StringComparer.Ordinal).ToList();

        Assert.Empty(failures);
        Assert.False(load.Corrupt);
        Assert.Equal(2, items.Count);
        Assert.Equal(new LibraryItem(@"Game\listed.mp4", ProfileId, 42, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), false), items[0]);
        Assert.Equal((@"Game\unlisted.mp4", (string?)null, 52L, false), (items[1].Path, items[1].ProfileId, items[1].Size, items[1].BeingWritten));
        Assert.Equal(File.GetCreationTimeUtc(Path.Combine(Output, "Game", "unlisted.mp4")), items[1].Date.UtcDateTime);
    }

    [Fact]
    public void ABadJsonLoadsTheScanAndAFailedSaveLeavesTheEvents()
    {
        var clip = Path.Combine(Output, "Game", "clip.mp4");
        File.WriteAllBytes(clip, Mp4(30));
        File.WriteAllText(LibraryFile, "{ not json");
        var failures = new List<Exception>();

        var load = LibraryActor.Load(Output, LibraryFile, _ => null, failures.Add);

        Assert.Equal(@"Game\clip.mp4", Assert.Single(load.Items).Path);
        Assert.Single(failures);

        // library.json is a directory, so the save can't replace it.
        var blocked = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(blocked);
        var events = new List<object>();
        var saveFailures = new List<Exception>();
        var second = Path.Combine(Output, "Game", "second.mp4");
        using (var actor = new LibraryActor(load.Items, () => Output, blocked, _ => null, false, events.Add, events.Add, events.Add, events.Add, events.Add, TimeProvider.System, saveFailures.Add, false))
        {
            actor.BeginWrite(second, ProfileId);
            File.WriteAllBytes(second, new byte[40]);
            actor.EndWrite(second, WriteOutcome.Written);
        }

        Assert.Collection(
            events,
            e => Assert.True(Assert.IsType<LibraryItem>(e).BeingWritten),
            e => Assert.Equal((40L, false), (Assert.IsType<LibraryItem>(e).Size, ((LibraryItem)e).BeingWritten)));
        Assert.Single(saveFailures);
        Assert.False(File.Exists(blocked + ".tmp"));
    }

    [Fact]
    public void StartDeletesLeftoversAndListsOnlyCompleteFiles()
    {
        var part = Path.Combine(Output, "Game", "crashed.mp4.rbpart");
        var reservation = Path.Combine(Output, "Game", "crashed.mp4");
        File.WriteAllBytes(part, Mp4()[..20]);
        File.WriteAllBytes(reservation, []);
        File.WriteAllBytes(Path.Combine(Output, "truncated.mp4"), Mp4(100)[..60]);
        File.WriteAllBytes(Path.Combine(Output, "Game", "clip.mp4"), Mp4());

        var load = LibraryActor.Load(Output, LibraryFile, folder => folder == "Game" ? ProfileId : null, _ => { });

        Assert.False(File.Exists(part));
        Assert.False(File.Exists(reservation));
        var item = Assert.Single(load.Items);
        Assert.Equal((@"Game\clip.mp4", ProfileId), (item.Path, item.ProfileId));
    }

    [Fact]
    public void ChangesOutsideTheAppAreReconciled()
    {
        using var library = new Harness(this, []);
        var copied = Path.Combine(Output, "Game", "copied.mp4");
        var renamed = Path.Combine(Output, "Game", "renamed.mp4");

        File.WriteAllBytes(copied, Mp4());
        var added = library.WaitFor(index => index.ContainsKey(@"Game\copied.mp4"))[@"Game\copied.mp4"];
        Assert.Equal(ProfileId, added.ProfileId);

        File.Move(copied, renamed);
        var moved = library.WaitFor(index => index.Count == 1 && index.ContainsKey(@"Game\renamed.mp4"))[@"Game\renamed.mp4"];
        Assert.Equal((added.ProfileId, added.Date), (moved.ProfileId, moved.Date));

        // A folder renamed moves its items, keeping their metadata.
        Directory.Move(Path.Combine(Output, "Game"), Path.Combine(Output, "Renamed"));
        renamed = Path.Combine(Output, "Renamed", "renamed.mp4");
        var inFolder = library.WaitFor(index => index.Count == 1 && index.ContainsKey(@"Renamed\renamed.mp4"))[@"Renamed\renamed.mp4"];
        Assert.Equal(added.ProfileId, inFolder.ProfileId);

        File.Delete(renamed);
        library.WaitFor(index => index.Count == 0);
    }

    [Fact]
    public void AWatcherErrorRescansTheFolder()
    {
        var kept = Path.Combine(Output, "kept.mp4");
        var deleted = Path.Combine(Output, "deleted.mp4");
        File.WriteAllBytes(kept, Mp4());
        File.WriteAllBytes(deleted, Mp4());
        var load = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });
        using var library = new Harness(this, load.Items);

        library.Actor.Watching = false;
        File.Delete(deleted);
        File.WriteAllBytes(Path.Combine(Output, "Game", "missed.mp4"), Mp4());
        library.Actor.OnWatcherError(new InternalBufferOverflowException());

        library.WaitFor(index => index.Keys.Order(StringComparer.Ordinal).SequenceEqual([@"Game\missed.mp4", "kept.mp4"]));
    }

    [Fact]
    public async Task TheActorsOwnMoveIsPublishedOnce()
    {
        File.WriteAllBytes(Path.Combine(Output, "a.mp4"), Mp4());
        var load = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });
        using var library = new Harness(this, load.Items);

        library.Actor.Move(Path.Combine(Output, "a.mp4"), Path.Combine(Output, "b.mp4"));
        library.WaitFor(index => index.ContainsKey("b.mp4"));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Collection(
            library.Events,
            e => Assert.Equal("a.mp4", Assert.IsType<LibraryItemRemoved>(e).Path),
            e => Assert.Equal("b.mp4", Assert.IsType<LibraryItem>(e).Path));
    }

    [Fact]
    public void ACorruptIndexFallsBackToTheBakThenTheScan()
    {
        File.WriteAllBytes(Path.Combine(Output, "clip.mp4"), Mp4());
        File.WriteAllText(LibraryFile, "{ not json");
        File.WriteAllText(LibraryFile + ".bak", """
            {"version":1,"items":[{"path":"clip.mp4","profileId":"c:\\games\\a.exe","date":"2026-11-01T10:00:00+00:00","size":48}]}
            """);

        var fromBak = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });

        Assert.False(fromBak.Corrupt);
        Assert.Equal(ProfileId, Assert.Single(fromBak.Items).ProfileId);
        Assert.True(File.Exists(LibraryFile + ".corrupt"));

        File.WriteAllText(LibraryFile, "{ not json");
        File.WriteAllText(LibraryFile + ".bak", "[]");

        var fromScan = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });

        Assert.True(fromScan.Corrupt);
        Assert.Null(Assert.Single(fromScan.Items).ProfileId);

        // The block is saved, so a restart before it is acknowledged still holds Cleanup off.
        new Harness(this, fromScan.Items, cleanupBlocked: true).Dispose();
        Assert.True(LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Corrupt);

        using (var library = new Harness(this, fromScan.Items, cleanupBlocked: true))
        {
            library.Actor.AcknowledgeCorrupt();
        }

        Assert.False(LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Corrupt);
    }

    [Fact]
    public void AnOutputFolderChangeRebuildsTheIndex()
    {
        File.WriteAllBytes(Path.Combine(Output, "old.mp4"), Mp4());
        var load = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });
        var missing = Path.Combine(_folder, "missing");
        var other = Path.Combine(_folder, "other");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "new.mp4"), Mp4());
        using var library = new Harness(this, load.Items);

        library.Folder = missing;
        library.Actor.OutputFolderChanged();
        library.WaitFor(index => index.Count == 0);
        Assert.True(Directory.Exists(missing));

        library.Folder = other;
        library.Actor.OutputFolderChanged();
        var reset = Assert.IsType<LibraryReset>(library.WaitForEvent(e => e is LibraryReset { Items.Count: > 0 }));
        Assert.Equal("new.mp4", Assert.Single(reset.Items).Path);

        library.Actor.Dispose();
        using var saved = JsonDocument.Parse(File.ReadAllBytes(LibraryFile));
        Assert.Equal(other, saved.RootElement.GetProperty("outputFolder").GetString());
    }

    [Fact]
    public void AWrittenClipsLengthIsPublishedSavedAndReusedUntilItsSizeChanges()
    {
        var clip = Path.Combine(Output, "Game", "clip.mp4");
        var first = 0;
        using (var library = new Harness(this, [], readLength: _ => { Interlocked.Increment(ref first); return 30; }))
        {
            library.Actor.Watching = true;
            library.Actor.BeginWrite(clip, ProfileId);
            File.WriteAllBytes(clip, Mp4());
            library.Actor.EndWrite(clip, WriteOutcome.Written);

            library.WaitForEvent(e => e is LibraryItem { LengthSeconds: 30 });
            Assert.Collection(
                library.Events.OfType<LibraryItem>(),
                e => Assert.True(e.BeingWritten),
                e => Assert.Equal((48L, (double?)null), (e.Size, e.LengthSeconds)),
                e => Assert.Equal((48L, (double?)30), (e.Size, e.LengthSeconds)));
        }

        using (var saved = JsonDocument.Parse(File.ReadAllBytes(LibraryFile)))
        {
            Assert.Equal(30, saved.RootElement.GetProperty("items")[0].GetProperty("lengthSeconds").GetDouble());
        }

        // A restart with the file unchanged reuses the saved length without reading it.
        var load = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });
        Assert.Equal(30, Assert.Single(load.Items).LengthSeconds);
        var second = 0;
        using var restarted = new Harness(this, load.Items, readLength: _ => { Interlocked.Increment(ref second); return 12; });
        restarted.Actor.Watching = true;
        Thread.Sleep(300);
        Assert.Equal(0, Volatile.Read(ref second));

        // Rewritten outside the app at a new size: the length is read again.
        File.WriteAllBytes(clip, Mp4(64));
        restarted.WaitFor(index => index[@"Game\clip.mp4"] is { Size: 96, LengthSeconds: 12 });
        Assert.Equal(1, Volatile.Read(ref second));
    }

    /// <summary>A live actor over the test's Output Folder, with the index its events build.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, LibraryItem> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<object> _events = [];
        private string _currentFolder = "";

        public Harness(LibraryActorTests test, IReadOnlyList<LibraryItem> items, bool cleanupBlocked = false, Func<string, double?>? readLength = null)
        {
            Folder = test.Output;
            foreach (var item in items)
            {
                _index[item.Path] = item;
            }

            Actor = new LibraryActor(
                items,
                () => Folder,
                test.LibraryFile,
                folder => string.Equals(folder, "Game", StringComparison.OrdinalIgnoreCase) ? ProfileId : null,
                cleanupBlocked,
                item => Apply(item, () => _index[item.Path] = item),
                item => Apply(item, () => _index[item.Path] = item),
                removed => Apply(removed, () => _index.Remove(removed.Path)),
                reset => Apply(reset, () =>
                {
                    _index.Clear();
                    foreach (var item in reset.Items)
                    {
                        _index[item.Path] = item;
                    }
                }),
                blocked => Apply(blocked, () => { }),
                TimeProvider.System,
                _ => { },
                createMissingFolder: true,
                readLength: readLength);
        }

        public LibraryActor Actor { get; }

        public string Folder
        {
            get => Volatile.Read(ref _currentFolder);
            set => Volatile.Write(ref _currentFolder, value);
        }

        public IReadOnlyList<object> Events
        {
            get
            {
                lock (_lock)
                {
                    return [.. _events];
                }
            }
        }

        public void Dispose() => Actor.Dispose();

        /// <summary>The index once <paramref name="done"/> holds, within 2 s.</summary>
        public Dictionary<string, LibraryItem> WaitFor(Func<Dictionary<string, LibraryItem>, bool> done)
        {
            var until = DateTime.UtcNow + Within;
            while (true)
            {
                lock (_lock)
                {
                    if (done(_index))
                    {
                        return new Dictionary<string, LibraryItem>(_index, StringComparer.OrdinalIgnoreCase);
                    }
                }

                Assert.True(DateTime.UtcNow < until, "The index didn't reach the expected state within 2 s.");
                Thread.Sleep(20);
            }
        }

        public object WaitForEvent(Func<object, bool> match)
        {
            var until = DateTime.UtcNow + Within;
            while (true)
            {
                if (Events.FirstOrDefault(match) is { } found)
                {
                    return found;
                }

                Assert.True(DateTime.UtcNow < until, "The event didn't arrive within 2 s.");
                Thread.Sleep(20);
            }
        }

        private void Apply(object e, Action change)
        {
            lock (_lock)
            {
                _events.Add(e);
                change();
            }
        }
    }
}
