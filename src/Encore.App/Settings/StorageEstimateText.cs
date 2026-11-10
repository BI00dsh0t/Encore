using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The wording of the Clips page's storage estimate: how much RAM or disk the last part of the game takes.</summary>
internal static class StorageEstimateText
{
    /// <summary>The Memory option's line, with the estimate.</summary>
    public static string MemoryLine(StorageEstimate? estimate) => estimate is { } known
        ? $"Fastest. Uses about {SettingsText.Bytes(known.Bytes)} of RAM."
        : "Fastest. Uses RAM.";

    /// <summary>The Disk option's line, with the estimate.</summary>
    public static string DiskLine(StorageEstimate? estimate) => estimate is { } known
        ? $"Uses about {SettingsText.Bytes(known.Bytes)} in the cache folder instead of RAM."
        : "Uses the cache folder instead of RAM.";

    /// <summary>The estimate of the Global settings, e.g. "Estimated RAM use: 1.6 GB".</summary>
    public static string Summary(StorageEstimate? estimate) => estimate switch
    {
        null => "Estimate not available yet",
        var known => (known.Storage == ReplayStorage.Memory ? "Estimated RAM use: " : "Estimated disk use: ")
            + SettingsText.Bytes(known.Bytes)
            + (known.WorstCase ? " (worst case for VBR)" : ""),
    };

    /// <summary>The warning when the estimate is over the free bytes; null otherwise. It blocks nothing.</summary>
    public static string? Warning(StorageEstimate? estimate) => estimate switch
    {
        { ExceedsFree: true, Storage: ReplayStorage.Memory } known =>
            $"More than the free RAM ({SettingsText.Bytes(known.FreeBytes ?? 0)}). The value is kept; Clips may be shorter.",
        { ExceedsFree: true } known =>
            $"More than the free space on the cache folder's drive ({SettingsText.Bytes(known.FreeBytes ?? 0)}). The value is kept.",
        _ => null,
    };
}
