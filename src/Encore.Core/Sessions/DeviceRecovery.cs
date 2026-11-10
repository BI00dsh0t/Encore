namespace Encore.Core.Sessions;

/// <summary>How a lost GPU device is recovered: rebuild at once, retry after each failure, and give up after a limit.</summary>
public sealed record DeviceRecovery(
    Action<string>? Reset = null,
    Action<string>? Failed = null,
    Action<TimeSpan>? Recovered = null,
    TimeSpan? RetryEvery = null,
    TimeSpan? GiveUpAfter = null)
{
    /// <summary>The wait between recovery attempts by default: 2 s.</summary>
    public static readonly TimeSpan DefaultRetryEvery = TimeSpan.FromSeconds(2);

    /// <summary>How long after a loss recovery is given up by default: 60 s.</summary>
    public static readonly TimeSpan DefaultGiveUpAfter = TimeSpan.FromSeconds(60);
}
