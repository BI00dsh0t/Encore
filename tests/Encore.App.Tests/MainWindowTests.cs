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
    public void TheSettingsButtonOpensOneSettingsWindowAndEscOrHidingTheAppHidesIt() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var window = new MainWindow { DataContext = model, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var navigation = (ListBox)window.FindName("Nav");
            var button = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SettingsButton");
            void ClickSettings() => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Single(navigation.Items);
            model.IsNavigationExpanded = false;
            window.UpdateLayout();
            Assert.Equal(58, ((ColumnDefinition)window.FindName("NavColumn")).Width.Value);

            // The window opens beside the Library; pressed again, the button brings back the same window.
            ClickSettings();
            var settings = Assert.IsType<SettingsWindow>(window.Settings);
            Assert.True(settings.IsVisible);
            Assert.Same(window, settings.Owner);
            Assert.True(model.Settings.IsShown);
            Assert.True(button.IsChecked);
            Assert.Same(model.Library.View, model.CurrentView);
            ClickSettings();
            Assert.Same(settings, window.Settings);
            Assert.Single(window.OwnedWindows);
            Assert.True(button.IsChecked);

            // Esc hides it, and so does the Main App hiding to the tray.
            settings.RaiseEvent(Escape(settings));
            Assert.False(settings.IsVisible);
            Assert.False(model.Settings.IsShown);
            Assert.False(button.IsChecked);
            ClickSettings();
            Assert.True(settings.IsVisible);
            window.Close();
            Assert.False(settings.IsVisible);
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

            // The first-use tips' Change hotkeys link opens the window at Hotkeys.
            model.Library.Tips.ChangeHotkeysCommand.Execute(null);
            var settings = Assert.IsType<SettingsWindow>(window.Settings);
            settings.UpdateLayout();
            var sections = (ListBox)settings.FindName("Sections");
            var page = (ContentControl)settings.FindName("Page");
            Assert.True(settings.IsVisible);
            Assert.Same(model.Hotkeys.View, page.Content);
            Assert.Same(model.Settings.Sections.Single(s => s.ViewModel == model.Hotkeys), sections.SelectedItem);

            // Choosing a section in the list shows its page and title.
            var soundsSection = model.Settings.Sections.Single(s => s.ViewModel == model.FeedbackSounds);
            var sounds = (ListBoxItem)sections.ItemContainerGenerator.ContainerFromItem(soundsSection);
            sounds.IsSelected = true;
            Assert.Same(model.FeedbackSounds.View, page.Content);
            Assert.Equal("Sounds", ((ContentControl)settings.FindName("PageTitle")).Content);

            // A game's page: choosing Games again, then Esc, each go back to the list before Esc closes the window.
            model.Settings.Select(model.GameProfiles);
            var drillIn = model.GameProfiles.DrillIn;
            drillIn.Open("test-game", "Test game");
            var games = (ListBoxItem)sections.ItemContainerGenerator.ContainerFromItem(model.Settings.SelectedSection);
            // The left-button event is direct, so the click is raised as the tunnelling mouse-up the section list hears.
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
            games.RaiseEvent(click);
            Assert.False(drillIn.IsOpen);
            drillIn.Open("test-game", "Test game");
            settings.RaiseEvent(Escape(settings));
            Assert.False(drillIn.IsOpen);
            Assert.True(settings.IsVisible);
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
