using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.App.Settings;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// The surfaces outside the Main App window follow the theme (story 9.7, AD-23): the tray menu, the
/// dialog behind <see cref="Prompts"/> and the Overlay's message card. Each test scopes the theme to its
/// own window, as <see cref="ThemeTests"/> does. PNGs of each surface in both themes go beside the test
/// binary for the builder's check.
/// </summary>
[Collection(nameof(WpfCollection))]
public sealed class SurfacesThemeTests
{
    private static readonly Color GamingFlyout = Color.FromRgb(0x1E, 0x22, 0x2A);

    [Fact]
    public void TheTrayMenuResolvesItsStylesFromTheThemeAndFollowsASwitch() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var menu = ShellApp.CreateTrayMenu(model, () => { }, () => { }, gallery: () => { });
        var target = new Border { Width = 10, Height = 10, ContextMenu = menu };
        var window = Offscreen(new Window { Content = target, Width = 100, Height = 100 });
        var theme = ThemeService.ForWindow(window);
        theme.Apply(AppTheme.GamingDark);
        window.Show();
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = -30000;
        menu.VerticalOffset = -30000;
        menu.IsOpen = true;
        try
        {
            Sta.Pump(TimeSpan.FromMilliseconds(150));
            Assert.Same(theme.GamingDark[typeof(ContextMenu)], menu.Style);
            Assert.Equal(GamingFlyout, ((SolidColorBrush)menu.Background).Color);
            Assert.Same(theme.GamingDark[typeof(MenuItem)], menu.Items.OfType<MenuItem>().First().Style);
            Save(menu, "tray-menu-gaming-dark.png");

            theme.Apply(AppTheme.Fluent);
            Sta.Pump(TimeSpan.FromMilliseconds(150));
            Assert.NotSame(theme.GamingDark[typeof(ContextMenu)], menu.Style);
            Assert.NotEqual(GamingFlyout, ((SolidColorBrush)menu.Background).Color);
            Save(menu, "tray-menu-fluent.png");
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
        }
    });

    [Fact]
    public void TheDialogAnswersYesNoAndEscAsTheMessageBoxDidAndIsExcludedFromCapture() => Sta.Run(() =>
    {
        var view = new Border();
        var owner = Offscreen(new Window { Content = view, Width = 200, Height = 100 });
        owner.Show();
        var steps = new Queue<(AppTheme Theme, Action<PromptDialog> Answer)>([
            (AppTheme.GamingDark, d => d.YesButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))),
            (AppTheme.Fluent, d => d.NoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))),
            (AppTheme.GamingDark, d => d.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(d), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent })),
            (AppTheme.Fluent, d => d.Close()),
        ]);
        var seen = new List<(string Title, string Message, bool Excluded, bool YesIsDefault, Window? Owner)>();
        void OnShowing(PromptDialog dialog)
        {
            var (which, answer) = steps.Dequeue();
            ThemeService.ForWindow(dialog).Apply(which);
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                var hwnd = new WindowInteropHelper(dialog).Handle;
                seen.Add((dialog.Title, dialog.MessageText.Text, CaptureExclusion.IsApplied(hwnd), dialog.YesButton.IsDefault, dialog.Owner));
                if (seen.Count <= 2)
                {
                    Save(dialog, $"dialog-{Name(which)}.png");
                }

                answer(dialog);
            });
        }

        PromptDialog.Showing += OnShowing;
        try
        {
            Assert.True(Prompts.YesNo(view, "Move “clip.mp4” to the Recycle Bin?", "Delete", MessageBoxImage.Question));
            Assert.False(Prompts.YesNo(view, "Delete it?", "Delete", MessageBoxImage.Warning));
            Assert.False(Prompts.YesNo(view, "Delete it?", "Delete", MessageBoxImage.Question));
            Assert.False(Prompts.YesNo(view, "Delete it?", "Delete", MessageBoxImage.Question));
        }
        finally
        {
            PromptDialog.Showing -= OnShowing;
            owner.Close();
        }

        Assert.Equal(("Delete", "Move “clip.mp4” to the Recycle Bin?"), (seen[0].Title, seen[0].Message));
        Assert.All(seen, s => Assert.True(s.Excluded && s.YesIsDefault && s.Owner == owner));
    });

    [Fact]
    public void TheLibraryDeleteAndTheCleanupConfirmationStillGetTheirAnswers() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession { Reply = (_, _) => Empty.Value };
        var folder = Directory.CreateTempSubdirectory("rb-surfaces-").FullName;
        var answers = new Queue<bool>([true, false]);
        var asked = new List<(string Caption, string Message, Window? Owner)>();
        void OnShowing(PromptDialog dialog)
        {
            // No shown owner, so the dialog centres on the screen; the test moves it off.
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            (dialog.Left, dialog.Top, dialog.ShowActivated) = (-30000, -30000, false);
            asked.Add((dialog.Title, dialog.MessageText.Text, dialog.Owner));
            var yes = answers.Dequeue();
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => dialog.Choose(yes));
        }

        PromptDialog.Showing += OnShowing;
        try
        {
            var library = new LibraryPage(engine);
            var item = new LibraryItem(@"Game\clip.mp4", null, 48, DateTimeOffset.UnixEpoch, false);
            library.Render(engine.State! with { LibraryFolder = folder, Library = [item] });
            library.Click(library.Rows[0], ctrl: false, shift: false);
            library.DeleteCommand.Execute(null);
            Sta.Pump(TimeSpan.FromMilliseconds(50));
            Assert.IsType<LibraryDelete>(Assert.Single(engine.Sent));

            var cleanup = new LibrarySettingsPage(new SettingsSender(engine), engine);
            Assert.False(cleanup.Confirm("Delete 3 Clips?"));
        }
        finally
        {
            PromptDialog.Showing -= OnShowing;
            Directory.Delete(folder, recursive: true);
        }

        Assert.Equal([("Delete", "Move “clip.mp4” to the Recycle Bin?", null), ("Storage and Cleanup", "Delete 3 Clips?", null)], asked);
    });

    [Fact]
    public void TheOverlayCardTakesTheThemeWhileItsGlyphsKeepTheirColours() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        // The notice expires on a timer thread later, so the Overlay's updates go through the dispatcher.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var overlay = new OverlayWindow(new OverlayViewModel(engine, action => dispatcher.BeginInvoke(action)));
        var theme = ThemeService.ForWindow(overlay);
        try
        {
            theme.Apply(AppTheme.GamingDark);
            engine.State = engine.State! with
            {
                Warnings = [new Warning("a", "save-failed", WarningSeverity.Error, "capture", "The Clip couldn't be saved", DateTimeOffset.UtcNow, Overlay: true, Sound: null, Dismissable: true)],
            };
            engine.RaiseChanged();
            Sta.Pump(TimeSpan.FromMilliseconds(150));
            var root = (Grid)((Viewbox)overlay.Content).Child;
            var card = (Border)root.Children[1];
            var text = (TextBlock)card.Child;
            var glyphs = ((StackPanel)root.Children[0]).Children.OfType<FrameworkElement>().ToList();
            var fixedBrushes = GlyphColours(glyphs);

            Assert.True(overlay.AllowsTransparency && overlay.Background == Brushes.Transparent && !overlay.IsHitTestVisible && !overlay.ShowActivated);
            Assert.Equal(Color.FromArgb(0xE6, 0x1E, 0x22, 0x2A), ((SolidColorBrush)card.Background).Color);
            Assert.Equal(Color.FromRgb(0x26, 0x2B, 0x34), ((SolidColorBrush)card.BorderBrush).Color);
            Assert.Equal(new CornerRadius(4), card.CornerRadius);
            Assert.Equal(Color.FromRgb(0xFF, 0x46, 0x55), ((SolidColorBrush)text.Foreground).Color);
            Assert.Contains(Color.FromRgb(0xE5, 0x39, 0x35), fixedBrushes);
            Save(root, "overlay-card-gaming-dark.png");

            theme.Apply(AppTheme.Fluent);
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Same(overlay.FindResource("RbOverlayCardBrush"), card.Background);
            Assert.Same(overlay.FindResource("SystemFillColorCriticalBrush"), text.Foreground);
            Assert.Equal(fixedBrushes, GlyphColours(glyphs));
            Save(root, "overlay-card-fluent.png");
        }
        finally
        {
            overlay.Close();
        }
    });

    private static List<Color> GlyphColours(IEnumerable<FrameworkElement> glyphs) =>
        glyphs.SelectMany(g => g is Panel p ? p.Children.OfType<Shape>() : g is Shape s ? [s] : [])
            .SelectMany(s => new[] { s.Fill, s.Stroke })
            .OfType<SolidColorBrush>()
            .Select(b => b.Color)
            .ToList();

    private static string Name(AppTheme theme) => theme == AppTheme.Fluent ? "fluent" : "gaming-dark";

    private static T Offscreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    // The builder's hand check: the surface on a mid-grey backdrop (a game, for the Overlay), beside the test binary.
    private static void Save(FrameworkElement element, string name)
    {
        var target = element is Window { Content: FrameworkElement content } ? content : element;
        var size = new Size(Math.Max(1, target.ActualWidth), Math.Max(1, target.ActualHeight));
        var backdrop = element is Window { Background: SolidColorBrush { Color.A: 255 } solid } ? solid : new SolidColorBrush(Color.FromRgb(0x50, 0x58, 0x60));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(backdrop, null, new Rect(size));
        }

        const double Scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }
}
