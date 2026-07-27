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

    /// <summary>
    /// MRA EIS success responses use <c>statusCode: 1</c>.
    /// <c>0</c> is the OpenAPI schema default / failure sentinel — never treat it as success.
    /// </summary>
    public bool IsSuccess => StatusCode == 1;
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
