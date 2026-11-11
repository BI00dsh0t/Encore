using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Engine.Audio;
using AudioDeviceHub = Encore.Platform.Audio.AudioDeviceHub;

namespace Encore.Engine.Tests;

/// <summary>The meter-cost measurement (Heavy): every listed device probed at once; it only reads the devices.</summary>
public sealed class MeterProbeCostTests
{
    private static readonly TimeSpan Span = TimeSpan.FromSeconds(10);

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task EveryListedDeviceProbedCostsLittleCpuAndKeepsTheMeterRate()
    {
        var output = TestContext.Current.TestOutputHelper;
        using var hub = new AudioDeviceHub(NullLoggerFactory.Instance);
        var devices = hub.ListPresent();
        Assert.NotEmpty(devices);

        var publishes = 0;
        using var meters = new TrackMeters(hub, levels => Interlocked.Increment(ref publishes), NullLogger.Instance);

        var idle = await CpuOverAsync(Span);
        meters.Watch(MeterProbes.WithProbes(Keys.TrackLayout.Default, devices));
        Volatile.Write(ref publishes, 0);
        var watched = await CpuOverAsync(Span);
        var rate = Volatile.Read(ref publishes) / Span.TotalSeconds;
        meters.Unwatch();

        var cores = Environment.ProcessorCount;
        output?.WriteLine($"{devices.Count} devices probed ({devices.Count(d => d.Flow == AudioFlow.Output)} playback, {devices.Count(d => d.Flow == AudioFlow.Input)} recording), {cores} logical cores");
        output?.WriteLine($"idle: {idle.TotalMilliseconds / Span.TotalSeconds:F1} ms CPU/s; probed: {watched.TotalMilliseconds / Span.TotalSeconds:F1} ms CPU/s ({100 * watched / (Span * cores):F2}% of the PC)");
        output?.WriteLine($"meters.levels: {rate:F1}/s");

        // The meter rate holds at 20 a second, and the whole watch stays under one core's tenth.
        Assert.InRange(rate, 15, 21);
        Assert.True(watched - idle < Span * 0.1, $"The probed watch took {watched - idle} CPU over {Span}.");
    }

    private static async Task<TimeSpan> CpuOverAsync(TimeSpan span)
    {
        using var process = Process.GetCurrentProcess();
        var start = process.TotalProcessorTime;
        await Task.Delay(span, TestContext.Current.CancellationToken);
        process.Refresh();
        return process.TotalProcessorTime - start;
    }
}
