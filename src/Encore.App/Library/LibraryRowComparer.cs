using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The Library's row order for a sort; ties go by date (newest first), then path.</summary>
internal sealed class LibraryRowComparer(LibrarySort sort, Func<string, string> gameName) : IComparer<(LibraryItem Item, string Key)>
{
    public int Compare((LibraryItem Item, string Key) x, (LibraryItem Item, string Key) y)
    {
        var (a, b) = (x.Item, y.Item);
        var order = sort switch
        {
            LibrarySort.OldestFirst => a.Date.CompareTo(b.Date),
            LibrarySort.Name => NaturalCompare(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path)),
            LibrarySort.LargestFirst => b.Size.CompareTo(a.Size),
            LibrarySort.LongestFirst => (a.LengthSeconds, b.LengthSeconds) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (la, lb) => lb!.Value.CompareTo(la!.Value),
            },
            LibrarySort.Game => string.Compare(gameName(x.Key), gameName(y.Key), StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };

        if (order == 0 && sort != LibrarySort.OldestFirst)
        {
            order = b.Date.CompareTo(a.Date);
        }

        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
    }

    /// <summary>Compares ignoring case, with each run of digits compared as a number ("Clip 2" before "Clip 10").</summary>
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (si, sj) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var (da, db) = (a[si..i].TrimStart('0'), b[sj..j].TrimStart('0'));
                var number = da.Length != db.Length ? da.Length.CompareTo(db.Length) : string.CompareOrdinal(da, db);
                if (number != 0)
                {
                    return number;
                }

                continue;
            }

            var letter = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (letter != 0)
            {
                return letter;
            }

            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
