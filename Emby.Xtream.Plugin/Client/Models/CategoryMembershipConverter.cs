using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    // Unknown membership must force category-endpoint fallback, never silently
    // exclude records from a complete-looking catalog used for orphan cleanup.
    internal sealed class CategoryMembershipConverter : JsonConverter<int[]>
    {
        public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return null;
            }

            var ids = new HashSet<int>();
            var valid = true;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                int id;
                if ((reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out id) && id > 0) ||
                    (reader.TokenType == JsonTokenType.String &&
                     int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0))
                    ids.Add(id);
                else
                {
                    valid = false;
                    reader.Skip();
                }
            }
            if (reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("Incomplete category membership array.");
            if (!valid || ids.Count == 0) return null;
            var result = new int[ids.Count];
            ids.CopyTo(result);
            return result;
        }

        public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var id in value) writer.WriteNumberValue(id);
            writer.WriteEndArray();
        }
    }
}
