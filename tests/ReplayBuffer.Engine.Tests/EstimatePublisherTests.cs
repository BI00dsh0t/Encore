using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Engine.Sessions;

namespace ReplayBuffer.Engine.Tests;

/// <summary>The storage estimate is seeded unpublished, then published when it changes.</summary>
public sealed class EstimatePublisherTests
{
    [Fact]
    public void ASettingsChangePublishesAnUnchangedRefreshDoesNotAndDiskReadsTheRingFolder()
    {
        var document = SettingsDocument.Empty;
        var memory = new FakeMemoryStatus();
        var folders = new List<string>();
        var published = new List<StorageEstimate>();
        using var publisher = new EstimatePublisher(
            () => document,
            memory,
            folder =>
            {
                folders.Add(folder);
                return 1000;
            },
            @"C:\Local\ring",
            published.Add,
            TimeProvider.System);

        var seed = publisher.Seed();
        Assert.Equal(ReplayStorage.Memory, seed.Storage);
        Assert.True(seed.FreeBytes >= 32L << 30, "The working set counts back as free.");
        Assert.False(seed.ExceedsFree);
        Assert.Empty(published);

        document = With(document, Keys.BufferWindow, 600);
        publisher.Refresh();
        Assert.True(Assert.Single(published).Bytes > seed.Bytes);

        // Free bytes moving under 10 % publish nothing.
        memory.Available -= 1L << 30;
        publisher.Refresh();
        Assert.Single(published);

        document = With(document, Keys.ReplayStorage, ReplayStorage.Disk);
        publisher.Refresh();
        Assert.Equal([@"C:\Local\ring"], folders);
        Assert.Equal(new StorageEstimate(ReplayStorage.Disk, published[0].Bytes, false, 1000, true), published[^1]);

        // A ring file in the folder is the ring's own use, so it counts back as free.
        var ringFolder = Directory.CreateTempSubdirectory("estimate-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(ringFolder, RingFile.Prefix + "1.bin"), new byte[4096]);
            document = With(document, Keys.RingPath, ringFolder);
            publisher.Refresh();
            Assert.Equal(ringFolder, folders[^1]);
            Assert.Equal(1000 + 4096, published[^1].FreeBytes);
        }
        finally
        {
            Directory.Delete(ringFolder, recursive: true);
        }
    }

    private static SettingsDocument With<T>(SettingsDocument document, SettingKey<T> key, T value) =>
        document with { Revision = document.Revision + 1, Global = document.Global.SetItem(key.Name, key.ToJson(value)) };

    private sealed class FakeMemoryStatus : IMemoryStatus
    {
        public long Available { get; set; } = 32L << 30;

        public MemoryStatus Read() => new(Available, 64L << 30);
    }
}
