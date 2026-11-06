using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class MainWindowTests
{
    // Shared-shell execution remains deferred under the clip-creator-only clearance.
    [Fact]
    public void NavigationWidthRetainsCurrentViewAndSettingsUsesASeparateBottomRoute() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var window = new MainWindow { DataContext = model, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var view = model.CurrentView;
            var selected = model.SelectedNavItem;
            var navigation = (System.Windows.Controls.ListBox)window.FindName("Nav");
            Assert.Single(navigation.Items);
            model.IsNavigationExpanded = false;
            window.UpdateLayout();
            Assert.Equal(58, ((System.Windows.Controls.ColumnDefinition)window.FindName("NavColumn")).Width.Value);
            Assert.Same(view, model.CurrentView);
            Assert.Same(selected, model.SelectedNavItem);
            window.Width = 600; window.UpdateLayout();
            Assert.Equal(58, ((System.Windows.Controls.ColumnDefinition)window.FindName("NavColumn")).Width.Value);
            var settings = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SettingsButton");
            settings.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(model.IsSettingsTabSelected);
            Assert.Same(model.Pages[0], model.SelectedPage);
            Assert.False(model.IsNavigationExpanded);
            var sections = (System.Windows.Controls.ComboBox)window.FindName("SettingsSections");
            Assert.Equal(model.Pages.Select(p => p.Title), sections.Items.Cast<NavItem>().Select(i => i.Title));
            model.IsNavigationExpanded = true;
            Assert.Same(model.SelectedPage.View, model.CurrentView);
        }
        finally { window.CloseForQuit(); }
    });

    // Regression written for review; shell/Settings execution remains deferred.
    [Fact]
    public void CurrentSettingsSectionReselectionClosesDrillInWhileEscapeOnlyDismissesTheMenu() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        model.SelectedNavItem = model.SettingsNavItems.Single(item => ReferenceEquals(item.Page, model.GameProfiles));
        var window = new MainWindow { DataContext = model, Width = 960, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var sections = (ComboBox)window.FindName("SettingsSections");
            var drillIn = model.CurrentDrillIn!;
            void OpenSectionMenu()
            {
                drillIn.Open("test-game", "Test game");
                sections.Focus();
                sections.IsDropDownOpen = true;
                window.UpdateLayout();
            }
            OpenSectionMenu();
            var current = (ComboBoxItem)sections.ItemContainerGenerator.ContainerFromItem(model.SelectedNavItem);
            current.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Assert.False(drillIn.IsOpen);
            sections.IsDropDownOpen = false;

            foreach (var key in new[] { Key.Enter, Key.Space })
            {
                OpenSectionMenu();
                sections.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sections), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(drillIn.IsOpen);
                Assert.False(sections.IsDropDownOpen);
            }

            OpenSectionMenu();
            sections.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sections), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.False(sections.IsDropDownOpen);
            Assert.True(drillIn.IsOpen);
        }
        finally { window.CloseForQuit(); }
    });

    [Fact]
    public void TheWindowIsExcludedFromCaptureAndClosingItOnlyHidesIt() => Sta.Run(() =>
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
            Assert.True(CaptureExclusion.IsApplied(hwnd));

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
