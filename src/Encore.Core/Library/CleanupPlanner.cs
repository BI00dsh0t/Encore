using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Library;

/// <summary>Cleanup's choice of what to delete: one function, so a preview and a pass agree.</summary>
internal static class CleanupPlanner
{
    /// <summary>The oldest items to delete so the Library is within its limits. Rules: docs/code-notes.md.</summary>
    public static CleanupPlan Plan(DateTimeOffset now, CleanupSettings limits, IEnumerable<LibraryItem> items, string folder, IReadOnlySet<string> skip, Func<string, bool> inside)
    {
        ArgumentNullException.ThrowIfNull(inside);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(skip);
        if (limits.MaxBytes is null && limits.MaxAgeSeconds is null)
        {
            return new CleanupPlan([], 0, 0, false);
        }

        var all = items.ToList();
        var total = all.Sum(i => i.Size);
        var skipped = false;
        var candidates = new List<LibraryItem>();
        foreach (var item in all.Where(i => !i.BeingWritten && !i.Favourite && Inside(folder, i.Path)))
        {
            if (skip.Contains(item.Path))
            {
                skipped = true;
            }
            else
            {
                candidates.Add(item);
            }
        }

        var delete = new List<LibraryItem>();
        var bytes = 0L;
        foreach (var item in candidates.OrderBy(i => i.Date).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            var overSize = limits.MaxBytes is { } maxBytes && total > maxBytes;
            var overAge = limits.MaxAgeSeconds is { } maxAge && (now - item.Date).TotalSeconds > maxAge;
            if (!overSize && !overAge)
            {
                break;
            }

            // Checked only for the items reached: a file behind a junction or link may really be outside the folder.
            if (!inside(item.Path))
            {
                continue;
            }

            delete.Add(item);
            bytes += item.Size;
            total -= item.Size;
        }

        // Still over the size limit means every candidate is planned; a skipped file may still go next pass.
        var unreachable = limits.MaxBytes is { } limit && total > limit && !skipped;
        return new CleanupPlan(delete, bytes, total, unreachable);
    }

    private static bool Inside(string folder, string key) =>
        LibraryActor.IsMp4(key) && LibraryActor.TryKey(folder, Path.GetFullPath(Path.Combine(folder, key)), out _);
}
