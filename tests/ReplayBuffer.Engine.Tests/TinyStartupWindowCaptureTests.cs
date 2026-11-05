using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.Windows;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// Bugfix tiny-startup-window-capture, on a real window: a game proposed while its window is a
/// 156×27 startup placeholder (as Rocket League's is) isn't captured until the window grows, and
/// then is, so a save works.
/// </summary>
public sealed class TinyStartupWindowCaptureTests : IDisposable
{
    private const string WindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
        $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
        $form.Location = New-Object System.Drawing.Point(100, 100)
        $form.ClientSize = New-Object System.Drawing.Size(156, 27)
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
        SessionTestKit.RequireFFmpeg();
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

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
        var options = new CaptureSessionOptions { OutputFolder = _directory, Replay = true, Anchored = qpc => anchored.TrySetResult(qpc) };
        var wait = new CaptureWait(
            target => WindowFinder.NotCapturable(target.Window, CaptureWaitWarnings.MinWidth, CaptureWaitWarnings.MinHeight),
            (_, why) => waiting.TrySetResult(why),
            (_, captured) => ended.TrySetResult(captured),
            e => CaptureSession.CodeOf(e) == ReplayBuffer.Contracts.ErrorCodes.CaptureFailed)
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

        // The placeholder is waited for, not captured.
        Assert.Contains("156x27", await waiting.Task.WaitAsync(Timeout, Token), StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        Assert.False(anchored.Task.IsCompleted);

        // The game grows its window: capture starts, and a save some seconds later writes a Clip.
        Assert.True(Native.SetWindowPos(window.Handle, 0, 0, 0, 640, 480, Native.NoMoveNoZOrderNoActivate), $"SetWindowPos failed: {Marshal.GetLastPInvokeError()}");
        await anchored.Task.WaitAsync(Timeout, Token);
        Assert.True(await ended.Task.WaitAsync(Timeout, Token));
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var clip = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

        Assert.True(new FileInfo(clip.Path).Length > 0, clip.Path);
    }

    private static class Native
    {
        // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE.
        public const uint NoMoveNoZOrderNoActivate = 0x0002 | 0x0004 | 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    }
}
