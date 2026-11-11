using System.Text.Json.Serialization;

namespace Encore.App.Library;

/// <summary>Source-generated JSON for <see cref="LibraryViewState"/>.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(LibraryViewState))]
internal sealed partial class LibraryViewStateJson : JsonSerializerContext;
