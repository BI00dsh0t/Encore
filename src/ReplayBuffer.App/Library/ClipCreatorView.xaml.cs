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
    private void CommitTime(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) { return; }
        ((System.Windows.Controls.TextBox)sender).GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
    private void StartMinus(object sender, RoutedEventArgs e) => Page?.Nudge(false, -1);
    private void StartPlus(object sender, RoutedEventArgs e) => Page?.Nudge(false, 1);
    private void EndMinus(object sender, RoutedEventArgs e) => Page?.Nudge(true, -1);
    private void EndPlus(object sender, RoutedEventArgs e) => Page?.Nudge(true, 1);
    internal Func<ClipCreatorPage, Task<string?>> ChooseDestination { get; set; } = ClipExportDestination.ChooseAsync;
    private void ChooseType(object sender, RoutedEventArgs e)
    {
        if (Page is not { CanOpenOptions: true } page) { return; }
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = ExportArrow };
        menu.SetResourceReference(StyleProperty, "RbClipMenuStyle");
        foreach (var (format, label) in new[] { ("mp4", "Export as video"), ("mp3", "Export as audio") })
        {
            var header = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var icon = new System.Windows.Shapes.Path
            {
                Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 1.5,
                Data = System.Windows.Media.Geometry.Parse(format == "mp4"
                    ? "M 1,1 L 15,1 L 15,15 L 1,15 Z M 5,1 L 5,15 M 11,1 L 11,15 M 1,5 L 5,5 M 1,11 L 5,11 M 11,5 L 15,5 M 11,11 L 15,11"
                    : "M 6,12 L 6,3 L 14,1 L 14,10 M 6,3 L 14,1 M 6,12 C 6,16 0,16 0,13 C 0,10 6,10 6,12 M 14,10 C 14,14 8,14 8,11 C 8,8 14,8 14,10"),
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorPrimaryBrush");
            header.Children.Add(icon);
            header.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14, FontWeight = FontWeights.Normal });
            var item = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = page.Format == format };
            System.Windows.Automation.AutomationProperties.SetName(item, label);
            item.SetResourceReference(StyleProperty, "RbClipMenuItemStyle");
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
