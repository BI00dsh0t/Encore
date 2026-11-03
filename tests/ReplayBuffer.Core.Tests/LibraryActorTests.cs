using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Library;

namespace ReplayBuffer.Core.Tests;

public sealed class LibraryActorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-library-" + Guid.NewGuid().ToString("N"));

    public LibraryActorTests() => Directory.CreateDirectory(Path.Combine(_folder, "out", "Game"));

    private string Output => Path.Combine(_folder, "out");

    private string LibraryFile => Path.Combine(_folder, "library.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void LoadMergesTheJsonWithTheScan()
    {
        File.WriteAllBytes(Path.Combine(Output, "Game", "listed.mp4"), new byte[10]);
        File.WriteAllBytes(Path.Combine(Output, "Game", "unlisted.mp4"), new byte[20]);
        File.WriteAllBytes(Path.Combine(Output, "Game", "reserved.mp4"), []);
        File.WriteAllBytes(Path.Combine(Output, "Game", "partial.mp4.rbpart"), new byte[5]);
        File.WriteAllText(LibraryFile, """
            {"version":1,"items":[
              {"path":"Game\\listed.mp4","profileId":"c:\\games\\a.exe","date":"2026-11-01T10:00:00+00:00"},
              {"path":"Game\\gone.mp4","profileId":"c:\\games\\a.exe","date":"2026-11-01T11:00:00+00:00"}]}
            """);
        var failures = new List<Exception>();

        var items = LibraryActor.Load(Output, LibraryFile, failures.Add).OrderBy(i => i.Path, StringComparer.Ordinal).ToList();

        Assert.Empty(failures);
        Assert.Equal(2, items.Count);
        Assert.Equal(new LibraryItem(@"Game\listed.mp4", @"c:\games\a.exe", 10, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), false), items[0]);
        Assert.Equal((@"Game\unlisted.mp4", (string?)null, 20L, false), (items[1].Path, items[1].ProfileId, items[1].Size, items[1].BeingWritten));
        Assert.Equal(File.GetCreationTimeUtc(Path.Combine(Output, "Game", "unlisted.mp4")), items[1].Date.UtcDateTime);
    }

    [Fact]
    public void ABadJsonLoadsTheScanAndAFailedSaveLeavesTheEvents()
    {
        var clip = Path.Combine(Output, "Game", "clip.mp4");
        File.WriteAllBytes(clip, new byte[30]);
        File.WriteAllText(LibraryFile, "{ not json");
        var failures = new List<Exception>();

        var items = LibraryActor.Load(Output, LibraryFile, failures.Add);

        Assert.Equal(@"Game\clip.mp4", Assert.Single(items).Path);
        Assert.Single(failures);

        // library.json is a directory, so the save can't replace it.
        var blocked = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(blocked);
        var events = new List<object>();
        var saveFailures = new List<Exception>();
        var second = Path.Combine(Output, "Game", "second.mp4");
        using (var actor = new LibraryActor([], () => Output, blocked, events.Add, events.Add, events.Add, TimeProvider.System, saveFailures.Add))
        {
            actor.BeginWrite(second, "c:\\games\\a.exe");
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
}
