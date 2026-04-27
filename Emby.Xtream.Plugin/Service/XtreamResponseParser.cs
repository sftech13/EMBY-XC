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
            if (string.IsNullOrWhiteSpace(json))
                return new List<SeriesInfo>();

            try
            {
                var list = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, options);
                if (list != null)
                    return list;
            }
            catch
            {
            }

            try
            {
                var dict = STJ.JsonSerializer.Deserialize<Dictionary<string, SeriesInfo>>(json, options);
                if (dict != null)
                    return dict.Values.Where(v => v != null).ToList();
            }
            catch
            {
            }

            return new List<SeriesInfo>();
        }
    }
}
