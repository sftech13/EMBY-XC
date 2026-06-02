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

        [JsonPropertyName("info")]
        public EpisodeMediaInfo Info { get; set; }
    }

    public class EpisodeMediaInfo
    {
        [JsonPropertyName("duration_secs")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? DurationSecs { get; set; }

        [JsonPropertyName("bitrate")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Bitrate { get; set; }

        [JsonPropertyName("video")]
        public EpisodeVideoInfo Video { get; set; }

        [JsonPropertyName("audio")]
        public EpisodeAudioInfo Audio { get; set; }
    }

    public class EpisodeVideoInfo
    {
        [JsonPropertyName("codec_name")]
        public string CodecName { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Height { get; set; }

        [JsonPropertyName("display_aspect_ratio")]
        public string DisplayAspectRatio { get; set; } = string.Empty;

        [JsonPropertyName("r_frame_rate")]
        public string RFrameRate { get; set; } = string.Empty;

        [JsonPropertyName("field_order")]
        public string FieldOrder { get; set; } = string.Empty;
    }

    public class EpisodeAudioInfo
    {
        [JsonPropertyName("codec_name")]
        public string CodecName { get; set; } = string.Empty;

        [JsonPropertyName("channels")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Channels { get; set; }

        [JsonPropertyName("sample_rate")]
        public string SampleRate { get; set; } = string.Empty;

        [JsonPropertyName("bit_rate")]
        public string BitRate { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }
}
