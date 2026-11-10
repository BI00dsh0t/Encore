using Encore.Contracts;
using Encore.Core.Output;

namespace Encore.Core.Tests;

public sealed class WriteFailuresTests
{
    [Fact]
    public void EachFailureMapsToItsShortReason()
    {
        Assert.Equal(ErrorCodes.DiskFull, WriteFailures.ReasonOf(new IOException("full", unchecked((int)0x80070070))));
        Assert.Equal(ErrorCodes.DiskFull, WriteFailures.ReasonOf(new IOException("full", unchecked((int)0x80070027))));
        Assert.Equal(ErrorCodes.AccessDenied, WriteFailures.ReasonOf(new UnauthorizedAccessException("denied")));
        Assert.Equal(ErrorCodes.DiskFull, WriteFailures.ReasonOf(new ErrorCodeException(ErrorCodes.DiskFull, "full")));
        Assert.Equal(ErrorCodes.ClipTooShort, WriteFailures.ReasonOf(new ErrorCodeException(ErrorCodes.ClipTooShort, "short")));
        Assert.Equal(ErrorCodes.SaveFailed, WriteFailures.ReasonOf(new ErrorCodeException(ErrorCodes.EncodeFailed, "mux")));
        Assert.Equal(ErrorCodes.SaveFailed, WriteFailures.ReasonOf(new InvalidOperationException("other")));
    }
}
