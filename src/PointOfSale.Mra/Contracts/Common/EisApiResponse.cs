using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Common;

public sealed class EisApiResponse<T>
{
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<EisApiError>? Errors { get; set; }

    public bool IsSuccess => StatusCode == 1 || StatusCode == 0;
}

public sealed class EisApiError
{
    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("fieldName")]
    public string? FieldName { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
