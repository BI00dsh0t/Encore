namespace Encore.App.Library;

/// <summary>An option of a view combo: its value and display name.</summary>
internal sealed record LibraryChoice<T>(T Value, string Name);
