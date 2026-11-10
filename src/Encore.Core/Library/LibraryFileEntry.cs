namespace Encore.Core.Library;

/// <summary>One saved item; its size and modified time tell a file changed outside the app. Fields: docs/code-notes.md.</summary>
internal sealed record LibraryFileEntry(string Path, string? ProfileId, DateTimeOffset Date, long? Size = null, double? LengthSeconds = null, DateTime? Modified = null, bool Favourite = false, string? Kind = null);
