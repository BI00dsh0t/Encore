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
    internal Func<ClipCreatorPage, Task<string?>> ChooseDestination { get; set; } = ClipExportDestination.ChooseAsync;
    private void ChooseType(object sender, RoutedEventArgs e)
    {
        if (Page is not { CanOpenOptions: true } page) { return; }
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = ExportArrow };
        foreach (var (format, label) in new[] { ("mp4", "Export as video"), ("mp3", "Export as audio") })
        {
            var item = new System.Windows.Controls.MenuItem { Header = label, IsCheckable = true, IsChecked = page.Format == format };
            item.Click += (_, _) => { page.Format = format; };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => ExportArrow.Focus();
        ExportArrow.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (Page is not { CanOpenOptions: true } page) { return; }
        var dialog = new ClipExportDialog(page);
        try
        {
            if (dialog.Ask(Window.GetWindow(this))) { await page.ChooseAndExportAsync(ChooseDestination); }
        }
        finally { ExportButton.Focus(); }
    }
}

internal static class ClipExportDestination
{
    internal static Task<string?> ChooseAsync(ClipCreatorPage page)
    {
        var owner = Application.Current?.Windows.OfType<ClipExportDialog>().FirstOrDefault(w => w.IsVisible)
            ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (page.SeparateAudioFiles)
        {
            var folder = new OpenFolderDialog { Title = "Choose the parent folder for the new named MP3 directory", Multiselect = false };
            return Task.FromResult(folder.ShowDialog(owner) == true ? Path.Combine(folder.FolderName, page.ExportName) : null);
        }
        var dialog = new SaveFileDialog
        {
            Title = page.SaveLabel, FileName = page.ExportName + "." + page.Format,
            Filter = page.IsMp4 ? "MP4 video|*.mp4" : "MP3 audio|*.mp3",
            AddExtension = true, DefaultExt = page.Format, OverwritePrompt = false, CheckFileExists = false,
        };
        return Task.FromResult(dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }
}
