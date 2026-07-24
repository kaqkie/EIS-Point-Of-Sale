using System.Text.Json;
using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Serialization;

public static class MraJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        options.Converters.Add(new MraDateTimeJsonConverter());
        return options;
    }
}
