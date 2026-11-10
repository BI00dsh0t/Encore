using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLinked;
using Microsoft.Extensions.Logging;
using Encore.Contracts;

namespace Encore.Platform.FFmpeg;

/// <summary>Binds FFmpeg.AutoGen to the DLLs beside the executable.</summary>
public static unsafe class FFmpegLibrary
{
    /// <summary>The DLLs FFmpeg.AutoGen.Bindings.DynamicallyLinked 9.0.1.1 imports.</summary>
    public static IReadOnlyList<string> RequiredLibraries { get; } =
    [
        "avcodec-63.dll",
        "avdevice-63.dll",
        "avfilter-12.dll",
        "avformat-63.dll",
        "avutil-61.dll",
        "swresample-7.dll",
        "swscale-10.dll",
    ];

    /// <summary>Read once: FFmpeg.AutoGen computes these on every read, which allocates, and encoders test them per packet.</summary>
    internal static readonly int ErrorAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    internal static readonly int ErrorEof = ffmpeg.AVERROR_EOF;

    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>The libraries missing from <paramref name="directory"/>.</summary>
    public static IReadOnlyList<string> MissingLibraries(string directory) =>
        RequiredLibraries.Where(name => !File.Exists(Path.Combine(directory, name))).ToList();

    /// <summary>Throws <see cref="ErrorCodes.FFmpegMissing"/>, naming the fetch script, when a library is missing.</summary>
    public static void EnsurePresent(string directory)
    {
        var missing = MissingLibraries(directory);
        if (missing.Count > 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.FFmpegMissing,
                $"FFmpeg is missing from {directory} ({string.Join(", ", missing)}). "
                + "Run 'pwsh tools/fetch-ffmpeg.ps1' from the repository root, then rebuild.");
        }
    }

    /// <summary>Binds the libraries once; throws <see cref="ErrorCodes.FFmpegMissing"/> when they are not beside the executable.</summary>
    public static void Initialize(ILogger logger)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            EnsurePresent(AppContext.BaseDirectory);
            DynamicallyLinkedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            logger.LogInformation("FFmpeg {Version}", ffmpeg.av_version_info());
            _initialized = true;
        }
    }

    /// <summary>Throws when an FFmpeg call failed: DiskFull for ENOSPC, AccessDenied for EACCES, EncodeFailed for any other.</summary>
    internal static int Check(int result, string operation)
    {
        if (result < 0)
        {
            var code = result switch
            {
                -28 => ErrorCodes.DiskFull,
                -13 => ErrorCodes.AccessDenied,
                _ => ErrorCodes.EncodeFailed,
            };
            throw new ErrorCodeException(code, $"{operation} failed: {ErrorText(result)}");
        }

        return result;
    }

    internal static string ErrorText(int error)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return new string((sbyte*)buffer);
    }
}
