using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;

namespace Encore.Engine.Hosting;

/// <summary>Commands only test instances answer, so a test can make the engine look stuck.</summary>
internal static class DebugCommands
{
    /// <summary>Never replies, so a test can hold one command pending.</summary>
    public static readonly CommandType<Empty, Empty> Hold =
        new("debug.hold", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    /// <summary>Stops the heartbeat and never replies, so the engine looks hung while its process lives.</summary>
    public static readonly CommandType<Empty, Empty> Hang =
        new("debug.hang", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    /// <summary>Ends every connection after its reply; with RefuseNew it also stops accepting new ones.</summary>
    public static readonly CommandType<DebugDrop, Empty> Drop =
        new("debug.drop", DebugJsonContext.Default.DebugDrop, DebugJsonContext.Default.Empty);
}

/// <summary><c>debug.drop</c>'s arguments.</summary>
internal sealed record DebugDrop(bool RefuseNew);

[JsonSourceGenerationOptions(JsonSerializerDefaults.General, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DebugDrop))]
[JsonSerializable(typeof(Empty))]
internal sealed partial class DebugJsonContext : JsonSerializerContext;
