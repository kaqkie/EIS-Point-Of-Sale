using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Serialization;

/// <summary>
/// Serializes invoice timestamps in the MRA EIS sample format
/// (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>). Default System.Text.Json emits up to 7
/// fractional digits, which the sandbox frequently rejects with a generic HTTP 500.
/// </summary>
public sealed class MraDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string WriteFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a string for MRA DateTime.");
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("MRA DateTime string was empty.");
        }

        if (!DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            throw new JsonException($"Invalid MRA DateTime value '{text}'.");
        }

        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        // Truncate (do not round) to whole milliseconds — matches MRA docs samples.
        utc = new DateTime(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc);

        writer.WriteStringValue(utc.ToString(WriteFormat, CultureInfo.InvariantCulture));
    }
}
