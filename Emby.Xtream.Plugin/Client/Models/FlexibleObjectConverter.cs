using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Returns null when the JSON value is not an object (empty string, array, number, null, etc.).
    /// Some XC providers return "" or [] for optional object fields like info.audio or info.video.
    /// </summary>
    internal sealed class FlexibleObjectConverter<T> : JsonConverter<T> where T : class
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return null;
            }
            return JsonSerializer.Deserialize<T>(ref reader, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, value, options);
        }
    }
}
