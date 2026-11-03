using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;

namespace ReplayBuffer.Core.Tests;

public sealed class OutputPathAllocatorTests : IDisposable
{
    // 10:00:00.900 local (UTC) at timestamp 10_000, 1000 ticks a second.
    private static readonly FixedTime Time = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, 900, TimeSpan.Zero), 10_000, 1000);

    private readonly string _output = Directory.CreateTempSubdirectory("rb-output-").FullName;
    private readonly OutputPathAllocator _allocator = new(Time);

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public void TwoAllocationsInOneSecondGetDistinctReservedPaths()
    {
        var document = Document(("a", new GameProfile { Name = "Game" }));

        var first = _allocator.Allocate(document, "a", _output, 10_000);
        var second = _allocator.Allocate(document, "a", _output, 9_500);

        Assert.Equal(Path.Combine(_output, "Game", "Game_2026-11-03_10-00-00.mp4"), first);
        Assert.Equal(Path.Combine(_output, "Game", "Game_2026-11-03_10-00-00_2.mp4"), second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void AGameWithNoProfileYetGetsAFolderNamedAfterIt()
    {
        var path = _allocator.AllocateForGame(Document(), @"c:\games\new.exe", "new.exe", _output, 10_000);

        Assert.Equal(Path.Combine(_output, "new.exe", "new.exe_2026-11-03_10-00-00.mp4"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AColonInTheNameIsReplacedInFolderAndFile()
    {
        var document = Document(("a", new GameProfile { Name = "Halo: Reach" }));

        var path = _allocator.Allocate(document, "a", _output, 10_000);

        Assert.Equal(Path.Combine(_output, "Halo_ Reach", "Halo_ Reach_2026-11-03_10-00-00.mp4"), path);
    }

    [Fact]
    public void DuplicateNamesGetNumberedFoldersThatNeverMove()
    {
        var profiles = Document(("a", new GameProfile { Name = "Launcher" }), ("b", new GameProfile { Name = "Launcher" })).Profiles.ToBuilder();
        ProfileFolders.AssignMissing(profiles);
        Assert.Equal("Launcher", profiles["a"].Folder);
        Assert.Equal("Launcher (2)", profiles["b"].Folder);

        profiles["0"] = new GameProfile { Name = "Launcher" };
        ProfileFolders.AssignMissing(profiles);

        Assert.Equal("Launcher", profiles["a"].Folder);
        Assert.Equal("Launcher (2)", profiles["b"].Folder);
        Assert.Equal("Launcher (3)", profiles["0"].Folder);
    }

    [Fact]
    public void NoProfileGetsTheMonitorRecordingsFolder()
    {
        var document = Document(("a", new GameProfile { Name = "monitor recordings" }));

        Assert.Equal("monitor recordings (2)", ProfileFolders.FolderOf(document, "a"));
    }

    [Fact]
    public void ARenamedProfileKeepsItsFolderAndUsesItsNewName()
    {
        var document = Document(("a", new GameProfile { Name = "New Name", Folder = "Old Name" }));

        var path = _allocator.Allocate(document, "a", _output, 10_000);

        Assert.Equal(Path.Combine(_output, "Old Name", "New Name_2026-11-03_10-00-00.mp4"), path);
    }

    [Fact]
    public void AnUnknownProfileIsRefused()
    {
        var e = Assert.Throws<ErrorCodeException>(() => _allocator.Allocate(SettingsDocument.Empty, "missing", _output, 10_000));

        Assert.Equal(ErrorCodes.ProfileNotFound, e.Code);
    }

    private static SettingsDocument Document(params (string Id, GameProfile Profile)[] profiles) => SettingsDocument.Empty with
    {
        Profiles = SettingsMaps.EmptyProfiles.AddRange(profiles.Select(p => KeyValuePair.Create(p.Id, p.Profile))),
    };

    private sealed class FixedTime(DateTimeOffset utcNow, long timestamp, long frequency) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long GetTimestamp() => timestamp;

        public override long TimestampFrequency => frequency;
    }
}
