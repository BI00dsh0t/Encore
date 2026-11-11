namespace Encore.Core.Library;

/// <summary>What one Cleanup pass has done so far: the files it deleted and the ones it couldn't. Actor thread only.</summary>
internal sealed class CleanupPass
{
    /// <summary>Whether a pass is deleting, one step per message.</summary>
    public bool IsRunning { get; set; }

    /// <summary>The keys this pass couldn't delete; they aren't tried again in it.</summary>
    public HashSet<string> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this pass found each key's file inside the Output Folder; the delete checks again.</summary>
    public Dictionary<string, bool> InsideOutputFolder { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Deleted { get; private set; }

    public long DeletedBytes { get; private set; }

    public int SkippedCount { get; private set; }

    /// <summary>A new pass plans from nothing: no skips, no counts.</summary>
    public void Reset()
    {
        Skipped.Clear();
        InsideOutputFolder.Clear();
        Deleted = 0;
        DeletedBytes = 0;
        SkippedCount = 0;
    }

    public void CountDeleted(long size)
    {
        Deleted++;
        DeletedBytes += size;
    }

    public void Skip(string key)
    {
        Skipped.Add(key);
        SkippedCount++;
    }
}
