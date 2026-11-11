using Encore.Contracts.Messages;

namespace Encore.Core.Feedback;

/// <summary>A user-facing notice of an outcome, raised as a warning the Overlay shows and the user may dismiss.</summary>
public sealed record FeedbackNotice(string Code, WarningSeverity Severity, string Source, string Message, string? Sound);
