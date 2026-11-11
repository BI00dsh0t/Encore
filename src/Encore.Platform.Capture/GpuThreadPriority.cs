using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;

namespace Encore.Platform.Capture;

/// <summary>Sets a device's GPU thread priority, so capture keeps its GPU time beside a game.</summary>
public static unsafe class GpuThreadPriority
{
    /// <summary>Sets the device's GPU thread priority (-7 to 7) and returns the HRESULT; readBack is what it then reports.</summary>
    public static int SetGpuThreadPriority(this D3D11Device device, int priority, out int? readBack)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(priority, -7);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 7);
        readBack = null;

        IDXGIDevice* dxgiDevice = null;
        try
        {
            var result = device.QueryDxgiDevice(&dxgiDevice);
            if (result.Failed)
            {
                return result.Value;
            }

            // CsWin32 turns a failed HRESULT into an exception carrying it.
            try
            {
                dxgiDevice->SetGPUThreadPriority(priority);
            }
            catch (Exception e)
            {
                result = new HRESULT(e.HResult);
            }

            try
            {
                int current;
                dxgiDevice->GetGPUThreadPriority(&current);
                readBack = current;
            }
            catch (Exception)
            {
                // The read-back is informational only.
            }

            return result.Value;
        }
        finally
        {
            if (dxgiDevice is not null) dxgiDevice->Release();
        }
    }
}
