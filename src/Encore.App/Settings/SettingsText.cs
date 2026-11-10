using System.Globalization;

namespace Encore.App.Settings;

/// <summary>How the settings pages read and write whole numbers: digits only, the same in every language.</summary>
internal static class SettingsText
{
    public static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseWhole(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Bytes with one decimal, in GB (10^9) from 1 GB, else in MB (10^6).</summary>
    public static string Bytes(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB");
}
