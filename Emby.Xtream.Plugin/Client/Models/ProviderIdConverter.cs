using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    // Media identifiers must never silently become zero: catalog deduplication
    // and orphan protection depend on preserving each provider record's identity.
    internal sealed class ProviderIdConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            long id;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out id) && id > 0)
                return id;
            if (reader.TokenType == JsonTokenType.String &&
                long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0)
                return id;
            throw new JsonException("Provider media ID must be a positive 64-bit integer; response rejected to protect existing files.");
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }
}
