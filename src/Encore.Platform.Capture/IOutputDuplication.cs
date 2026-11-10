using Windows.Win32.Foundation;

namespace Encore.Platform.Capture;

/// <summary>One monitor's output duplication, faked in tests; TryAcquire never waits, as DXGI would hold the device lock.</summary>
internal interface IOutputDuplication
{
    VideoSize ModeSize { get; }

    /// <summary>Creates the duplication; <c>DXGI_ERROR_NOT_FOUND</c> when the monitor isn't an output of the adapter.</summary>
    HRESULT Duplicate();

    /// <summary>Releases the duplication, if any.</summary>
    void Release();

    /// <summary>Acquires the next frame without waiting; <c>DXGI_ERROR_WAIT_TIMEOUT</c> means the desktop hasn't changed.</summary>
    HRESULT TryAcquire(out DuplicatedFrame frame);

    /// <summary>Releases the frame <see cref="TryAcquire"/> acquired.</summary>
    HRESULT ReleaseFrame();
}
