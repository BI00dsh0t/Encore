using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;

namespace Encore.Platform.FFmpeg.Tests;

public sealed class FFmpegLibraryTests
{
    [Fact]
    public void AFolderWithoutTheDllsFailsWithFFmpegMissingAndNamesTheFetchScript()
    {
        var empty = Directory.CreateTempSubdirectory("rb-noffmpeg-");
        try
        {
            var e = Assert.Throws<ErrorCodeException>(() => FFmpegLibrary.EnsurePresent(empty.FullName));

            Assert.Equal(ErrorCodes.FFmpegMissing, e.Code);
            Assert.Contains("tools/fetch-ffmpeg.ps1", e.Message);
            Assert.Equal(FFmpegLibrary.RequiredLibraries.Count, FFmpegLibrary.MissingLibraries(empty.FullName).Count);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheTestOutputHasEveryDllTheBindingsImport()
    {
        Assert.SkipUnless(Directory.Exists(Path.Combine(AppContext.BaseDirectory)) && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")),
            "native/ffmpeg is not fetched");

        Assert.Empty(FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory));
    }

    [Fact]
    public void CheckMapsAFullDiskToDiskFullAndAnyOtherErrorToEncodeFailed()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")), "native/ffmpeg is not fetched");
        FFmpegLibrary.Initialize(NullLogger.Instance);

        Assert.Equal(ErrorCodes.DiskFull, Assert.Throws<ErrorCodeException>(() => FFmpegLibrary.Check(-28, "x")).Code);
        Assert.Equal(ErrorCodes.EncodeFailed, Assert.Throws<ErrorCodeException>(() => FFmpegLibrary.Check(-1, "x")).Code);
    }
}
