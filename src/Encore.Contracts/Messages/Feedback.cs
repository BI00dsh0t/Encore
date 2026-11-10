using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

[JsonConverter(typeof(CamelCaseEnumConverter<WarningSeverity>))]
public enum WarningSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// <c>warning.raised</c>, one of the two shapes the Overlay and status line render (AD-3): a
/// user-facing problem, active until it is dismissed.
/// </summary>
/// <param name="Id">The engine's id for this occurrence.</param>
/// <param name="Code">One of <see cref="ErrorCodes"/>.</param>
/// <param name="Source">The engine area that raised it, such as <c>settings</c> or <c>audio</c>.</param>
/// <param name="At">When it was raised, in UTC.</param>
/// <param name="Overlay">Whether the Overlay shows it.</param>
/// <param name="Sound">The <c>feedbackSounds</c> name the engine played for it, or <c>null</c>.</param>
/// <param name="Dismissable">Whether <c>warning.dismiss</c> may remove it.</param>
public sealed record Warning(
    string Id,
    string Code,
    WarningSeverity Severity,
    string Source,
    string Message,
    DateTimeOffset At,
    bool Overlay,
    string? Sound,
    bool Dismissable);

/// <summary>A built-in Feedback Sound; each name is its <c>feedbackSounds</c> flag.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FeedbackSound>))]
public enum FeedbackSound
{
    ClipSaved,
    SaveFailed,
    RecordingStarted,
    RecordingStopped,
    DiskFull,
}

/// <summary><c>feedback.preview</c>'s arguments: the sound to play, even when it is switched off.</summary>
public sealed record FeedbackPreview(FeedbackSound Sound);

/// <summary><c>warning.dismiss</c>'s arguments.</summary>
public sealed record WarningDismiss(string Id);

/// <summary><c>warning.dismissed</c>: the warning is no longer active.</summary>
public sealed record WarningDismissed(string Id);

/// <summary>Feedback messages: warnings, the Overlay and Feedback Sounds.</summary>
public static class FeedbackMessages
{
    /// <summary>Dismissing is a command, never local App state (AD-3).</summary>
    public static CommandType<WarningDismiss, Empty> Dismiss { get; } =
        new("warning.dismiss", FeedbackJsonContext.Default.WarningDismiss, FeedbackJsonContext.Default.Empty);

    /// <summary>Plays a Feedback Sound on the Feedback Sound device, whether or not it is switched on.</summary>
    public static CommandType<FeedbackPreview, Empty> Preview { get; } =
        new("feedback.preview", FeedbackJsonContext.Default.FeedbackPreview, FeedbackJsonContext.Default.Empty);

    /// <summary>Adds the warning, replacing an active one with the same id.</summary>
    public static EventType<Warning> Raised { get; } = new(
        "warning.raised",
        FeedbackJsonContext.Default.Warning,
        "warnings",
        (snapshot, warning) => snapshot with { Warnings = [.. snapshot.Warnings.Where(w => w.Id != warning.Id), warning] });

    public static EventType<WarningDismissed> Dismissed { get; } = new(
        "warning.dismissed",
        FeedbackJsonContext.Default.WarningDismissed,
        "warnings",
        (snapshot, dismissed) => snapshot with { Warnings = [.. snapshot.Warnings.Where(w => w.Id != dismissed.Id)] });

    public static IReadOnlyList<CommandType> Commands { get; } = [Dismiss, Preview];

    public static IReadOnlyList<EventType> Events { get; } = [Raised, Dismissed];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Warning))]
[JsonSerializable(typeof(WarningDismiss))]
[JsonSerializable(typeof(WarningDismissed))]
[JsonSerializable(typeof(FeedbackPreview))]
[JsonSerializable(typeof(Empty))]
public sealed partial class FeedbackJsonContext : JsonSerializerContext;
