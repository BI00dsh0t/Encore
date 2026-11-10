using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Hotkeys;
using Encore.Core.Sessions;
using Encore.Engine.Ipc;
using Encore.Engine.Settings;
using Encore.Platform.Windows;

namespace Encore.Engine.Hotkeys;

/// <summary>Glues the Hotkey thread to the engine: registers from settings, publishes status, posts presses on.</summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SettingsStore _store;
    private readonly EventHub _hub;
    private readonly bool _useCatalogDefault;
    private readonly ILogger _log;
    private readonly HotkeyThread _thread;

    // Under _lock: what is registered now, and the status last published.
    private IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> _applied = [];
    private Dictionary<HotkeyAction, HotkeyRegistrationResult> _results = [];
    private HotkeyStatus _status;
    private int _disposed;

    /// <summary>Registers the document's Hotkeys and publishes the first status; only the installed instance gets defaults.</summary>
    public HotkeyService(SettingsStore store, EventHub hub, SessionOrchestrator sessions, bool useCatalogDefault, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _store = store;
        _hub = hub;
        _useCatalogDefault = useCatalogDefault;
        _log = log;
        _status = hub.State.Hotkeys;

        // Runs on the Hotkey thread: posting is all it does, so the next press isn't held up.
        _thread = new HotkeyThread((action, qpc) => sessions.Post(new HotkeyPressed(action, qpc)));
        try
        {
            lock (_lock)
            {
                Refresh(store.Current);
            }
        }
        catch
        {
            _thread.Dispose();
            throw;
        }

        store.Changed += OnChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _store.Changed -= OnChanged;
        lock (_lock)
        {
            _thread.Dispose();
        }
    }

    /// <summary>Logs a press; the orchestrator thread calls it once it has published the press.</summary>
    public static void LogPress(ILogger log, HotkeyPress press) =>
        log.LogInformation("Hotkey {Action} pressed at qpc {Qpc}", ActionName(press.Action), press.Qpc);

    private static string ActionName(HotkeyAction action) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(action.ToString());

    private void OnChanged(SettingsDocument document)
    {
        // Raised under the store's lock, after the commit: a failure here must not look like a refused update.
        try
        {
            lock (_lock)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    Refresh(document);
                }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            _log.LogWarning(e, "Hotkeys were not re-registered: {Message}", e.Message);
        }
    }

    private void Refresh(SettingsDocument document)
    {
        var plan = HotkeyPlan.For(document, _useCatalogDefault);
        IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> wanted =
            [.. plan.Where(e => e.Registers).Select(e => (e.Action, e.Binding!))];

        if (!wanted.SequenceEqual(_applied))
        {
            _results = _thread.Apply(wanted).ToDictionary(r => r.Action);
            _applied = wanted;
            foreach (var result in _results.Values.Where(r => r.Outcome != HotkeyRegistrationOutcome.Registered))
            {
                _log.LogWarning("Hotkey {Action} not registered: {Outcome} (error {Error})", ActionName(result.Action), result.Outcome, result.Error);
            }

            _log.LogInformation("Hotkeys registered: {Count} of {Wanted}", _results.Values.Count(r => r.Outcome == HotkeyRegistrationOutcome.Registered), wanted.Count);
        }

        var status = new HotkeyStatus([.. plan.Select(Status)]);
        if (status != _status)
        {
            _status = status;
            _hub.Publish(HotkeyMessages.Status, status);
        }
    }

    private HotkeyBindingStatus Status(HotkeyPlanEntry entry)
    {
        if (!entry.Registers)
        {
            return new HotkeyBindingStatus(entry.Action, entry.Binding, false, entry.SkipCode);
        }

        var outcome = _results[entry.Action].Outcome;
        return new HotkeyBindingStatus(
            entry.Action,
            entry.Binding,
            outcome == HotkeyRegistrationOutcome.Registered,
            outcome switch
            {
                HotkeyRegistrationOutcome.Registered => null,
                HotkeyRegistrationOutcome.Taken => ErrorCodes.HotkeyTaken,
                _ => ErrorCodes.HotkeyFailed,
            });
    }
}
