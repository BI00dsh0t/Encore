namespace Encore.Contracts.Tests;

/// <summary>The rebrand's one-time copy of an instance's old ReplayBuffer data folders (2026-11-10).</summary>
public sealed class LegacyDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-legacy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AFirstStartCopiesTheOldFoldersOnceWithoutTheCacheAndLeavesThemInPlace()
    {
        var roaming = Path.Combine(_root, "Roaming");
        var local = Path.Combine(_root, "Local");
        Directory.CreateDirectory(Path.Combine(roaming, "ReplayBuffer"));
        File.WriteAllText(Path.Combine(roaming, "ReplayBuffer", "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(local, "ReplayBuffer", "ring"));
        File.WriteAllText(Path.Combine(local, "ReplayBuffer", "ring", "big.bin"), "x");
        File.WriteAllText(Path.Combine(local, "ReplayBuffer", "library.json"), "[]");
        var instance = new Instance(Instance.InstalledName, Path.Combine(roaming, "Encore"), Path.Combine(local, "Encore"));

        var copied = LegacyData.CopyIfFirstStart(instance);

        Assert.Equal(2, copied.Count);
        Assert.True(File.Exists(Path.Combine(roaming, "Encore", "settings.json")));
        Assert.True(File.Exists(Path.Combine(local, "Encore", "library.json")));
        Assert.False(Directory.Exists(Path.Combine(local, "Encore", "ring")));
        Assert.True(File.Exists(Path.Combine(roaming, "ReplayBuffer", "settings.json")));

        File.WriteAllText(Path.Combine(roaming, "Encore", "settings.json"), "{\"changed\":true}");
        Assert.Empty(LegacyData.CopyIfFirstStart(instance));
        Assert.Equal("{\"changed\":true}", File.ReadAllText(Path.Combine(roaming, "Encore", "settings.json")));
    }

    [Fact]
    public void ATestInstanceNeverCopies()
    {
        var instance = new Instance("Encore-Test-legacy1", Path.Combine(_root, "a"), Path.Combine(_root, "b"));
        Assert.Empty(LegacyData.CopyIfFirstStart(instance));
    }
}
