using System.Windows.Media.Imaging;

namespace Encore.App.Library;

/// <summary>One frozen frame of the clip creator's filmstrip, at a source time in seconds.</summary>
internal sealed record ClipThumbnailImage(double Time, BitmapSource Image);
