using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.Platform.Desktop;

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

/// <summary>The real shell: Explorer's select, ShellExecute open, and the shell's icon of the program file.</summary>
internal sealed class WindowsGameShell : IGameShell
{
    private const int IconSize = 64;

    public static WindowsGameShell Instance { get; } = new();

    public void ShowProgram(string path)
    {
        if (!ShellFolder.SelectInExplorer(path))
        {
            OpenFolder(Path.GetDirectoryName(path) ?? path);
        }
    }

    public void OpenFolder(string folder)
    {
        using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
    }

    // The shell reads the file and can be slow, so on an STA thread of its own; the frozen image crosses back.
    public void LoadIcon(string path, Action<ImageSource> loaded)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !File.Exists(path))
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                if (ShellThumbnail.TryGetIcon(path, IconSize) is { } image)
                {
                    var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Bgra, image.Width * 4);
                    bitmap.Freeze();
                    dispatcher.BeginInvoke(() => loaded(bitmap));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // No icon: the row keeps its letter tile.
            }
        })
        {
            IsBackground = true,
            Name = "GameIcon",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
