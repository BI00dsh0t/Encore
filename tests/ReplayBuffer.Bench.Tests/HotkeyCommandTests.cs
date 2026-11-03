using System.Runtime.InteropServices;
using System.Text.Json;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Bench.Tests;

/// <summary>
/// The matrix's Hotkey failure rows, against the built engine's <c>serve</c>. Each test has its own
/// Ctrl+Alt+Shift combination, which no other test in the solution uses (the engine's own Hotkey
/// tests take F18–F21, F23 and F24).
/// </summary>
[Collection(nameof(BenchProcessCollection))]
public sealed class HotkeyCommandTests : IDisposable
{
    private const int CtrlAltShift = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
    private const int VkF17 = 0x80;
    private const int VkF22 = 0x85;

    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void AKeyAnotherProcessHoldsIsHotkeyTakenAndExitsOne()
    {
        var engine = Engine();
        using var held = new HeldHotkey(VkF22, CtrlAltShift);
        Assert.True(held.Registered);

        var code = HotkeyBenchmark.Run(_run.Context("hotkey", "--key", "ctrl+alt+shift+f22", "--timeout", "5", "--engine", engine));

        Assert.Equal(1, code);
        var report = Read();
        Assert.StartsWith(ErrorCodes.HotkeyTaken + ":", report.Error, StringComparison.Ordinal);
        Assert.Equal(DriftMath.Inconclusive, report.Verdict);
        Assert.Equal(0, report.Presses);
    }

    [Fact]
    public void NoPressWithinTheTimeoutStopsWithWhatWasGathered()
    {
        var engine = Engine();

        var code = HotkeyBenchmark.Run(_run.Context("hotkey", "--key", "ctrl+alt+shift+f17", "--presses", "3", "--timeout", "5", "--engine", engine));

        Assert.Equal(0, code);
        var report = Read();
        Assert.Equal(3, report.PressesRequested);
        Assert.Equal(0, report.Presses);
        Assert.Empty(report.Timings);
        Assert.Equal(DriftMath.Inconclusive, report.Verdict);
        Assert.Null(report.Error);

        // The engine was shut down: the combination is free again.
        using var after = new HeldHotkey(VkF17, CtrlAltShift);
        Assert.True(after.Registered);
    }

    private static string Engine()
    {
        var engine = BenchRun.EngineExe;
        Assert.SkipUnless(engine is not null, "the engine is not built beside these tests");
        return engine!;
    }

    private HotkeyReport Read() =>
        JsonSerializer.Deserialize<HotkeyReport>(File.ReadAllText(_run.PathOf(HotkeyBenchmark.FileName)), BenchJson.Options)!;

    /// <summary>A Hotkey this test process holds, registered on a thread of its own until disposed.</summary>
    private sealed class HeldHotkey : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public HeldHotkey(int vk, int modifiers)
        {
            using var registered = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                Registered = RegisterHotKey(0, 1, (uint)(modifiers | HotkeyModifiers.NoRepeat), (uint)vk);
                registered.Set();
                _release.Wait();
                if (Registered)
                {
                    UnregisterHotKey(0, 1);
                }
            })
            { IsBackground = true };
            _thread.Start();
            registered.Wait();
        }

        public bool Registered { get; private set; }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(nint hwnd, int id);
    }
}
