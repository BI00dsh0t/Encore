using System.Globalization;
using Encore.Contracts;

namespace Encore.App.Library;

/// <summary>The wording of the Library's delete: its question, why a row can't be deleted, and what failed.</summary>
internal static class LibraryDeleteText
{
    /// <summary>Whether Delete may send this row: not a Favourite, not being written, and shown by a snapshot.</summary>
    public static bool IsDeletable(LibraryRow row) => !row.IsFavourite && !row.IsLocked && row.Item is not null;

    /// <summary>The confirmation: what goes to the Recycle Bin, and which selected rows are kept.</summary>
    public static string Question(IReadOnlyList<LibraryRow> shown, IReadOnlyList<LibraryRow> deletable)
    {
        var question = deletable.Count == 1
            ? $"Move “{deletable[0].Name}” to the Recycle Bin?"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Move {deletable.Count} items ({LibraryText.SizeText(deletable.Sum(row => row.Item!.Size))}) to the Recycle Bin?");

        var favourites = shown.Count(row => row.IsFavourite);
        var saving = shown.Count(row => !row.IsFavourite && row.IsLocked);
        var kept = new List<string>(2);
        if (favourites > 0)
        {
            kept.Add(favourites == 1 ? "1 Favourite" : string.Create(CultureInfo.InvariantCulture, $"{favourites} Favourites"));
        }

        if (saving > 0)
        {
            kept.Add(saving == 1 ? "1 file being saved" : string.Create(CultureInfo.InvariantCulture, $"{saving} files being saved"));
        }

        return kept.Count == 0
            ? question
            : $"{question}\n{string.Join(" and ", kept)} {(favourites + saving == 1 ? "is" : "are")} kept.";
    }

    /// <summary>Why none of the rows can be deleted.</summary>
    public static string NotDeletable(IReadOnlyList<LibraryRow> rows)
    {
        var favourites = rows.Count(row => row.IsFavourite);
        if (favourites == rows.Count)
        {
            return rows.Count == 1
                ? "Favourites can't be deleted. Remove it from Favourites first."
                : "Favourites can't be deleted. Remove them from Favourites first.";
        }

        if (favourites == 0)
        {
            return rows.Count == 1 ? "That file is still being written." : "Those files are still being written.";
        }

        return "Favourites and files being saved can't be deleted.";
    }

    /// <summary>What a delete that failed in part says: how many went, and the reason most of the others gave.</summary>
    public static string Failure(string code, int sent, int deleted)
    {
        var failed = sent - deleted;
        if (sent == 1)
        {
            return Refusal(code, "Couldn't delete the file.");
        }

        var count = failed == 1
            ? "1 file couldn't be deleted."
            : string.Create(CultureInfo.InvariantCulture, $"{failed} files couldn't be deleted.");
        return string.Create(CultureInfo.InvariantCulture, $"Deleted {deleted} of {sent}. ") + Refusal(code, count);
    }

    /// <summary>The engine's refusal of a rename or delete in the user's words, or <paramref name="otherwise"/>.</summary>
    public static string Refusal(string code, string otherwise) => code switch
    {
        ErrorCodes.FileBeingWritten => "That file is still being written.",
        ErrorCodes.InvalidFileName => "That name can't be used for a file.",
        ErrorCodes.FileNameTaken => "A file with that name already exists.",
        ErrorCodes.RecycleBinUnavailable => "That drive has no Recycle Bin, so the file wasn't deleted.",
        ErrorCodes.LibraryItemChanged => "That file changed; try again.",
        _ => otherwise,
    };
}
