using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    internal sealed class FlexibleNullableDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetDouble(out var n) ? n : (double?)null;
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : (double?)null;
                case JsonTokenType.Null:
                case JsonTokenType.False:
                case JsonTokenType.True:
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
    }
}
