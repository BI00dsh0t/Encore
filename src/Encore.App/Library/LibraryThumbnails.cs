using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.Platform.Desktop;

namespace Encore.App.Library;

/// <summary>The Library rows' thumbnails: fetched off the UI thread, set on it, and cached in memory by path and size.</summary>
internal sealed class LibraryThumbnails
{
    /// <summary>The thumbnails' side in pixels: 96×54 rows at up to 200 % scale.</summary>
    private const int Size = 192;

    private readonly Func<string, ImageSource?> _source;
    private readonly Action<Action> _background;
    private readonly Action<Action> _post;

    // UI thread only.
    private readonly Dictionary<(string Path, long Size), ImageSource> _cache = [];

    /// <summary>The source runs on the background runner; post runs an action on the UI thread.</summary>
    public LibraryThumbnails(Func<string, ImageSource?> source, Action<Action> background, Action<Action> post)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _background = background ?? throw new ArgumentNullException(nameof(background));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>The shell's thumbnails, asked on one dedicated STA thread in order, as frozen bitmaps.</summary>
    public static LibraryThumbnails Shell(Action<Action> post)
    {
        var queue = new BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
#pragma warning disable CA1031 // A thumbnail that fails leaves its row with the generic glyph.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        })
        {
            Name = "LibraryThumbnails",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new LibraryThumbnails(FromShell, queue.Add, post);
    }

    /// <summary>Sets a row's thumbnail: at once when cached, else when the source has it; a row that moved on keeps its own.</summary>
    public void Request(LibraryRow row, string absolutePath, long size)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);

        var key = (absolutePath, size);
        row.ThumbnailRequest = key;
        if (_cache.TryGetValue(key, out var cached))
        {
            row.Thumbnail = cached;
            return;
        }

        _background(() =>
        {
            var image = _source(absolutePath);
            _post(() =>
            {
                if (image is not null)
                {
                    _cache[key] = image;
                }

                if (row.ThumbnailRequest == key)
                {
                    row.Thumbnail = image;
                }
            });
        });
    }

    private static ImageSource? FromShell(string path)
    {
        if (ShellThumbnail.TryGet(path, Size) is not { } image)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
