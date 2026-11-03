using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

public sealed class TrayTests
{
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--tray" }, true)]
    public void OnlyTheTrayArgumentStartsWithNoWindow(string[] args, bool inTray) =>
        Assert.Equal(inTray, ShellApp.StartsInTray(args));

    [Fact]
    public void WithNoInstanceArgumentTheAppRunsOnItsOwnInstance()
    {
        Assert.True(ShellApp.TryParseInstance(["--tray"], out var instance));
        Assert.Equal(Contracts.Instance.Current, instance);
    }

    [Fact]
    public void TheInstanceArgumentTakesATestInstance()
    {
        Assert.True(ShellApp.TryParseInstance(["--tray", "--instance", "ReplayBuffer-Test-x1"], out var instance));
        Assert.Equal("ReplayBuffer-Test-x1", instance.Name);
        Assert.True(instance.IsTest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ReplayBuffer")]
    public void AnyOtherInstanceArgumentIsRefused(string? name)
    {
        string[] args = name is null ? ["--tray", "--instance"] : ["--instance", name];

        Assert.False(ShellApp.TryParseInstance(args, out _));
    }

    [Fact]
    public void TheMenuOffersOpenStartWithWindowsAndQuitAndItsPopupIsExcludedFromCapture() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession { Reply = (_, _) => new Contracts.Messages.SettingsUpdateResult(1) };
        var model = new MainViewModel(engine, action => action());
        var opened = 0;
        var quit = 0;
        var menu = ShellApp.CreateTrayMenu(model, () => opened++, () => quit++);
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Open", "Start with Windows", "Quit"], items.Select(i => (string)i.Header));

        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = -32000;
        menu.VerticalOffset = -32000;
        menu.IsOpen = true;
        try
        {
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(menu));
            Assert.True(CaptureExclusion.IsApplied(source.Handle));

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
