using System.Collections.ObjectModel;

namespace Encore.App.Library;

/// <summary>Updates a bound list in place, so the view keeps its scroll position and the rows it has made.</summary>
internal static class CollectionSync
{
    /// <summary>Makes the target list equal the wanted one in place, keeping shared items; a reorder resets the list.</summary>
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
        where T : class
    {
        var position = new Dictionary<T, int>(wanted.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < wanted.Count; i++)
        {
            position[wanted[i]] = i;
        }

        RemoveUnwanted(target, position);
        if (IsReordered(target, position))
        {
            target.Clear();
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= target.Count || !ReferenceEquals(target[i], wanted[i]))
            {
                target.Insert(i, wanted[i]);
            }
        }
    }

    private static void RemoveUnwanted<T>(ObservableCollection<T> target, Dictionary<T, int> position)
        where T : class
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!position.ContainsKey(target[i]))
            {
                target.RemoveAt(i);
            }
        }
    }

    private static bool IsReordered<T>(ObservableCollection<T> target, Dictionary<T, int> position)
        where T : class
    {
        for (var i = 1; i < target.Count; i++)
        {
            if (position[target[i]] < position[target[i - 1]])
            {
                return true;
            }
        }

        return false;
    }
}
