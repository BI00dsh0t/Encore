using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Encore.App.Library;

/// <summary>The clip creator's view: it runs the playhead's clock and opens the export menu and dialogs.</summary>
internal partial class ClipCreatorView
{
    // How often the playhead follows the preview.
    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _position;

    public ClipCreatorView()
    {
        InitializeComponent();
        _position = new DispatcherTimer(PositionInterval, DispatcherPriority.Background, (_, _) => Page?.Tick(), Dispatcher)
        {
            IsEnabled = false,
        };
        Loaded += (_, _) => _position.Start();
        Unloaded += (_, _) => _position.Stop();
    }

    /// <summary>Asks where the export goes; tests replace the Windows dialogs.</summary>
    internal Func<ClipCreatorViewModel, Task<string?>> ChooseDestination { get; set; } = ClipExportDestination.ChooseAsync;

    private ClipCreatorViewModel? Page => DataContext as ClipCreatorViewModel;

    /// <summary>Focuses the timeline, when the creator opens.</summary>
    public void FocusFirstControl() => Timeline.Focus();

    private void OnKeyboardHelpClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dialog = new ClipShortcutsDialog { Owner = owner };
        if (owner is not null)
        {
            dialog.MaxHeight = Math.Max(240, owner.ActualHeight - 32);
        }

        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            ((Button)sender).Focus();
        }
    }

    // The time boxes commit on Enter as well as on leaving them.
    private void OnTimeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void OnStartMinusClick(object sender, RoutedEventArgs e) => Page?.Nudge(false, -1);

    private void OnStartPlusClick(object sender, RoutedEventArgs e) => Page?.Nudge(false, 1);

    private void OnEndMinusClick(object sender, RoutedEventArgs e) => Page?.Nudge(true, -1);

    private void OnEndPlusClick(object sender, RoutedEventArgs e) => Page?.Nudge(true, 1);

    // The arrow beside Export: a menu to choose video or audio, with the current one ticked.
    private void OnExportTypeClick(object sender, RoutedEventArgs e)
    {
        if (Page is not { CanOpenOptions: true } page)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = ExportArrow };
        menu.SetResourceReference(StyleProperty, "EncoreClipMenuStyle");
        menu.Items.Add(FormatItem(page, "mp4", "Export as video", "VideoIcon"));
        menu.Items.Add(FormatItem(page, "mp3", "Export as audio", "AudioIcon"));
        menu.Closed += (_, _) => ExportArrow.Focus();
        ExportArrow.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private MenuItem FormatItem(ClipCreatorViewModel page, string format, string label, string iconKey)
    {
        var icon = new Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            Data = (Geometry)FindResource(iconKey),
        };
        icon.SetResourceReference(Shape.StrokeProperty, "TextFillColorPrimaryBrush");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(icon);
        header.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(8, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.Normal,
        });
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = page.Format == format };
        AutomationProperties.SetName(item, label);
        item.SetResourceReference(StyleProperty, "EncoreClipMenuItemStyle");
        item.Click += (_, _) => page.Format = format;
        return item;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (Page is not { CanOpenOptions: true } page)
        {
            return;
        }

        var dialog = new ClipExportDialog(page);
        try
        {
            if (dialog.Ask(Window.GetWindow(this)))
            {
                await page.ChooseAndExportAsync(ChooseDestination);
            }
        }
        finally
        {
            ExportButton.Focus();
        }
    }
}
