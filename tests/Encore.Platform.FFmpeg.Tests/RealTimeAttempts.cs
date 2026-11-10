using System.Globalization;
using Xunit.Sdk;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>
/// The retry rule for the real-time timing tests: <c>dotnet test --solution</c> runs the GPU test
/// assemblies as parallel processes, so a timing bound can be missed because of a neighbour, not the
/// code. A test passes on the first of <see cref="MaxAttempts"/> attempts that meets every bound;
/// each missed attempt is logged, and when all miss the last attempt's failure is rethrown. Only a
/// timing bound checked with <see cref="Bound"/> or <see cref="InRange{T}"/> is retried; any other
/// assertion (a crash, a fault, a wrong count or PTS), a skip, or any other exception fails at once.
/// </summary>
public static class RealTimeAttempts
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs <paramref name="attempt"/> (given the attempt number, from 1) until it returns without
    /// missing a timing bound, at most <see cref="MaxAttempts"/> times. Each attempt must write its own files.
    /// </summary>
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
