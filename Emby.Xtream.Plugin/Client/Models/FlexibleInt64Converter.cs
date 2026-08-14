using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    internal sealed class FlexibleInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    long number;
                    return reader.TryGetInt64(out number) ? number : 0L;
                case JsonTokenType.String:
                    var value = reader.GetString();
                    long parsed;
                    return !string.IsNullOrWhiteSpace(value) &&
                           long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                        ? parsed
                        : 0L;
                default:
                    return 0L;
            }
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
