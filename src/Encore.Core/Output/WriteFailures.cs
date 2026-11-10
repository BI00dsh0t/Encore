using Encore.Contracts;

namespace Encore.Core.Output;

/// <summary>
/// The short reason a failed save reports in <c>clip.failed</c> (R6), from what its writer threw.
/// Clips use it now; Recordings reuse it later.
/// </summary>
public static class WriteFailures
{
    // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as HRESULTs.
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);

    /// <summary>
    /// <see cref="ErrorCodes.ClipTooShort"/>, <see cref="ErrorCodes.SaveBusy"/>,
    /// <see cref="ErrorCodes.DiskFull"/> or <see cref="ErrorCodes.AccessDenied"/> when
    /// <paramref name="e"/> carries or means one, else <see cref="ErrorCodes.SaveFailed"/>.
    /// </summary>
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
