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
        Assert.Equal(64, surface.Direct3DSurface.Description.Width);
        surface.Flush();

        surface.Dispose();
        surface.Dispose();
        Assert.Equal(IntPtr.Zero, surface.BackBuffer);
    });

    // QA 14.2 round 1: the WinRT wrapper kept references on the surface, and through it on the texture and
    // device, until a GC. After a resize and after Dispose, with no GC, every COM object it held is gone.
    [Fact]
    public void DisposeReleasesEveryReferenceAtOnceEvenAfterAResize() => OnSta(() =>
    {
        var window = new WindowInteropHelper(new Window()).EnsureHandle();
        var surface = new VideoSurface(64, 36, window);
        _ = surface.Direct3DSurface.Description;
        var first = surface.AddRefsForTest();

        surface.Resize(32, 18);
        _ = surface.Direct3DSurface.Description;
        Assert.Equal(32, surface.Direct3DSurface.Description.Width);

        // The old texture's views (the first three) are gone; the devices (the last four) are kept.
        Assert.All(first[..3], view => Assert.Equal(0u, VideoSurface.ReleaseForTest(view)));
        var second = surface.AddRefsForTest();
        foreach (var device in first[3..])
        {
            VideoSurface.ReleaseForTest(device);
        }

        surface.Dispose();

        Assert.All(second, pointer => Assert.Equal(0u, VideoSurface.ReleaseForTest(pointer)));
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
