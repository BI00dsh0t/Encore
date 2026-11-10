using Encore.Contracts.Messages;

namespace Encore.App.Library;

internal interface IClipMediaService
{
    Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken token);
}
