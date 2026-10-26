using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayBuffer.Contracts.Ipc;

/// <summary>Protocol constants (AD-3, AD-22).</summary>
public static class Protocol
{
    /// <summary>The envelope's <c>v</c>; it stays 1 through v1 (AD-22).</summary>
    public const int Version = 1;

    /// <summary>The longest frame the engine reads from a client.</summary>
    public const int MaxEngineFrameBytes = 1024 * 1024;

    /// <summary>The longest frame a client reads from the engine (a snapshot carries the settings document).</summary>
    public const int MaxClientFrameBytes = 64 * 1024 * 1024;
}

/// <summary>
/// One frame, as read. A client sends commands <c>{ v, id, type, payload }</c>. The engine sends
/// replies <c>{ v, id, type, payload | error }</c> and events <c>{ v, type, seq, payload }</c>: a frame
/// from the engine with an <see cref="Id"/> is a reply, one without is an event. Unknown fields are
/// ignored, so newer peers can add them (AD-22).
/// </summary>
public sealed record Envelope
{
    public int V { get; init; }

    public string? Id { get; init; }

    public string? Type { get; init; }

    public long? Seq { get; init; }

    public JsonElement? Payload { get; init; }

    public IpcError? Error { get; init; }
}

/// <summary>A command's error reply (AD-3); <see cref="Code"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed record IpcError(string Code, string Message);

/// <summary>Source-generated serialization of the envelope.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(IpcError))]
public sealed partial class IpcJsonContext : JsonSerializerContext;
