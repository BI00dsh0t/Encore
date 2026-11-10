using System.Text.Json.Serialization;

namespace Encore.Engine;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(CaptureStatsReport))]
public sealed partial class CaptureStatsJson : JsonSerializerContext;
