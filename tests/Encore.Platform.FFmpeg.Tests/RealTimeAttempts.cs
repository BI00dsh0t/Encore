using System.Globalization;
using Xunit.Sdk;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>The retry rule for real-time timing tests: only a missed timing bound is retried; anything else fails at once.</summary>
public static class RealTimeAttempts
{
    public const int MaxAttempts = 3;

    /// <summary>Runs an attempt until it misses no timing bound, a few times at most; each attempt must write its own files.</summary>
    public static void Run(Action<int> attempt, ITestOutputHelper? output = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        output ??= TestContext.Current.TestOutputHelper;
        for (var number = 1; ; number++)
        {
            try
            {
                attempt(number);
                return;
            }
            catch (RealTimeBoundException e) when (number < MaxAttempts)
            {
                output?.WriteLine($"Attempt {number} of {MaxAttempts} missed a timing bound: {e.Message}");
            }
            catch (RealTimeBoundException e)
            {
                output?.WriteLine($"Attempt {number} of {MaxAttempts} missed a timing bound: {e.Message}");
                throw;
            }
        }
    }

    /// <summary>A timing bound that contention from a neighbouring test can miss: retried by <see cref="Run"/>.</summary>
    public static void Bound(bool condition, string message)
    {
        if (!condition)
        {
            throw new RealTimeBoundException(message);
        }
    }

    /// <summary>A timing range that contention can miss: retried by <see cref="Run"/>.</summary>
    public static void InRange<T>(T actual, T low, T high, string what)
        where T : IComparable<T> =>
        Bound(actual.CompareTo(low) >= 0 && actual.CompareTo(high) <= 0,
            string.Create(CultureInfo.InvariantCulture, $"{what} {actual} is outside [{low}, {high}]"));
}

/// <summary>A missed timing bound; the only failure <see cref="RealTimeAttempts.Run"/> retries.</summary>
public sealed class RealTimeBoundException(string message) : XunitException(message);
