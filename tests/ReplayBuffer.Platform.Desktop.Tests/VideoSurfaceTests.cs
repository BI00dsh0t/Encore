using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;

namespace ReplayBuffer.Platform.Desktop.Tests;

/// <summary>Story 14.2: the in-app player's shared surface. A GPU device, but no playback; light enough for the default run.</summary>
public sealed class VideoSurfaceTests
{
    [Fact]
    public void ASharedSurfaceHasBothViewsAndReleasesThem() => OnSta(() =>
    {
        var window = new WindowInteropHelper(new Window()).EnsureHandle();

        var surface = new VideoSurface(64, 36, window);

        Assert.Equal((64, 36), (surface.Width, surface.Height));
        Assert.NotEqual(IntPtr.Zero, surface.BackBuffer);
        Assert.NotNull(surface.Direct3DSurface);
        Assert.Equal(64, surface.Direct3DSurface.Description.Width);
        surface.Flush();

        surface.Dispose();
        surface.Dispose();
        Assert.Equal(IntPtr.Zero, surface.BackBuffer);
    });

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
