using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ordering.Api.Serialization;

/// <summary>
/// Snowflake ids use up to 63 bits; JavaScript numbers are exact only to 2^53. Every <c>long</c>
/// therefore crosses the wire as a JSON string (and is accepted back as string or number).
/// </summary>
public sealed class LongAsStringJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt64();
        }

        if (reader.TokenType == JsonTokenType.String
            && long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new JsonException("Expected a 64-bit integer, as a number or a string.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
