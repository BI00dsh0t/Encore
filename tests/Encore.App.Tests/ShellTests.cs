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
    public void EverySettingsSectionOpensItsPageAndTheMainWindowStaysOnTheLibrary() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());

        Assert.Equal([model.LibraryNav], model.NavItems);
        Assert.Equal(model.SettingsViewModels.Select(p => p.Title), model.Settings.Sections.Select(i => i.Title));
        foreach (var section in model.Settings.Sections)
        {
            model.Settings.SelectedSection = section;
            Assert.Equal(section.Title, model.Settings.Title);
            Assert.Same(section.ViewModel!.View, model.Settings.CurrentView);
            Assert.Same(model.Library.View, model.CurrentView);
        }

        // A cleared list selection keeps the page shown.
        model.Settings.SelectedSection = null!;
        Assert.Same(model.LibrarySettings, model.Settings.SelectedPage);
    });

    [Fact]
    public void TheShellAndTheSettingsOverlayLoadInBothThemesWithAGamesPageOpen() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        engine.State = engine.State! with
        {
            Capture = CaptureState.Initial with { Replay = ReplayState.Buffering },
            TargetGame = new TargetGame(@"c:\games\valorant.exe", "Valorant", 7),
            Warnings = [new Warning("w1", "mic.unavailable", WarningSeverity.Warning, "audio", "The Mic device is unavailable; its Track records silence.", DateTimeOffset.Now, false, null, true)],
            Settings = SettingsDocument.Empty with
            {
                Revision = 1,
                Profiles = SettingsMaps.EmptyProfiles.Add(@"c:\games\valorant.exe", new GameProfile { Name = "Valorant" }),
            },
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
        try
        {
            foreach (var which in new[] { AppTheme.Fluent, AppTheme.GamingDark })
            {
                var name = which == AppTheme.Fluent ? "fluent" : "gaming-dark";
                theme.Apply(which);
                window.Show();
                Sta.Pump(TimeSpan.FromMilliseconds(150));

                // The nav list holds the Library; no section list is left in the main window.
                Assert.Equal([model.LibraryNav], nav.Items.Cast<NavItem>());
                Assert.Null(window.FindName("SettingsSections"));
                Assert.Single(UIElementAutomationPeer.CreatePeerForElement(nav).GetChildren());
                var library = (ListBoxItem)nav.ItemContainerGenerator.ContainerFromItem(model.LibraryNav);
                var fill = (Border)library.Template.FindName("Fill", library);
                NavPress.SetIsPressed(library, true);
                var pressed = (SolidColorBrush)window.FindResource("SubtleFillColorTertiaryBrush");
                Assert.Equal(pressed.Color, ((SolidColorBrush)fill.Background).Color);
                NavPress.SetIsPressed(library, false);
                Assert.Equal("Library", ((TextBlock)library.Template.FindName("Label", library)).Text);
                Save(window, $"shell-{name}.png");

                // Settings over the window, each section through its list, then a game's page with its breadcrumb.
                model.OpenSettings(model.General);
                var settings = window.SettingsOverlay;
                var sections = (ListBox)settings.FindName("Sections");
                var pageHost = (ContentControl)settings.FindName("Page");
                foreach (var section in model.Settings.Sections)
                {
                    sections.SelectedItem = section;
                    Sta.Pump(TimeSpan.FromMilliseconds(30));
                    Assert.Same(section.ViewModel!.View, pageHost.Content);
                    Assert.True(((FrameworkElement)pageHost.Content).IsLoaded, $"{section.Title} didn't load under {which}");
                }

                model.Settings.Select(model.General);
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Save(window, $"settings-overlay-{name}.png");
                model.Settings.Select(model.GameProfiles);
                model.GameProfiles.Open(model.GameProfiles.Profiles[0]);
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(((FrameworkElement)settings.FindName("Breadcrumb")).IsVisible);
                Save(window, $"settings-overlay-games-{name}.png");
                model.GameProfiles.DrillIn.Close();
                model.Settings.Close();
                Sta.Pump(TimeSpan.FromMilliseconds(250));
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
