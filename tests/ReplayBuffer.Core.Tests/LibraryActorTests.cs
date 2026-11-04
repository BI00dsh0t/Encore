using System.Text.Json;
using ReplayBuffer.Contracts;
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
        var listedModified = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(Output, "Game", "listed.mp4")), TimeSpan.Zero);
        Assert.Equal(new LibraryItem(@"Game\listed.mp4", ProfileId, 42, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), false, Modified: listedModified), items[0]);
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
            {"version":1,"items":[{"path":"clip.mp4","profileId":"c:\\games\\a.exe","date":"2026-11-01T10:00:00+00:00","size":48,"favourite":true}]}
            """);

        var fromBak = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { });

        Assert.False(fromBak.Corrupt);
        Assert.Equal((ProfileId, true), (Assert.Single(fromBak.Items).ProfileId, fromBak.Items[0].Favourite));
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

    [Fact]
    public async Task AFavouriteIsSavedAndMovesWithAnOutsideRename()
    {
        File.WriteAllBytes(Path.Combine(Output, "Game", "clip.mp4"), Mp4());
        var item = Assert.Single(LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Items);
        using (var library = new Harness(this, [item]))
        {
            await library.Actor.SetFavourite(new LibrarySetFavourite(item.Path, item.Size, item.Modified, true));
            library.WaitForEvent(e => e is LibraryItem { Favourite: true });
        }

        // A restart keeps it, and a rename in Explorer moves it with the item.
        var loaded = Assert.Single(LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Items);
        Assert.True(loaded.Favourite);
        using var restarted = new Harness(this, [loaded]);
        restarted.Actor.Watching = true;
        File.Move(Path.Combine(Output, "Game", "clip.mp4"), Path.Combine(Output, "Game", "renamed.mp4"));
        restarted.WaitFor(index => index.Count == 1 && index.TryGetValue(@"Game\renamed.mp4", out var moved) && moved.Favourite);
    }

    [Fact]
    public async Task AStaleIdentityIsRefusedAndAFileChangedOnDiskIsRefreshed()
    {
        var clip = Path.Combine(Output, "clip.mp4");
        File.WriteAllBytes(clip, Mp4());
        var item = Assert.Single(LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Items);
        using var library = new Harness(this, [item]);

        var older = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            library.Actor.SetFavourite(new LibrarySetFavourite(item.Path, item.Size, item.Modified!.Value.AddMinutes(-1), true)));
        Assert.Equal(ErrorCodes.LibraryItemChanged, older.Code);
        Assert.Empty(library.Events);

        // Touched on disk at the same size: refused, and the item now carries the file's modified time.
        var touched = item.Modified!.Value.AddHours(1);
        File.SetLastWriteTimeUtc(clip, touched.UtcDateTime);
        var changed = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            library.Actor.SetFavourite(new LibrarySetFavourite(item.Path, item.Size, item.Modified, true)));
        Assert.Equal(ErrorCodes.LibraryItemChanged, changed.Code);
        var refreshed = Assert.IsType<LibraryItem>(library.WaitForEvent(e => e is LibraryItem));
        Assert.Equal((touched, false), (refreshed.Modified, refreshed.Favourite));
    }

    [Fact]
    public async Task AFavouriteSetWhileBeingWrittenSurvivesItsEnd()
    {
        var clip = Path.Combine(Output, "Game", "clip.mp4");
        using var library = new Harness(this, []);
        library.Actor.BeginWrite(clip, ProfileId);
        library.WaitFor(index => index.ContainsKey(@"Game\clip.mp4"));

        await library.Actor.SetFavourite(new LibrarySetFavourite(@"Game\clip.mp4", 0, null, true));
        File.WriteAllBytes(clip, Mp4());
        library.Actor.EndWrite(clip, WriteOutcome.Written);

        library.WaitFor(index => index[@"Game\clip.mp4"] is { BeingWritten: false, Favourite: true, Size: 48, Modified: not null });
    }

    [Fact]
    public async Task ARenameMovesTheFileKeepsItsFavouriteAndIsPublishedOnce()
    {
        File.WriteAllBytes(Path.Combine(Output, "Game", "a.mp4"), Mp4());
        var item = Assert.Single(LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Items) with { Favourite = true };
        using var library = new Harness(this, [item]);

        await library.Actor.Rename(new LibraryRename(item.Path, item.Size, item.Modified, "b"));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(Output, "Game", "a.mp4")));
        Assert.True(File.Exists(Path.Combine(Output, "Game", "b.mp4")));
        Assert.Collection(
            library.Events,
            e => Assert.Equal(@"Game\a.mp4", Assert.IsType<LibraryItemRemoved>(e).Path),
            e => Assert.Equal(item with { Path = @"Game\b.mp4" }, Assert.IsType<LibraryItem>(e)));
    }

    [Fact]
    public async Task ADeleteRecyclesThroughThePortAndNoRecycleBinKeepsTheFile()
    {
        var bin = Path.Combine(_folder, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(Output, "a.mp4"), Mp4());
        File.WriteAllBytes(Path.Combine(Output, "kept.mp4"), Mp4());
        var items = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Items.OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
        var recycled = new List<string>();
        using var library = new Harness(this, items, recycle: path =>
        {
            if (Path.GetFileName(path) == "kept.mp4")
            {
                return false;
            }

            recycled.Add(path);
            File.Move(path, Path.Combine(bin, Path.GetFileName(path)));
            return true;
        });

        await library.Actor.Delete(new LibraryDelete(items[0].Path, items[0].Size, items[0].Modified));
        var noBin = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            library.Actor.Delete(new LibraryDelete(items[1].Path, items[1].Size, items[1].Modified)));

        Assert.Equal([Path.Combine(Output, "a.mp4")], recycled);
        Assert.Equal("a.mp4", Assert.IsType<LibraryItemRemoved>(Assert.Single(library.Events)).Path);
        Assert.Equal(ErrorCodes.RecycleBinUnavailable, noBin.Code);
        Assert.True(File.Exists(Path.Combine(Output, "kept.mp4")));
        library.WaitFor(index => index.Count == 1 && index.ContainsKey("kept.mp4"));
    }

    [Fact]
    public async Task AClipOrARecordingBeingWrittenIsNeitherRenamedNorDeleted()
    {
        var clip = Path.Combine(Output, "Game", "clip.mp4");
        var recording = Path.Combine(Output, "Game", "recording.mp4");
        using var library = new Harness(this, [], recycle: _ => throw new InvalidOperationException("Recycled a file being written."));

        // A Clip's empty reservation, and a Recording's fragmented MP4 growing in an open stream.
        library.Actor.BeginWrite(clip, ProfileId);
        File.WriteAllBytes(clip, []);
        library.Actor.BeginWrite(recording, ProfileId);
        using var stream = new FileStream(recording, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        stream.Write(Fragment("ftyp", 8));
        stream.Write(Fragment("moov", 8));
        stream.Write(Fragment("moof", 8));
        stream.Write(Fragment("mdat", 32));
        stream.Flush();
        library.WaitFor(index => index.Count == 2);

        foreach (var key in new[] { @"Game\clip.mp4", @"Game\recording.mp4" })
        {
            var renamed = await Assert.ThrowsAsync<ErrorCodeException>(() => library.Actor.Rename(new LibraryRename(key, 0, null, "other")));
            var deleted = await Assert.ThrowsAsync<ErrorCodeException>(() => library.Actor.Delete(new LibraryDelete(key, 0, null)));
            Assert.Equal((ErrorCodes.FileBeingWritten, ErrorCodes.FileBeingWritten), (renamed.Code, deleted.Code));
        }

        stream.Write(Fragment("moof", 8));
        stream.Write(Fragment("mdat", 16));
        stream.Flush();
        Assert.Empty(File.ReadAllBytes(clip));
        Assert.Equal(80, new FileInfo(recording).Length);
        Assert.False(File.Exists(Path.Combine(Output, "Game", "other.mp4")));

        static byte[] Fragment(string type, int size)
        {
            var box = new byte[size];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(box, size);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            return box;
        }
    }

    [Fact]
    public async Task ABadNameATakenNameAndAStaleIdentityAreRefused()
    {
        File.WriteAllBytes(Path.Combine(Output, "a.mp4"), Mp4());
        File.WriteAllBytes(Path.Combine(Output, "b.mp4"), Mp4());
        var items = LibraryActor.Load(Output, LibraryFile, _ => null, _ => { }).Items;
        var item = items.Single(i => i.Path == "a.mp4");
        using var library = new Harness(this, items);

        foreach (var name in new[] { "a:b", "CON", "x." })
        {
            var bad = await Assert.ThrowsAsync<ErrorCodeException>(() => library.Actor.Rename(new LibraryRename(item.Path, item.Size, item.Modified, name)));
            Assert.Equal(ErrorCodes.InvalidFileName, bad.Code);
        }

        var taken = await Assert.ThrowsAsync<ErrorCodeException>(() => library.Actor.Rename(new LibraryRename(item.Path, item.Size, item.Modified, "b")));
        Assert.Equal(ErrorCodes.FileNameTaken, taken.Code);

        var stale = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            library.Actor.Rename(new LibraryRename(item.Path, item.Size, item.Modified!.Value.AddMinutes(-1), "c")));
        Assert.Equal(ErrorCodes.LibraryItemChanged, stale.Code);

        Assert.True(File.Exists(Path.Combine(Output, "a.mp4")));
        Assert.Empty(library.Events);
    }

    /// <summary>A live actor over the test's Output Folder, with the index its events build.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, LibraryItem> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<object> _events = [];
        private string _currentFolder = "";

        public Harness(LibraryActorTests test, IReadOnlyList<LibraryItem> items, bool cleanupBlocked = false, Func<string, double?>? readLength = null, Func<string, bool>? recycle = null)
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
                readLength: readLength,
                recycle: recycle);
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
