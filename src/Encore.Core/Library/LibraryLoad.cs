using Encore.Contracts.Messages;

namespace Encore.Core.Library;

/// <summary>What the load found at start; Corrupt means the index was rebuilt and Cleanup stays blocked.</summary>
public sealed record LibraryLoad(IReadOnlyList<LibraryItem> Items, bool Corrupt);
