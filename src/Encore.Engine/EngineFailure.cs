using Encore.Contracts;

namespace Encore.Engine;

/// <summary>A failure to report with its code, and the candidates the user can pick from.</summary>
public sealed class EngineFailure(string code, string message, IReadOnlyList<string>? candidates = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];

    /// <summary>The error code a failure is reported with: its own, else <c>encode-failed</c>.</summary>
    public static string CodeOf(Exception e) => e switch
    {
        EngineFailure f => f.Code,
        ErrorCodeException c => c.Code,
        _ => ErrorCodes.EncodeFailed,
    };
}
