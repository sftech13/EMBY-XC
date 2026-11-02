using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Xtream.Plugin.Client.Models;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    internal static class XtreamResponseParser
    {
        public static List<Category> DeserializeCategories(string json, STJ.JsonSerializerOptions options)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<Category>();

            try
            {
                var list = STJ.JsonSerializer.Deserialize<List<Category>>(json, options);
                if (list != null)
                    return list;
            }
            catch
            {
            }

            try
            {
                var dict = STJ.JsonSerializer.Deserialize<Dictionary<string, Category>>(json, options);
                if (dict != null)
                    return dict.Values.Where(v => v != null).ToList();
            }
            catch
            {
            }

            return new List<Category>();
        }

        public static List<SeriesInfo> DeserializeSeriesList(string json, STJ.JsonSerializerOptions options)
        {
            return DeserializeMediaCatalog<SeriesInfo>(json, options, s => s.SeriesId);
        }

        public static List<VodStreamInfo> DeserializeVodList(string json, STJ.JsonSerializerOptions options)
        {
            return DeserializeMediaCatalog<VodStreamInfo>(json, options, s => s.StreamId);
        }

        private static List<T> DeserializeMediaCatalog<T>(
            string json, STJ.JsonSerializerOptions options, Func<T, long> getId) where T : class
        {
            // Do not swallow conversion/shape failures into an empty catalog.
            // Callers must abort before publishing snapshots or deleting orphans.
            // Select the response shape without building a second full DOM for
            // potentially large catalogs. Deserialization validates the whole JSON.
            var trimmed = json?.TrimStart();
            if (string.IsNullOrEmpty(trimmed))
                throw new STJ.JsonException("Provider catalog was empty; sync aborted to protect existing files.");
            List<T> records;
            if (trimmed[0] == '[')
                records = STJ.JsonSerializer.Deserialize<List<T>>(json, options);
            else if (trimmed[0] == '{')
                records = STJ.JsonSerializer.Deserialize<Dictionary<string, T>>(json, options)?.Values.ToList();
            else
                throw new STJ.JsonException("Provider catalog must be an array or object; sync aborted to protect existing files.");

            if (records == null || records.Any(record => record == null || getId(record) <= 0))
                throw new STJ.JsonException("Provider catalog contains a missing or invalid media ID; sync aborted to protect existing files.");
            return records;
        }

        internal static void ValidateEpisodeIds(SeriesDetailInfo detail)
        {
            if (detail?.Episodes == null) return;
            if (detail.Episodes.Values.Any(season => season == null || season.Any(e => e == null || e.Id <= 0)))
            {
                throw new STJ.JsonException("Series detail contains a missing or invalid episode ID; existing episodes must be protected.");
            }
        }
    }
}
