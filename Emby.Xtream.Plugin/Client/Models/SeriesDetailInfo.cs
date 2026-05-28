using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class SeriesDetailInfo
    {
        [JsonPropertyName("episodes")]
        [JsonConverter(typeof(FlexibleEpisodesConverter))]
        public Dictionary<string, List<EpisodeInfo>> Episodes { get; set; } = new Dictionary<string, List<EpisodeInfo>>();

        [JsonPropertyName("info")]
        public SeriesInfo Info { get; set; }
    }

    public class EpisodeInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("episode_num")]
        public int EpisodeNum { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; } = "mp4";

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("season")]
        public int Season { get; set; }
    }
}
