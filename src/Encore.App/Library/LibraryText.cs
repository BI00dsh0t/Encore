using System.Globalization;

namespace Encore.App.Library;

/// <summary>How the Library words a file's size and length.</summary>
internal static class LibraryText
{
    private const double BytesPerMegabyte = 1024 * 1024;
    private const double BytesPerGigabyte = BytesPerMegabyte * 1024;

    /// <summary>A size in MB below 1 GB, else in GB (1024-based), with one decimal.</summary>
    public static string SizeText(long bytes)
    {
        // Compared after rounding, so a size just under 1 GB doesn't read "1024.0 MB".
        return Math.Round(bytes / BytesPerMegabyte, 1) < 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerMegabyte:0.0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerGigabyte:0.0} GB");
    }

    /// <summary>A length as <c>m:ss</c>, or <c>h:mm:ss</c> from an hour; empty when unknown.</summary>
    public static string LengthText(double? seconds)
    {
        if (seconds is not { } value || double.IsNaN(value) || value < 0)
        {
            return "";
        }

        var whole = (long)Math.Round(value);
        var (hours, minutes, rest) = (whole / 3600, whole / 60 % 60, whole % 60);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{rest:00}");
    }
}
