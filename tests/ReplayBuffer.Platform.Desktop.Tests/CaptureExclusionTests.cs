using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;

namespace ReplayBuffer.Platform.Desktop.Tests;

public sealed class CaptureExclusionTests
{
    [Fact]
    public void ApplyExcludesAWindowFromCapture() => OnSta(() =>
    {
        var window = new Window();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Assert.False(CaptureExclusion.IsApplied(hwnd));

            CaptureExclusion.Apply(hwnd);

            Assert.True(CaptureExclusion.IsApplied(hwnd));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ApplyToAHandleThatIsNoWindowThrows()
    {
        Assert.Throws<Win32Exception>(() => CaptureExclusion.Apply(0));
        Assert.Throws<Win32Exception>(() => CaptureExclusion.IsApplied(0));
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
