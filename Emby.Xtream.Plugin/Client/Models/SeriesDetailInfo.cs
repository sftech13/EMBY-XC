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
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int Id { get; set; }

        [JsonPropertyName("episode_num")]
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int EpisodeNum { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; } = "mp4";

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Duration { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("season")]
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int Season { get; set; }

        [JsonPropertyName("info")]
        [JsonConverter(typeof(FlexibleObjectConverter<EpisodeMediaInfo>))]
        public EpisodeMediaInfo Info { get; set; }
    }

    public class EpisodeMediaInfo
    {
        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Duration { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("movie_image")]
        public string MovieImage { get; set; } = string.Empty;

        [JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; } = string.Empty;

        [JsonPropertyName("duration_secs")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? DurationSecs { get; set; }

        [JsonPropertyName("bitrate")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Bitrate { get; set; }

        [JsonPropertyName("video")]
        [JsonConverter(typeof(FlexibleObjectConverter<EpisodeVideoInfo>))]
        public EpisodeVideoInfo Video { get; set; }

        [JsonPropertyName("audio")]
        [JsonConverter(typeof(FlexibleObjectConverter<EpisodeAudioInfo>))]
        public EpisodeAudioInfo Audio { get; set; }

        // Some Xtream implementations flatten probe data into the info object
        // rather than returning nested video/audio objects. Keep these aliases
        // optional so richer providers can populate generic NFO streamdetails
        // without making codec data a requirement for metadata sidecars.
        [JsonPropertyName("video_codec")]
        public string VideoCodec { get; set; } = string.Empty;

        [JsonPropertyName("video_codec_name")]
        public string VideoCodecName { get; set; } = string.Empty;

        [JsonPropertyName("audio_codec")]
        public string AudioCodec { get; set; } = string.Empty;

        [JsonPropertyName("audio_codec_name")]
        public string AudioCodecName { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Height { get; set; }

        [JsonPropertyName("resolution")]
        public string Resolution { get; set; } = string.Empty;

        [JsonPropertyName("frame_rate")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string FrameRate { get; set; } = string.Empty;

        [JsonPropertyName("r_frame_rate")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string RFrameRate { get; set; } = string.Empty;

        [JsonPropertyName("channels")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? Channels { get; set; }

        [JsonPropertyName("sample_rate")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string SampleRate { get; set; } = string.Empty;

        [JsonPropertyName("audio_bitrate")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string AudioBitRate { get; set; } = string.Empty;
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

        [JsonPropertyName("bit_rate")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string BitRate { get; set; } = string.Empty;
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
