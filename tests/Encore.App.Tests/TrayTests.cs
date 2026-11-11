using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Encore.App.Overlay;
using Encore.Contracts.Settings;
using Encore.Platform.Desktop;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class TrayTests
{
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--tray" }, true)]
    public void OnlyTheTrayArgumentStartsWithNoWindow(string[] args, bool inTray) =>
        Assert.Equal(inTray, AppArguments.StartsInTray(args));

    [Fact]
    public void WithNoInstanceArgumentTheAppRunsOnItsOwnInstance()
    {
        Assert.True(AppArguments.TryParseInstance(["--tray"], out var instance));
        Assert.Equal(Contracts.Instance.Current, instance);
    }

    [Fact]
    public void TheInstanceArgumentTakesATestInstance()
    {
        Assert.True(AppArguments.TryParseInstance(["--tray", "--instance", "Encore-Test-x1"], out var instance));
        Assert.Equal("Encore-Test-x1", instance.Name);
        Assert.True(instance.IsTest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Encore")]
    [InlineData("Encore-Test-")]
    public void AnyOtherInstanceArgumentIsRefused(string? name)
    {
        string[] args = name is null ? ["--tray", "--instance"] : ["--instance", name];

        Assert.False(AppArguments.TryParseInstance(args, out _));
    }

    [Fact]
    public void TheRestartItemIsHiddenUntilACrashLoopAndRestartsTheEngine() => Sta.Run(() =>
    {
        var model = new MainViewModel(new FakeEngineSession(), action => action());
        var restarts = 0;
        var menu = TrayMenu.Create(model, () => { }, () => { }, () => restarts++);

        var restart = menu.Items.OfType<MenuItem>().Single(i => i.Name == TrayMenu.RestartItemName);
        Assert.Equal(Visibility.Collapsed, restart.Visibility);
        restart.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, restarts);

        TrayMenu.ShowCrashLoop(restart, crashLoop: true);
        Assert.Equal(Visibility.Visible, restart.Visibility);
        TrayMenu.ShowCrashLoop(restart, crashLoop: false);
        Assert.Equal(Visibility.Collapsed, restart.Visibility);
    });

    [Theory]
    [InlineData((int)OverlayDot.None, false, false, false, TrayIcons.IdleDark)]
    [InlineData((int)OverlayDot.None, false, true, false, TrayIcons.IdleLight)]
    [InlineData((int)OverlayDot.Hollow, false, true, false, TrayIcons.Ready)]
    [InlineData((int)OverlayDot.Solid, false, false, false, TrayIcons.Recording)]
    [InlineData((int)OverlayDot.Hollow, true, false, false, TrayIcons.ErrorDark)]
    [InlineData((int)OverlayDot.None, true, true, false, TrayIcons.ErrorLight)]
    [InlineData((int)OverlayDot.Solid, true, true, true, TrayIcons.IdleLight)]
    public void EachStateShowsItsIconAndHighContrastShowsThePlainMark(
        int dot, bool crashLoop, bool lightTaskbar, bool highContrast, string expected) =>
        Assert.Equal(expected, TrayCapture.IconName((OverlayDot)dot, crashLoop, new TaskbarLook(lightTaskbar, highContrast)));

    [Fact]
    public void EveryTrayIconLoadsAtTheTraySizes()
    {
        foreach (var name in TrayIcons.All)
        {
            foreach (var size in new[] { 16, 24, 32, 48 })
            {
                using var icon = TrayIcons.Load(name, size);
                Assert.Equal((size, size), (icon.Width, icon.Height));
            }
        }

        Assert.InRange(TrayIconSize.Current(), 16, 64);
    }

    [Fact]
    public void RewindIsAcceptedOnATestInstanceOnly()
    {
        Assert.True(AppArguments.TryParse(["--tray", "--instance", "Encore-Test-x1", "--rewind"], out var instance));
        Assert.True(instance.IsTest);
        Assert.False(AppArguments.TryParse(["--tray", "--rewind"], out _));
    }

    [Fact]
    public void TheMenuOffersOpenStartWithWindowsAndQuitAndItsPopupAllowsCapture() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession { Reply = (_, _) => new Contracts.Messages.SettingsUpdateResult(1) };
        var model = new MainViewModel(engine, action => action());
        var opened = 0;
        var quit = 0;
        var folders = new List<string>();
        var menu = TrayMenu.Create(model, () => opened++, () => quit++, openFolder: folders.Add);
        var all = menu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).ToList();

        // The live status first, then Open, Open Library and Open the Clips folder.
        Assert.Equal(["Waiting for a game · Mic muted", "Open", "Open Library", "Open the Clips folder", "Start with Windows", "Quit"], all.Select(i => (string)i.Header));
        Assert.False(all[0].IsEnabled);
        engine.State = engine.State! with { Capture = engine.State.Capture with { Replay = Contracts.Messages.ReplayState.Buffering, MicMuted = false }, LibraryFolder = @"C:\Clips" };
        engine.RaiseChanged();
        Assert.Equal("Ready to save Clips", (string)all[0].Header);
        model.SelectedNavItem = model.NavItems.First(i => i.ViewModel == model.General);
        all[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(model.LibraryNav, model.SelectedNavItem);
        all[3].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([@"C:\Clips"], folders);
        opened = 0;
        var items = all.Where(i => (string)i.Header is "Open" or "Start with Windows" or "Quit").ToList();

        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = -32000;
        menu.VerticalOffset = -32000;
        menu.IsOpen = true;
        try
        {
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(menu));
            Assert.False(CaptureExclusion.IsApplied(source.Handle));

            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal((1, 1), (opened, quit));

            Assert.False(items[1].IsChecked);
            items[1].IsChecked = true;
            Assert.True(model.General.StartWithWindows);
            var update = Assert.IsType<SettingsUpdate>(Assert.Single(engine.Sent));
            Assert.Equal("startWithWindows", Assert.Single(update.Ops).Key);
        }
        finally
        {
            menu.IsOpen = false;
        }
    });
}
