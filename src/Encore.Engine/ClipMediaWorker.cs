using System.Text.Json;
using Encore.Contracts.Messages;
using Encore.Engine.ClipMedia;

namespace Encore.Engine;

/// <summary>The isolated media worker: no settings, capture host, instance mutex or live IPC.</summary>
public static class ClipMediaWorker
{
    private const int MaxRequestLength = 65536;

    /// <summary>Reads one request from stdin, writes progress and the result to stdout, and returns the exit code.</summary>
    public static async Task<int> RunAsync()
    {
        using var cancel = new CancellationTokenSource();
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null || line.Length > MaxRequestLength)
            {
                throw new IOException("Missing or oversized clip request.");
            }

            var request = JsonSerializer.Deserialize(line, ClipMediaJsonContext.Default.ClipMediaRequest)
                ?? throw new IOException("Invalid clip request.");

            // Closing the private request pipe cancels this worker and its owned tool process.
            _ = Task.Run(() => CancelOnCloseAsync(cancel));
            var result = await ExecuteAsync(request, new ProgressSink(), cancel.Token).ConfigureAwait(false);
            Write(result);
            return 0;
        }
        catch (Exception error)
        {
            var message = error is OperationCanceledException ? "Clip operation cancelled." : error.Message;
            Write(new ClipMediaResponse("error", Error: message));
            return 1;
        }
    }

    /// <summary>Refuses a source that is gone or changed since the App chose it.</summary>
    public static void ValidateSource(ClipSource source)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists || file.Length != source.Length || file.LastWriteTimeUtc.Ticks != source.ModifiedUtcTicks)
        {
            throw new IOException("The source disappeared or changed. Reopen it from the Library.");
        }
    }

    /// <summary>Runs one operation on a source, checking before and after that the source is unchanged.</summary>
    public static async Task<ClipMediaResponse> ExecuteAsync(
        ClipMediaRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ValidateSource(request.Source);
        var metadata = await ClipProbe.ProbeAsync(request.Source, cancellationToken).ConfigureAwait(false);
        if (request.Operation is "export" or "preview")
        {
            // Rename is the commit point. Do not turn an already-published output into failure.
            return await RenderAsync(request, metadata, progress, cancellationToken).ConfigureAwait(false);
        }

        var response = request.Operation switch
        {
            "probe" => new ClipMediaResponse("complete", Metadata: metadata),
            "prepareWaveforms" => new ClipMediaResponse(
                "complete",
                Cache: await ClipWaveforms.PrepareAsync(request, metadata, cancellationToken).ConfigureAwait(false)),
            "prepareThumbnails" => new ClipMediaResponse(
                "complete",
                Thumbnails: await ClipThumbnails.PrepareAsync(request, metadata, cancellationToken).ConfigureAwait(false)),
            "waveform" => new ClipMediaResponse(
                "complete",
                Waveforms: await WaveformsAsync(request, metadata, cancellationToken).ConfigureAwait(false)),
            "range" => new ClipMediaResponse(
                "complete",
                Range: await ClipSelection.RangeAsync(request, metadata, cancellationToken).ConfigureAwait(false)),
            _ => throw new IOException("Unknown clip operation."),
        };
        ValidateSource(request.Source);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    private static async Task<ClipWaveform[]> WaveformsAsync(ClipMediaRequest request, ClipMetadata metadata, CancellationToken token)
    {
        ClipSelection.CheckRange(request.Start, request.End, metadata.Duration);
        var lanes = new List<ClipWaveform>();
        foreach (var track in ClipSelection.Tracks(request, metadata))
        {
            var bins = await ClipWaveforms.BinsAsync(request, metadata, track, token).ConfigureAwait(false);
            lanes.Add(new(request.Start, request.End, track.StreamIndex, bins));
        }

        return [.. lanes];
    }

    private static async Task<ClipMediaResponse> RenderAsync(
        ClipMediaRequest request,
        ClipMetadata metadata,
        IProgress<double>? progress,
        CancellationToken token)
    {
        if (request.Format == "mp3" && ClipSelection.Tracks(request, metadata).Length == 0)
        {
            throw new IOException("MP3 export needs at least one audio track.");
        }

        var range = await ClipSelection.RangeAsync(request, metadata, token).ConfigureAwait(false);

        // Preserve the chosen export's range semantics even though preview needs an MP4 picture.
        var renderRequest = request.Operation == "preview" ? request with { Format = "mp4" } : request;
        var output = await ClipExport.ExportAsync(renderRequest, metadata, range, progress, token).ConfigureAwait(false);
        return new("complete", Range: range, Output: output);
    }

    private static async Task CancelOnCloseAsync(CancellationTokenSource cancel)
    {
        try
        {
            await Console.In.ReadLineAsync().ConfigureAwait(false);
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Write(ClipMediaResponse response) =>
        Console.WriteLine(JsonSerializer.Serialize(response, ClipMediaJsonContext.Default.ClipMediaResponse));

    private sealed class ProgressSink : IProgress<double>
    {
        public void Report(double value) => Write(new ClipMediaResponse("progress", Progress: value));
    }
}
