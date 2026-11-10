using Encore.Contracts;

namespace Encore.Core.Output;

/// <summary>The short reason a failed save reports in <c>clip.failed</c>, from what its writer threw.</summary>
public static class WriteFailures
{
    // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as HRESULTs.
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);

    /// <summary>The error code an exception carries or means: too short, busy, disk full or access denied, else save failed.</summary>
    public static string ReasonOf(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            ErrorCodeException { Code: ErrorCodes.ClipTooShort or ErrorCodes.SaveBusy or ErrorCodes.DiskFull or ErrorCodes.AccessDenied } coded => coded.Code,
            ErrorCodeException => ErrorCodes.SaveFailed,
            IOException { HResult: DiskFullHResult or HandleDiskFullHResult } => ErrorCodes.DiskFull,
            UnauthorizedAccessException => ErrorCodes.AccessDenied,
            _ => ErrorCodes.SaveFailed,
        };
    }
}
