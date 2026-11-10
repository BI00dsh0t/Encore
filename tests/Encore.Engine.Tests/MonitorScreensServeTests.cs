using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Engine.Tests;

/// <summary>
/// Story 11.2 over a real pipe of a throwaway test instance: the <c>monitorRecordingScreens</c> key and
/// <c>monitors.list</c>'s new fields on this PC's real displays, which it only reads.
/// </summary>
public sealed class MonitorScreensServeTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-screens-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task TheScreensKeyRoundTripsRefusesAnEmptySelectionAndSurvivesARestart()
    {
        var host = Start();
        var client = await ConnectAsync(host);
        var chosen = MonitorRecordingScreens.Selected(@"\\?\DISPLAY#SAM7177#5&1ae6848c&1&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"\\.\DISPLAY2");

        await client.SendAsync(SettingsMessages.Update, Set(0, chosen), Token);
        var refused = await Assert.ThrowsAsync<ErrorCodeException>(
            () => client.SendAsync(SettingsMessages.Update, Set(1, new MonitorRecordingScreens(MonitorScreensMode.Selected, [])), Token));
        Assert.Equal(ErrorCodes.SettingInvalidValue, refused.Code);

        await client.DisposeAsync();
        await host.DisposeAsync();
        _disposables.Clear();

        // A new engine on the same instance reads it back, and the old key still resolves as before.
        client = await ConnectAsync(Start());
        var effective = SettingsResolver.Resolve(client.State.Settings, null);
        Assert.Equal(chosen.Mode, effective.MonitorRecordingScreens!.Mode);
        Assert.Equal(chosen.Monitors, effective.MonitorRecordingScreens.Monitors);
        Assert.Null(effective.MonitorRecordingMonitor);
    }

    [Fact]
    public async Task MonitorsListGivesEachScreensIdPhysicalPlaceAndRefreshRate()
    {
        var client = await ConnectAsync(Start());

        var monitors = (await client.SendAsync(RecordingMessages.MonitorsList, Empty.Value, Token)).Monitors;

        Assert.NotEmpty(monitors);
        Assert.True(monitors[0].Primary);
        Assert.Equal((0, 0), (monitors[0].X, monitors[0].Y));
        Assert.All(monitors, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.MonitorId), m.DeviceName);
            Assert.NotNull(m.X);
            Assert.NotNull(m.Y);
            Assert.True(m.RefreshHz > 0, m.DeviceName);
            Assert.True(m.Width > 0 && m.Height > 0, m.DeviceName);
        });
        Assert.Equal(monitors.Count, monitors.Select(m => m.MonitorId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static SettingsUpdate Set(long expectedRevision, MonitorRecordingScreens screens) =>
        new(expectedRevision, [SettingsOp.SetGlobal(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(screens))]);

    private EngineHost Start()
    {
        // Fake audio endpoints and game windows, as the other serve tests use; only the displays are real.
        var host = EngineHost.Start(TestInstance, NullLoggerFactory.Instance, hostOptions: new EngineHostOptions
        {
            Heartbeat = null,
            ProbeEncoders = false,
            AudioEndpoints = new FakeEndpointSource(),
            GameWindows = new GameDetectionServeTests.FakeProbe(),
        });
        _disposables.Add(host);
        return host;
    }

    private async Task<EngineClient> ConnectAsync(EngineHost host)
    {
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
        _disposables.Add(client);
        return client;
    }
}
