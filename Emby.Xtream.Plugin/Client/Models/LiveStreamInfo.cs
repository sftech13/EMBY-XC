using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class LiveStreamInfo
    {
        [JsonPropertyName("num")]
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int Num { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream_type")]
        public string StreamType { get; set; } = string.Empty;

        [JsonPropertyName("stream_id")]
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int StreamId { get; set; }

        [JsonPropertyName("stream_icon")]
        public string StreamIcon { get; set; } = string.Empty;

        [JsonPropertyName("epg_channel_id")]
        public string EpgChannelId { get; set; } = string.Empty;

        [JsonPropertyName("added")]
        [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
        public double? Added { get; set; }

        [JsonPropertyName("category_id")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? CategoryId { get; set; }

        [JsonPropertyName("custom_sid")]
        public string CustomSid { get; set; } = string.Empty;

        [JsonPropertyName("tv_archive")]
        public int TvArchive { get; set; }

        [JsonPropertyName("direct_source")]
        public string DirectSource { get; set; } = string.Empty;

        [JsonPropertyName("tv_archive_duration")]
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int TvArchiveDuration { get; set; }

        [JsonPropertyName("is_adult")]
        public int IsAdult { get; set; }

        public bool IsAdultChannel => IsAdult != 0;
    }
}
