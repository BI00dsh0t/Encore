namespace Encore.App.Library;

/// <summary>A group's header in the Library list; one instance per group key, its name kept current.</summary>
internal sealed class LibraryGroupHeader(string key) : Observable
{
    private string _name = "";

    public string Key { get; } = key;

    /// <summary>False: a header, which the list never selects or focuses.</summary>
    public bool IsRow => false;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
}
