using Encore.Contracts.Messages;

namespace Encore.Core.Library;

public sealed partial class LibraryActor
{
    private abstract record Message;

    private sealed record Start : Message;

    private sealed record Begin(string Path, string? ProfileId, string? Kind = null) : Message;

    private sealed record End(string Path, WriteOutcome Outcome) : Message;

    private sealed record Watched(WatcherChangeTypes Kind, string Path, string? OldPath) : Message;

    private sealed record Settle : Message;

    private sealed record WatchError(Exception Error) : Message;

    private sealed record Expired(string Path, long Id) : Message;

    private sealed record FolderChanged : Message;

    private sealed record Acknowledge : Message;

    private sealed record RetrySave : Message;

    private sealed record Watch(bool On, TaskCompletionSource Done) : Message;

    private sealed record LengthRead(string Folder, string Key, long Size, DateTime Modified, double? Length) : Message;

    private sealed record SetFavouriteRequest(LibrarySetFavourite Args, TaskCompletionSource Done) : Message;

    private sealed record RenameRequest(LibraryRename Args, TaskCompletionSource Done) : Message;

    private sealed record DeleteRequest(LibraryDelete Args, TaskCompletionSource Done) : Message;

    // A Cleanup trigger: a timer's, or a settings change (Changed) that counts only when the limits differ.
    private sealed record CleanupTick(bool Changed) : Message;

    private sealed record CleanupStep : Message;

    private sealed record PreviewRequest(LibraryCleanupPreview Args, TaskCompletionSource<LibraryCleanupPlan> Done) : Message;
}
