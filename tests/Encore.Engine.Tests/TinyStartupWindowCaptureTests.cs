using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Output;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Platform.Windows;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>A bug that bit: a game proposed while its window is a tiny startup placeholder isn't captured until it grows.</summary>
public sealed class TinyStartupWindowCaptureTests : IDisposable
{
    // A placeholder window of 156x27; on a signal it closes, and 1.5 s later the process shows its real window.
    private const string WindowScript =
        """
        param([string] $Title, [string] $Signal)
        Add-Type -AssemblyName System.Windows.Forms
        function New-GameForm([string] $text, [int] $width, [int] $height) {
            $form = New-Object System.Windows.Forms.Form
            $form.Text = $text
            $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
            $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
            $form.Location = New-Object System.Drawing.Point(100, 100)
            $form.ClientSize = New-Object System.Drawing.Size($width, $height)
            $form
        }
        $script:first = New-GameForm $Title 156 27
        $script:second = $null
        $script:closedAt = 0
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $color = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
            if ($null -ne $script:first) {
                $script:first.BackColor = $color
                if (Test-Path $Signal) {
                    $script:first.Close()
                    $script:first = $null
                    $script:closedAt = $start.ElapsedMilliseconds
                }
            } elseif ($null -eq $script:second -and $start.ElapsedMilliseconds - $script:closedAt -gt 1500) {
                $script:second = New-GameForm ($Title + ' main') 640 480
                $script:second.Show()
            }
            if ($null -ne $script:second) { $script:second.BackColor = $color }
        })
        $timer.Start()
        $script:first.Show()
        [System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))
        """;

    private const string ProfileId = @"c:\games\tiny\tinygame.exe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-tiny-").FullName;
    private readonly List<Process> _processes = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AWindowThatStartsTinyIsCapturedOnceItGrowsAndASaveWorks()
    {
        SessionTestKit.RequireCapture();
        using var hub = new ToneHub(new Dictionary<string, (double, double)> { ["game"] = (0.1, 440) });
        var game = new LayoutDevice(Guid.NewGuid(), "game", "Game device", "", SettingsFlow.Output, IsMic: false);
        var layout = new TrackLayout([game], [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false)]);
        var document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr)))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory))
                .SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Tiny Game",
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
            }),
        };

        var signal = Path.Combine(_directory, "replace-window.signal");
        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript, "-Signal", signal);
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var ended = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
        var options = new CaptureSessionOptions { OutputFolder = _directory, Replay = true, FollowWindow = true, Anchored = qpc => anchored.TrySetResult(qpc) };

        // The engine's window choice, its waits reported to the test instead of the status line.
        var wait = new CaptureWait(
            target => WindowFinder.CaptureWindowOf(target.ProcessId, target.Window, CaptureWaitWarnings.MinWidth, CaptureWaitWarnings.MinHeight) is var (handle, why) && handle != 0
                ? CaptureWindow.Of(handle)
                : CaptureWindow.None(why ?? "no window"),
            (_, why) => waiting.Writer.TryWrite(why),
            (_, captured) => ended.Writer.TryWrite(captured),
            e => EngineFailure.CodeOf(e) == ErrorCodes.CaptureFailed)
        {
            Poll = TimeSpan.FromMilliseconds(200),
        };

        using var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub),
            () => document,
            _ => { },
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => saved.Writer.TryComplete(e), wait),
            clips: new ReplayClips(OutputPathAllocator.System, () => _directory, c => saved.Writer.TryWrite(c), _ => { }));
        orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "tinygame.exe"), TargetChangeReason.Detected));

        // The placeholder (WinForms makes it at least 156x39) is waited for, not captured.
        Assert.Contains("under 320x180", await NextAsync(waiting), StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        Assert.False(anchored.Task.IsCompleted);

        // The game grows its window: capture starts, and a save some seconds later writes a Clip.
        Assert.True(Native.SetWindowPos(window.Handle, 0, 0, 0, 640, 480, Native.NoMoveNoZOrderNoActivate), $"SetWindowPos failed: {Marshal.GetLastPInvokeError()}");
        var anchor = await anchored.Task.WaitAsync(Timeout, Token);
        Assert.True(await NextAsync(ended));
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var before = await NextAsync(saved);
        Assert.True(new FileInfo(before.Path).Length > 0, before.Path);

        // The window is replaced by another of the same process: the session moves to it, keeping its ring.
        File.WriteAllText(signal, "");
        await NextAsync(waiting);
        Assert.True(await NextAsync(ended));
        var swapped = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        var press = Stopwatch.GetTimestamp();
        Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press)));
        var after = await NextAsync(saved);

        var duration = double.Parse(
            SessionTestKit.Probe(after.Path, "-show_streams").GetProperty("streams")[0].GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);
        var sinceSwap = (double)(press - swapped) / Stopwatch.Frequency;
        var sinceAnchor = (double)(press - anchor) / Stopwatch.Frequency;
        Assert.True(duration > sinceSwap + 2, $"the Clip is {duration:0.0} s; the swap was {sinceSwap:0.0} s and the anchor {sinceAnchor:0.0} s before the press");
    }

    private static async Task<T> NextAsync<T>(System.Threading.Channels.Channel<T> channel) =>
        await channel.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

    private static class Native
    {
        // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE.
        public const uint NoMoveNoZOrderNoActivate = 0x0002 | 0x0004 | 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    }
}
