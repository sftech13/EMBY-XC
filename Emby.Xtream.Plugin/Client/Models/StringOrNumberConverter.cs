using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Handles Xtream API fields that are sometimes a quoted string and sometimes a bare number.
    /// For example, the "rating" field may arrive as "7.5" or as 7.5 depending on provider.
    /// </summary>
    internal sealed class StringOrNumberConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString() ?? string.Empty;
                case JsonTokenType.Number:
                    // Preserve exact representation where possible.
                    if (reader.TryGetDouble(out var d))
                        return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return string.Empty;
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Null:
                    return string.Empty;
                default:
                    return string.Empty;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
