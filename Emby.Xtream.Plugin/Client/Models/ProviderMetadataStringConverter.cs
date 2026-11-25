using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    // Opt-in for provider metadata only. Primary IDs retain strict converters.
    internal sealed class ProviderMetadataStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String: return reader.GetString();
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                // A boolean is not a valid codec, date, genre or description.
                // Treat absent optional metadata as absent, not as invented text.
                case JsonTokenType.True:
                case JsonTokenType.False: return null;
                case JsonTokenType.StartArray:
                case JsonTokenType.StartObject: reader.Skip(); return null;
                default: return null;
            }
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    internal sealed class EpisodeNumberConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            int number;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out number) && number >= 0) return number;
            if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 0) return number;
            reader.Skip();
            return 1;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

}
