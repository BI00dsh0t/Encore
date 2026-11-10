using System.Text.Json.Serialization;

namespace Encore.App.Library;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(LibraryViewState))]
internal sealed partial class LibraryViewStateJson : JsonSerializerContext;
