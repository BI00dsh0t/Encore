using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ReplayBuffer.App.Library;

internal partial class ClipCreatorView
{
    private readonly DispatcherTimer _position;
    public ClipCreatorView()
    {
        InitializeComponent();
        _position = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Page?.Tick(), Dispatcher) { IsEnabled = false };
        Loaded += (_, _) => _position.Start();
        Unloaded += (_, _) => _position.Stop();
    }
    private ClipCreatorPage? Page => DataContext as ClipCreatorPage;
    public void FocusFirstControl() => Timeline.Focus();
    private void StartMinus(object sender, RoutedEventArgs e) => Page?.Nudge(false, -1);
    private void StartPlus(object sender, RoutedEventArgs e) => Page?.Nudge(false, 1);
    private void EndMinus(object sender, RoutedEventArgs e) => Page?.Nudge(true, -1);
    private void EndPlus(object sender, RoutedEventArgs e) => Page?.Nudge(true, 1);
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (Page is not { CanExport: true } page) { return; }
        var separate = page.Format == "mp3" && !page.Mix;
        // A save picker supplies both a name and destination; a separate MP3 selection names a directory.
        var dialog = new SaveFileDialog
        {
            Title = separate ? "Name the new folder for separate MP3 tracks" : "Export clip",
            FileName = Path.GetFileNameWithoutExtension(page.Source.Path) + " clip" + (separate ? " tracks" : "." + page.Format),
            Filter = separate ? "New track folder name|*" : page.Format == "mp4" ? "MP4 video|*.mp4" : "MP3 audio|*.mp3",
            AddExtension = !separate, DefaultExt = separate ? "" : page.Format,
            OverwritePrompt = false, CheckFileExists = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { await page.ExportAsync(dialog.FileName); }
    }
}
