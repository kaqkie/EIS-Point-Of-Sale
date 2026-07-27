namespace PointOfSale.Mra.Contracts.Common;

/// <summary>
/// Documentation-facing alias for the standard MRA EIS envelope
/// (<c>statusCode</c>, <c>remark</c>, <c>data</c>, <c>errors</c>).
/// Use <see cref="EisApiResponse{T}"/> in application code.
/// </summary>
public sealed class MraApiResponse<T> : EisApiResponse<T>;
