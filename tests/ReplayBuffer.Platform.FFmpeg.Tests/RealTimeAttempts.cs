using Xunit.Sdk;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>
/// The retry rule for the real-time timing tests: <c>dotnet test --solution</c> runs the GPU test
/// assemblies as parallel processes, so a timing bound can be missed because of a neighbour, not the
/// code. A test passes on the first of <see cref="MaxAttempts"/> attempts that meets every bound;
/// each failed attempt is logged, and when all fail the last attempt's failure is rethrown. Only
/// assertion failures are retried; a skip, or any other exception, propagates at once.
/// </summary>
public static class RealTimeAttempts
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs <paramref name="attempt"/> (given the attempt number, from 1) until it returns without an
    /// assertion failure, at most <see cref="MaxAttempts"/> times. Each attempt must write its own files.
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
            catch (Exception e) when (e is IAssertionException and not SkipException && number < MaxAttempts)
            {
                output?.WriteLine($"Attempt {number} of {MaxAttempts} failed: {e.Message}");
            }
            catch (Exception e) when (e is IAssertionException and not SkipException)
            {
                output?.WriteLine($"Attempt {number} of {MaxAttempts} failed: {e.Message}");
                throw;
            }
        }
    }
}
