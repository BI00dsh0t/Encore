using Encore.Contracts.Messages;

namespace Encore.Core.Library;

/// <summary>What a Cleanup pass would delete now, oldest first; Unreachable means the size limit can't be met.</summary>
internal sealed record CleanupPlan(IReadOnlyList<LibraryItem> Items, long Bytes, long Remaining, bool Unreachable);
