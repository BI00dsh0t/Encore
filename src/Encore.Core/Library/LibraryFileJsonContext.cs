using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.Core.Library;

/// <summary>The source-generated JSON of <c>library.json</c>.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LibraryFile))]
internal sealed partial class LibraryFileJsonContext : JsonSerializerContext;
