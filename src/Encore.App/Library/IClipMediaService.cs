using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>Runs one clip media request (probe, waveform, thumbnails or export); tests fake it.</summary>
internal interface IClipMediaService
{
    Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token);
}
