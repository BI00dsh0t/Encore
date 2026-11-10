namespace Encore.App.Library;

/// <summary>One entry of the game filter, one per game key, so a renamed profile keeps its selection; null is "All games".</summary>
internal sealed class LibraryGameOption(string? key) : Observable
{
    private string _name = "";

    public string? Key { get; } = key;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
}
