using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>Runs clip media requests in the engine's worker process, one at a time; thumbnails have a queue of their own.</summary>
internal sealed class ClipMediaService : IClipMediaService
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _thumbnails = new(1, 1);

    public async Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token)
    {
        var slot = request.Operation == "prepareThumbnails" ? _thumbnails : _operation;
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A worker of its own for each request, independent of the capture service and its supervision.
            using var worker = ClipMediaWorkerProcess.Start();
            return await worker.RunAsync(request, progress, token).ConfigureAwait(false);
        }
        finally
        {
            slot.Release();
        }
    }
}
