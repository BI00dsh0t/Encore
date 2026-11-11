namespace Encore.App.Status;

/// <summary>The status header's dot (EXPERIENCE.md): <c>record</c>, <c>success</c> or <c>text-disabled</c>.</summary>
internal enum StatusDot
{
    /// <summary>Not connected, or neither buffering nor recording.</summary>
    Idle,

    Buffering,

    /// <summary>A Recording or a Monitor Recording runs.</summary>
    Recording,
}
