using System.Text.Json;
using System.Text.Json.Serialization;

namespace BestStoriesApi.Stories;

/// <summary>
/// The JSON shape of a story, generated at compile time: camelCase names, ISO 8601 times with an offset.
/// Compile-time generation means no reflection when a snapshot is serialised.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Story))]
[JsonSerializable(typeof(IEnumerable<Story>))]
public partial class StoryJsonContext : JsonSerializerContext;
