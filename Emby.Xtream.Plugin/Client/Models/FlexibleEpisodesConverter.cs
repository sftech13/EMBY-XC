using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Handles providers that return "episodes": [] (array) instead of "episodes": {} (object).
    /// </summary>
    internal sealed class FlexibleEpisodesConverter : JsonConverter<Dictionary<string, List<EpisodeInfo>>>
    {
        public override Dictionary<string, List<EpisodeInfo>> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var empty = new Dictionary<string, List<EpisodeInfo>>();

            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return empty;

                case JsonTokenType.StartArray:
                    // Provider returned [] instead of {} — skip the array and treat as empty.
                    reader.Skip();
                    return empty;

                case JsonTokenType.StartObject:
                    // Normal case: {"1": [...], "2": [...]}
                    var result = new Dictionary<string, List<EpisodeInfo>>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName) continue;
                        var key = reader.GetString();
                        reader.Read();
                        var episodes = JsonSerializer.Deserialize<List<EpisodeInfo>>(ref reader, options)
                                       ?? new List<EpisodeInfo>();
                        result[key] = episodes;
                    }
                    return result;

                default:
                    reader.Skip();
                    return empty;
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<string, List<EpisodeInfo>> value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
