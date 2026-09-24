using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class SeriesInfo
    {
        [JsonPropertyName("series_id")]
        [JsonConverter(typeof(FlexibleInt32Converter))]
        public int SeriesId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string Cover { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string ReleaseDateAlt { get; set; } = string.Empty;

        [JsonPropertyName("year")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Year { get; set; } = string.Empty;

        [JsonPropertyName("episode_run_time")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string EpisodeRunTime { get; set; } = string.Empty;

        [JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        [JsonConverter(typeof(FlexibleNullableInt32Converter))]
        public int? CategoryId { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("last_modified")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string LastModified { get; set; } = string.Empty;

        [JsonPropertyName("tmdb")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string TmdbId { get; set; } = string.Empty;

        [JsonPropertyName("tmdb_id")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string TmdbIdAlt { get; set; } = string.Empty;

        [JsonPropertyName("tvdb")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string TvdbId { get; set; } = string.Empty;

        [JsonPropertyName("tvdb_id")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string TvdbIdAlt { get; set; } = string.Empty;

        [JsonPropertyName("imdb")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string ImdbId { get; set; } = string.Empty;

        [JsonPropertyName("imdb_id")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string ImdbIdAlt { get; set; } = string.Empty;
    }
}
