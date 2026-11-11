using System.Windows.Media;

namespace Encore.App.Settings;

/// <summary>What the Games pages ask of the Windows shell; the App never opens the program itself. Tests fake it.</summary>
internal interface IGameShell
{
    /// <summary>Shows the program file selected in Explorer.</summary>
    void ShowProgram(string path);

    /// <summary>Opens a folder in Explorer.</summary>
    void OpenFolder(string folder);

    /// <summary>Loads the program's icon off the UI thread and hands it over on the UI thread; nothing when it has none.</summary>
    void LoadIcon(string path, Action<ImageSource> loaded);
}
