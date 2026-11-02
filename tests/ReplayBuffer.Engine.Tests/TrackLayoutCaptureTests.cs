using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Settings;
using ReplayBuffer.Platform.Audio;
using PlatformFlow = ReplayBuffer.Platform.Audio.AudioFlow;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// <c>debug-capture --track-layout</c> end to end: the published engine reads a throwaway test
/// instance's Global <c>trackLayout</c> and writes one titled AAC stream per Track. Uses the default
/// (eConsole) output and input endpoints; the instance's folders are deleted afterwards.
/// </summary>
public sealed class TrackLayoutCaptureTests : IDisposable
{
    private const string WindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.Width = 641
        $form.Height = 481
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
        })
        $timer.Start()
        $form.Show()
        $form.Hide()
        [void] $form.ShowDialog()
        """;

    private readonly Instance _instance = Instance.ForTest(Instance.TestPrefix + Guid.NewGuid().ToString("N"));
    private readonly string _directory = Directory.CreateTempSubdirectory("rb-tracks-").FullName;
    private readonly List<Process> _processes = [];

    private static string BinFolder => EngineBinaries.Folder;

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        foreach (var folder in new[] { _instance.RoamingDataFolder, _instance.LocalDataFolder, _directory })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void EachTrackIsOneTitledAacStreamInLayoutOrder()
    {
        RequireFFmpeg();
        var output = AudioEndpoints.Default(PlatformFlow.Output);
        var input = AudioEndpoints.Default(PlatformFlow.Input);
        var outDevice = Device(output.Id, output.FriendlyName, SettingsFlow.Output);
        var inDevice = Device(input.Id, input.FriendlyName, SettingsFlow.Input);
        StoreLayout(new TrackLayout(
            [outDevice, inDevice],
            [Track("Game", outDevice), Track("Both", outDevice, inDevice), Track("Mic", inDevice)]));
        var title = OpenWindow();
        var file = Path.Combine(_directory, "tracks.mp4");

        var (exitCode, _, stderr) = RunEngine(TimeSpan.FromSeconds(40), "--window", title, "--seconds", "3", "--output", file);

        Assert.True(exitCode == 0, stderr);
        var streams = Probe(file);
        Assert.Equal(["h264", "aac", "aac", "aac"], streams.Select(s => s.Codec));
        Assert.Equal(["Game", "Both", "Mic"], streams.Skip(1).Select(s => s.Title));
        Assert.Equal([true, false, false], streams.Skip(1).Select(s => s.Default));

        var video = streams[0].Duration;
        Assert.InRange(video, 2.5, 4);
        Assert.All(streams.Skip(1), s => Assert.InRange(s.Duration, video - 0.1, video + 0.1));
    }

    [Fact]
    public void TheDefaultLayoutHasATrackWithNoDevicesAndIsRefused()
    {
        RequireFFmpeg();

        // No settings.json: the catalog default, one "Everything" Track with no devices.
        var (exitCode, _, stderr) = RunEngine(TimeSpan.FromSeconds(30), "--window", "anything", "--output", Path.Combine(_directory, "never.mp4"));

        Assert.Equal(1, exitCode);
        Assert.Contains("error invalid-arguments: Track 'Everything' has no devices", stderr);
        Assert.False(File.Exists(Path.Combine(_directory, "never.mp4")));
    }

    [Fact]
    public void AnAbsentDeviceIsRefusedWithThePresentEndpointsAsCandidates()
    {
        RequireFFmpeg();
        var missing = Device("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", "Gone Speakers", SettingsFlow.Output);
        StoreLayout(new TrackLayout([missing], [Track("Game", missing)]));

        var (exitCode, _, stderr) = RunEngine(TimeSpan.FromSeconds(30), "--window", "anything", "--output", Path.Combine(_directory, "never.mp4"));

        Assert.Equal(1, exitCode);
        Assert.Contains("error audio-device-not-found", stderr);
        Assert.Contains("Gone Speakers", stderr);
        Assert.Contains("Candidates:", stderr);
    }

    [Fact]
    public void AnInputEndpointNamedAsAnOutputIsAbsent()
    {
        RequireFFmpeg();
        var input = AudioEndpoints.Default(PlatformFlow.Input);
        var wrong = Device(input.Id, input.FriendlyName, SettingsFlow.Output);
        StoreLayout(new TrackLayout([wrong], [Track("Game", wrong)]));

        var (exitCode, _, stderr) = RunEngine(TimeSpan.FromSeconds(30), "--window", "anything", "--output", Path.Combine(_directory, "never.mp4"));

        Assert.Equal(1, exitCode);
        Assert.Contains("error audio-device-not-found", stderr);
    }

    [Fact]
    public void AnInvalidStoredLayoutIsSettingInvalidValue()
    {
        RequireFFmpeg();
        WriteSettings("""{"schemaVersion":1,"revision":1,"global":{"trackLayout":{"devices":[],"tracks":[{"id":"00000000-0000-0000-0000-000000000000","name":"","deviceKeys":[],"muteAffected":false}]}},"profiles":{}}""");

        var (exitCode, _, stderr) = RunEngine(TimeSpan.FromSeconds(30), "--window", "anything");

        Assert.Equal(1, exitCode);
        Assert.Contains("error setting-invalid-value", stderr);
        Assert.Contains("trackLayout", stderr);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsSettingsUnreadableAndLeftInPlace()
    {
        RequireFFmpeg();
        const string Contents = "{ not json";
        WriteSettings(Contents);

        var (exitCode, _, stderr) = RunEngine(TimeSpan.FromSeconds(30), "--window", "anything");

        Assert.Equal(1, exitCode);
        Assert.Contains("error settings-unreadable", stderr);
        Assert.Equal(Contents, File.ReadAllText(Path.Combine(_instance.RoamingDataFolder, SettingsStore.FileName)));
        Assert.Single(Directory.GetFiles(_instance.RoamingDataFolder));
    }

    private static LayoutDevice Device(string endpointId, string name, SettingsFlow flow) =>
        new(Guid.NewGuid(), endpointId, name, "", flow, IsMic: flow == SettingsFlow.Input);

    private static Track Track(string name, params LayoutDevice[] devices) =>
        new(Guid.NewGuid(), name, [.. devices.Select(d => d.Key)], MuteAffected: false);

    private static void RequireFFmpeg() =>
        Assert.SkipUnless(File.Exists(Path.Combine(BinFolder, "ffprobe.exe")), "native/ffmpeg is not fetched");

    /// <summary>Stores <paramref name="layout"/> as the instance's Global trackLayout, through the store as the engine would.</summary>
    private void StoreLayout(TrackLayout layout)
    {
        var (store, code) = SettingsStore.Open(_instance.RoamingDataFolder);
        Assert.Null(code);
        store.Update(new SettingsUpdate(store.Current.Revision, [SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout))]));
    }

    private void WriteSettings(string contents)
    {
        Directory.CreateDirectory(_instance.RoamingDataFolder);
        File.WriteAllText(Path.Combine(_instance.RoamingDataFolder, SettingsStore.FileName), contents);
    }

    private (int ExitCode, string Stdout, string Stderr) RunEngine(TimeSpan timeout, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(BinFolder, "ReplayBuffer.Engine.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "debug-capture", "--instance", _instance.Name, "--track-layout" }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start)!;
        _processes.Add(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(timeout), "the engine did not exit");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private string OpenWindow()
    {
        var title = "RB-Test-" + Guid.NewGuid().ToString("N");
        var script = Path.Combine(_directory, "window.ps1");
        File.WriteAllText(script, WindowScript);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-Title", title })
        {
            start.ArgumentList.Add(arg);
        }

        _processes.Add(Process.Start(start)!);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (Platform.Windows.WindowFinder.List().Any(w => w.Title == title))
            {
                return title;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The test window never appeared.");
    }

    /// <summary>
    /// Each stream's codec, title, default flag and duration. The mp4 muxer stores a stream's
    /// <c>title</c> in the track's udta <c>name</c> box, which FFmpeg reads back as the <c>name</c> tag.
    /// </summary>
    private static List<(string Codec, string? Title, bool Default, double Duration)> Probe(string file)
    {
        var start = new ProcessStartInfo(Path.Combine(BinFolder, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_streams", "-of", "json", file })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);

        using var json = JsonDocument.Parse(stdout.Result);
        return json.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => (
                s.GetProperty("codec_name").GetString()!,
                s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var t) ? t.GetString() : null,
                s.GetProperty("disposition").GetProperty("default").GetInt32() == 1,
                s.TryGetProperty("duration", out var d) ? double.Parse(d.GetString()!, CultureInfo.InvariantCulture) : -1))
            .ToList();
    }
}
