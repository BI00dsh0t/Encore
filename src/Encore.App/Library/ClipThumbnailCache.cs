using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>Loads a bounded, source-bound filmstrip once. Frozen images need no live file handles or UI-thread decoding.</summary>
internal static class ClipThumbnailCache
{
    internal static Task<ClipThumbnailImage[]> LoadAsync(string folder, ClipThumbnailDescriptor descriptor, ClipSource source, double duration, CancellationToken token) => Task.Run(() =>
    {
        if (descriptor.Version != 1 || descriptor.Source != source || descriptor.Duration != duration || descriptor.Frames is null || descriptor.Frames.Length is < 1 or > 64)
        { throw new IOException("The thumbnail cache does not match this source."); }
        var images = new List<ClipThumbnailImage>();
        var previous = -1d;
        foreach (var frame in descriptor.Frames)
        {
            token.ThrowIfCancellationRequested();
            if (frame is null || !double.IsFinite(frame.Time) || frame.Time < 0 || frame.Time >= duration || frame.Time <= previous
                || string.IsNullOrWhiteSpace(frame.FileName) || frame.FileName != Path.GetFileName(frame.FileName))
            { throw new IOException("The thumbnail cache contains an invalid frame."); }
            var file = new FileInfo(Path.Combine(folder, frame.FileName));
            if (file.Length is < 1 or > 262144) { throw new IOException("The thumbnail cache image is too large or incomplete."); }
            using var stream = file.OpenRead();
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frameImage = decoder.Frames[0];
            if (frameImage.PixelWidth is < 1 or > 160 || frameImage.PixelHeight is < 1 or > 90)
            { throw new IOException("The thumbnail cache image dimensions are invalid."); }
            // Detach from the decoder thread, not just its frame wrapper: the UI thread may clone or freeze the image later.
            var converted = new FormatConvertedBitmap(frameImage, PixelFormats.Pbgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            image.Freeze();
            images.Add(new(frame.Time, image));
            previous = frame.Time;
        }
        return images.ToArray();
    }, token);
}
