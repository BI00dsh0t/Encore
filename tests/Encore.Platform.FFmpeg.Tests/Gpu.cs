using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Platform.Capture;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>Whether this PC has a GPU the capture device can be created on; a hosted CI runner has none.</summary>
public static class Gpu
{
    private static readonly Lazy<bool> Present = new(CanCreateDevice);

    /// <summary>Skips the test when no Direct3D 11 device with video support can be created here.</summary>
    public static void Require() => Assert.SkipUnless(Present.Value, "no GPU here can create the Direct3D 11 capture device");

    private static bool CanCreateDevice()
    {
        try
        {
            using var device = D3D11Device.Create(0, NullLogger.Instance);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
