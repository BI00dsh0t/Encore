namespace Encore.Core.Library;

/// <summary>The index file: the saved items, the Output Folder they were saved for, and whether a rebuild is unacknowledged.</summary>
internal sealed record LibraryFile(
    int Version,
    IReadOnlyList<LibraryFileEntry> Items,
    string? OutputFolder = null,
    bool CleanupBlocked = false);
