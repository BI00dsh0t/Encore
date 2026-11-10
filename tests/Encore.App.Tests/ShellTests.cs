using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Theme;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The Main App's shell: the navigation, the page header and the status header, in both themes.</summary>
[Collection(nameof(WpfCollection))]
public sealed class ShellTests
{
    [Fact]
    public void EveryNavItemOpensItsPageAndTheOldSelectionPathsSelectTheirItem() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());

        Assert.Equal(
            ["Library", "Settings", .. model.SettingsViewModels.Select(p => p.Title)],
            model.NavItems.Select(i => i.Title));
        Assert.Same(model.LibraryNav, model.SelectedNavItem);
        foreach (var item in model.NavItems.Where(i => !i.IsSection))
        {
            model.SelectedNavItem = item;
            Assert.Equal(item.Title, model.CurrentTitle);
            Assert.Equal(item.ViewModel is not null, model.IsSettingsTabSelected);
            Assert.Same(item.ViewModel?.View ?? model.Library.View, model.CurrentView);
        }

        // The section label is never selected, and the tray's path (IsSettingsTabSelected) selects the page's item.
        model.SelectedNavItem = model.NavItems[1];
        Assert.Same(model.LibrarySettings, model.SelectedNavItem.ViewModel);
        model.IsSettingsTabSelected = false;
        model.SelectedSettingsViewModel = model.Hotkeys;
        Assert.Same(model.LibraryNav, model.SelectedNavItem);
        model.IsSettingsTabSelected = true;
        Assert.Same(model.Hotkeys, model.SelectedNavItem.ViewModel);

        // Ctrl+Tab steps over the label and wraps round.
        model.SelectedNavItem = model.LibraryNav;
        model.NavigateBy(1);
        Assert.Same(model.General, model.SelectedSettingsViewModel);
        model.NavigateBy(-1);
        model.NavigateBy(-1);
        Assert.Same(model.LibrarySettings, model.SelectedNavItem.ViewModel);
    });

    [Fact]
    public void TheShellLoadsInBothThemesAndTheNavOpensEveryPage() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            Capture = CaptureState.Initial with { Replay = ReplayState.Buffering },
            TargetGame = new TargetGame(@"c:\games\valorant.exe", "Valorant", 7),
            Warnings = [new Warning("w1", "mic.unavailable", WarningSeverity.Warning, "audio", "The Mic device is unavailable; its Track records silence.", DateTimeOffset.Now, false, null, true)],
        };
        var model = new MainViewModel(engine, action => action());
        var window = new MainWindow
        {
            DataContext = model,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        var theme = ThemeService.ForWindow(window);
        var nav = (ListBox)window.FindName("Nav");
        var sections = (ComboBox)window.FindName("SettingsSections");
        var pageHost = (ContentControl)window.FindName("Page");
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                theme.Apply(which);
                window.Show();
                Sta.Pump(TimeSpan.FromMilliseconds(150));

                // The nav list holds the Library; the settings pages are chosen in the section list.
                Assert.Equal([model.LibraryNav], nav.Items.Cast<NavItem>());
                Assert.Equal(model.SettingsViewModels.Select(p => p.Title), sections.Items.Cast<NavItem>().Select(i => i.Title));
                foreach (var item in model.NavItems.Where(i => !i.IsSection))
                {
                    (item.ViewModel is null ? (System.Windows.Controls.Primitives.Selector)nav : sections).SelectedItem = item;
                    Sta.Pump(TimeSpan.FromMilliseconds(30));
                    Assert.Same(item.ViewModel?.View ?? model.Library.View, pageHost.Content);
                    Assert.True(((FrameworkElement)pageHost.Content).IsLoaded, $"{item.Title} didn't load under {which}");
                }

                var peer = UIElementAutomationPeer.CreatePeerForElement(nav);
                Assert.Single(peer.GetChildren());
                var library = (ListBoxItem)nav.ItemContainerGenerator.ContainerFromItem(model.LibraryNav);
                var fill = (Border)library.Template.FindName("Fill", library);
                NavPress.SetIsPressed(library, true);
                var pressed = (SolidColorBrush)window.FindResource("SubtleFillColorTertiaryBrush");
                Assert.Equal(pressed.Color, ((SolidColorBrush)fill.Background).Color);
                NavPress.SetIsPressed(library, false);
                Assert.Equal("Library", ((TextBlock)library.Template.FindName("Label", library)).Text);

                model.SelectedNavItem = model.NavItems.First(i => i.ViewModel == model.General);
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Save(window, $"shell-{(which == AppTheme.Fluent ? "fluent" : "gaming-dark")}.png");
                model.SelectedNavItem = model.LibraryNav;
            }
        }
        finally
        {
            window.CloseForQuit();
        }
    });

    // The builder's hand check: the shell as it renders, beside the test binary.
    private static void Save(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }
}
