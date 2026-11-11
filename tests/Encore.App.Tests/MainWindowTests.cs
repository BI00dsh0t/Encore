using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Encore.Platform.Desktop;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class MainWindowTests
{
    [Fact]
    public void TheSettingsButtonOpensOneOverlayThatEscTheCrossAndTheScrimCloseAndTheLibraryCannotTakeFocus() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var window = new MainWindow { DataContext = model, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var overlay = window.SettingsOverlay;
            var button = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SettingsButton");
            var search = (TextBox)model.Library.View.FindName("SearchBox");
            void ClickSettings()
            {
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Sta.Pump(TimeSpan.FromMilliseconds(250));
            }

            void AssertClosed()
            {
                Sta.Pump(TimeSpan.FromMilliseconds(250));
                Assert.False(model.Settings.IsOpen);
                Assert.False(overlay.IsVisible);
                Assert.False(button.IsChecked);
                Assert.True(search.Focus(), "The Library takes focus again once Settings closes");
            }

            Assert.Single(((ListBox)window.FindName("Nav")).Items);
            Assert.True(search.Focus());

            // Settings opens over the Library, which then takes no focus; pressed again, the button opens no second overlay.
            ClickSettings();
            Assert.True(model.Settings.IsOpen);
            Assert.True(overlay.IsVisible);
            Assert.True(button.IsChecked);
            Assert.True(overlay.IsKeyboardFocusWithin);
            Assert.False(search.Focus());
            Assert.True(overlay.IsKeyboardFocusWithin);
            ClickSettings();
            Assert.True(model.Settings.IsOpen);
            Assert.True(overlay.IsVisible);
            Assert.Equal(1.0, overlay.Opacity, 2);

            // Esc closes it, then the cross, then a click on the scrim.
            overlay.RaiseEvent(Escape(window));
            AssertClosed();
            ClickSettings();
            ((Button)overlay.FindName("CloseButton")).Command.Execute(null);
            AssertClosed();
            ClickSettings();
            var scrim = (Border)overlay.FindName("Scrim");
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            };
            scrim.RaiseEvent(press);
            AssertClosed();
        }
        finally { window.CloseForQuit(); }
    });

    [Fact]
    public void ALinkOpensSettingsAtItsPageAndTheSectionListShowsAndReturnsFromPages() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var window = new MainWindow { DataContext = model, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();

            // The first-use tips' Change hotkeys link opens Settings at Hotkeys.
            model.Library.Tips.ChangeHotkeysCommand.Execute(null);
            Sta.Pump(TimeSpan.FromMilliseconds(250));
            var overlay = window.SettingsOverlay;
            var sections = (ListBox)overlay.FindName("Sections");
            var page = (ContentControl)overlay.FindName("Page");
            Assert.True(overlay.IsVisible);
            Assert.Same(model.Hotkeys.View, page.Content);
            Assert.Same(model.Settings.Sections.Single(s => s.ViewModel == model.Hotkeys), sections.SelectedItem);

            // Choosing a section in the list shows its page and title; a link while it is open switches the section.
            var soundsSection = model.Settings.Sections.Single(s => s.ViewModel == model.FeedbackSounds);
            var sounds = (ListBoxItem)sections.ItemContainerGenerator.ContainerFromItem(soundsSection);
            sounds.IsSelected = true;
            Assert.Same(model.FeedbackSounds.View, page.Content);
            Assert.Equal("Sounds", ((ContentControl)overlay.FindName("PageTitle")).Content);
            model.Library.Tips.ChangeHotkeysCommand.Execute(null);
            Assert.Same(model.Hotkeys.View, page.Content);

            // A game's page: choosing Games again, then Esc, each go back to the list before Esc closes Settings.
            model.Settings.Select(model.GameProfiles);
            var drillIn = model.GameProfiles.DrillIn;
            drillIn.Open("test-game", "Test game");
            sections.UpdateLayout();
            var games = (ListBoxItem)sections.ItemContainerGenerator.ContainerFromItem(model.Settings.SelectedSection);
            // The left-button event is direct, so the click is raised as the tunnelling mouse-up the section list hears.
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
            games.RaiseEvent(click);
            Assert.False(drillIn.IsOpen);
            drillIn.Open("test-game", "Test game");
            overlay.RaiseEvent(Escape(window));
            Assert.False(drillIn.IsOpen);
            Assert.True(model.Settings.IsOpen);
        }
        finally { window.CloseForQuit(); }
    });

    private static KeyEventArgs Escape(Window window) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };

    [Fact]
    public void TheWindowAllowsCaptureAndClosingItOnlyHidesIt() => Sta.Run(() =>
    {
        var window = new MainWindow
        {
            DataContext = new MainViewModel(new FakeEngineSession(), action => action()),
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Assert.False(CaptureExclusion.IsApplied(hwnd));

            window.Open();
            Assert.True(window.IsVisible);
            window.Close();

            Assert.False(window.IsVisible);
            Assert.Equal(hwnd, new WindowInteropHelper(window).Handle);
            window.Open();
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.CloseForQuit();
        }

        Assert.Equal(0, new WindowInteropHelper(window).Handle);
    });
}
