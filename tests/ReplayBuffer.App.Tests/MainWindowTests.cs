using System.Windows.Interop;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class MainWindowTests
{
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
