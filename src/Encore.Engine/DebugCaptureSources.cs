using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Core.Settings;
using Encore.Engine.Settings;
using Encore.Platform.Audio;
using Encore.Platform.Windows;
using AudioFlow = Encore.Platform.Audio.AudioFlow;
using SettingsAudioFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine;

/// <summary>Finds what a <c>debug-capture</c> records, its window and audio devices, and describes them for the user.</summary>
internal static class DebugCaptureSources
{
    /// <summary>The window named by <c>--hwnd</c> or <c>--window</c>; refused with the candidates when none or several match.</summary>
    public static WindowInfo ResolveWindow(DebugCaptureCommand command)
    {
        if (command.Hwnd is { } handle)
        {
            return WindowFinder.FromHandle(handle)
                ?? throw new EngineFailure(
                    ErrorCodes.WindowNotFound,
                    $"0x{handle:X} is not a visible, titled top-level window.",
                    WindowFinder.List().Select(Describe).ToList());
        }

        var match = WindowFinder.Match(WindowFinder.List(), command.Window!);
        return match.Outcome switch
        {
            WindowMatchOutcome.Found => match.Window!,
            WindowMatchOutcome.NotFound => throw new EngineFailure(
                ErrorCodes.WindowNotFound,
                $"No window title contains '{command.Window}'.",
                match.Candidates.Select(Describe).ToList()),
            _ => throw new EngineFailure(
                ErrorCodes.WindowAmbiguous,
                $"{match.Candidates.Count} window titles contain '{command.Window}'; use a longer title or --hwnd.",
                match.Candidates.Select(Describe).ToList()),
        };
    }

    /// <summary>One Track per device: the output device's loopback and the input device, each named or the default.</summary>
    public static SessionAudio DeviceAudio(DebugCaptureCommand command)
    {
        var output = ResolveEndpoint(AudioFlow.Output, command.OutputDevice);
        var input = ResolveEndpoint(AudioFlow.Input, command.InputDevice);
        return SessionAudio.OnePerDevice(
        [
            new SessionAudioDevice(output.Id, SettingsAudioFlow.Output, output.FriendlyName),
            new SessionAudioDevice(input.Id, SettingsAudioFlow.Input, input.FriendlyName),
        ]);
    }

    /// <summary>The session audio of the stored Global <c>trackLayout</c>; refused when it is invalid or a device is missing.</summary>
    public static SessionAudio LayoutAudio(SettingsDocument document)
    {
        if (document.Global.TryGetValue(Keys.TrackLayout.Name, out var raw) && !Keys.TrackLayout.IsValid(raw))
        {
            throw new EngineFailure(
                ErrorCodes.SettingInvalidValue,
                $"The stored '{Keys.TrackLayout.Name}' is not a valid Track Layout; fix or remove it in {SettingsStore.FileName}.");
        }

        var audio = SessionAudio.FromLayout(SettingsResolver.Resolve(document, null).TrackLayout);
        var present = new Dictionary<SettingsAudioFlow, IReadOnlyList<AudioEndpoint>>();
        foreach (var device in audio.Devices)
        {
            var flow = device.Flow == SettingsAudioFlow.Output ? AudioFlow.Output : AudioFlow.Input;
            if (!present.TryGetValue(device.Flow, out var endpoints))
            {
                endpoints = ListEndpoints(flow);
                present[device.Flow] = endpoints;
            }

            RequirePresent(device, flow, endpoints);
        }

        return audio;
    }

    public static string Describe(WindowInfo window) => $"0x{window.Handle:X8}  {window.ProcessName ?? "?",-24} {window.Title}";

    public static string Describe(AudioEndpoint endpoint) => $"{endpoint.FriendlyName}  [{endpoint.Id}]";

    /// <summary><c>debug-list</c>'s line: flow, friendly name, parent name and endpoint id.</summary>
    public static string DescribeFully(AudioEndpoint endpoint)
    {
        var flow = endpoint.Flow == AudioFlow.Output ? "output" : "input";
        var parent = endpoint.ParentName.Length > 0 ? endpoint.ParentName : "-";
        return $"{flow,-6}  {endpoint.FriendlyName}  parent: {parent}  [{endpoint.Id}]";
    }

    private static IReadOnlyList<AudioEndpoint> ListEndpoints(AudioFlow flow)
    {
        try
        {
            return AudioEndpoints.List(flow);
        }
        catch (Exception e)
        {
            throw new EngineFailure(ErrorCodes.AudioDeviceFailed, $"Could not list {flow} devices: {e.Message}");
        }
    }

    private static void RequirePresent(SessionAudioDevice device, AudioFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (endpoints.Any(endpoint => string.Equals(endpoint.Id, device.EndpointId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var kind = flow == AudioFlow.Output ? "output" : "input";
        throw new EngineFailure(
            ErrorCodes.AudioDeviceNotFound,
            $"The Track Layout's {kind} device '{device.FriendlyName}' [{device.EndpointId}] is not present.",
            endpoints.Select(Describe).ToList());
    }

    /// <summary>The default endpoint of a flow, or the one whose name contains query.</summary>
    private static AudioEndpoint ResolveEndpoint(AudioFlow flow, string? query)
    {
        try
        {
            if (query is null)
            {
                return AudioEndpoints.Default(flow);
            }

            var match = AudioEndpoints.Match(AudioEndpoints.List(flow), query);
            var kind = flow == AudioFlow.Output ? "output" : "input";
            return match.Outcome switch
            {
                EndpointMatchOutcome.Found => match.Endpoint!,
                EndpointMatchOutcome.NotFound => throw new EngineFailure(
                    ErrorCodes.AudioDeviceNotFound,
                    $"No {kind} device name contains '{query}'.",
                    match.Candidates.Select(Describe).ToList()),
                _ => throw new EngineFailure(
                    ErrorCodes.AudioDeviceAmbiguous,
                    $"{match.Candidates.Count} {kind} device names contain '{query}'; use the full name.",
                    match.Candidates.Select(Describe).ToList()),
            };
        }
        catch (Exception e) when (e is not EngineFailure)
        {
            throw new EngineFailure(ErrorCodes.AudioDeviceFailed, $"Could not list {flow} devices: {e.Message}");
        }
    }
}
