using System;
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
                    return reader.TryGetInt64(out var n) ? n : 0L;
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return 0L;
                    return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0L;
                case JsonTokenType.Null:
                case JsonTokenType.False:
                case JsonTokenType.True:
                default:
                    return 0L;
            }
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }
}
