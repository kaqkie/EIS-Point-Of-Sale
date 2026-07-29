using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;
using PointOfSale.Mra.Services;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Validates MRA VAT 5 certificates, applies relief pricing for <c>isReliefSupply</c> sales,
/// and tracks reusable certificate quantity balances across partial usage.
/// </summary>
public sealed class Vat5CertificateValidationService
{
    private readonly MraApiClient _apiClient;
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IVat5CertificateResponseService _responseParser;
    private readonly ILogger<Vat5CertificateValidationService> _logger;

    public Vat5CertificateValidationService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        IConfigurationRepository configurationRepository,
        ILogger<Vat5CertificateValidationService> logger,
        IVat5CertificateResponseService? responseParser = null)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _configurationRepository = configurationRepository;
        _logger = logger;
        _responseParser = responseParser
            ?? new Vat5CertificateResponseService(NullLogger<Vat5CertificateResponseService>.Instance);
    }

    /// <summary>
    /// <c>POST /api/v1/utilities/validate-vat5-certificate</c> —
    /// <c>Accept: text/plain</c>, JSON body, <c>Authorization: Bearer {jwt}</c>.
    /// Parses the EIS envelope, evaluates authenticity/expiry/quantity, and updates the local ledger.
    /// </summary>
    public async Task<Vat5ValidationResult> ValidateVat5CertificateAsync(
        ValidateVat5CertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CertificateNumber);
        if (request.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Validation quantity must be greater than zero.");
        }

        var payload = new ValidateVat5CertificateRequest
        {
            ProjectNumber = request.ProjectNumber.Trim(),
            CertificateNumber = request.CertificateNumber.Trim(),
            Quantity = request.Quantity
        };

        var signed = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var context = new MraRequestContext
        {
            JwtToken = signed.JwtToken,
            SecretKey = signed.SecretKey,
            UseBearerAuthorization = true,
            AcceptHeader = "text/plain"
        };

        var response = await _apiClient
            .PostAsync<ValidateVat5CertificateRequest, Vat5CertificateValidationData>(
                "utilities/validate-vat5-certificate",
                payload,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        var existing = await ReadLedgerAsync(payload.ProjectNumber, payload.CertificateNumber, cancellationToken)
            .ConfigureAwait(false);
        var parsed = _responseParser.Validate(
            response,
            requestedQuantity: payload.Quantity,
            alreadyConsumedQuantity: existing?.ConsumedQuantity ?? 0m);

        if (!parsed.Success || parsed.Data is null || parsed.Evaluation is null)
        {
            _logger.LogWarning(
                "validate-vat5-certificate failed for project={Project} cert={Certificate}. Remark={Remark}",
                payload.ProjectNumber,
                payload.CertificateNumber,
                parsed.Remark ?? "(null)");
            return Vat5ValidationResult.Failed(
                parsed.Remark ?? "VAT 5 certificate validation failed.",
                parsed.StatusCode,
                parsed.Errors);
        }

        var data = parsed.Data;
        var evaluation = parsed.Evaluation;
        var project = evaluation.ProjectNumber ?? payload.ProjectNumber;
        var certificate = evaluation.CertificateNumber ?? payload.CertificateNumber;
        var approvedQty = evaluation.ApprovedQuantity > 0 ? evaluation.ApprovedQuantity : payload.Quantity;

        var ledger = await UpsertLedgerFromValidationAsync(
                project,
                certificate,
                approvedQty,
                data.DateOfIssue,
                data.DateOfExpiry,
                cancellationToken)
            .ConfigureAwait(false);

        // Re-evaluate against the persisted ledger remaining quantity.
        evaluation = _responseParser.EvaluateCertificate(
            data,
            payload.Quantity,
            alreadyConsumedQuantity: ledger.ConsumedQuantity);

        if (evaluation.IsExpired)
        {
            _logger.LogWarning(
                "VAT5 certificate expired. project={Project} cert={Certificate} expiry={Expiry}",
                project,
                certificate,
                data.DateOfExpiry);
        }
        else if (!evaluation.CanCoverRequestedQuantity)
        {
            _logger.LogWarning(
                "VAT5 certificate quantity insufficient. project={Project} cert={Certificate} requested={Requested} remaining={Remaining}",
                project,
                certificate,
                payload.Quantity,
                ledger.RemainingQuantity);
        }
        else
        {
            _logger.LogInformation(
                "VAT5 certificate valid. project={Project} cert={Certificate} approved={Approved} remaining={Remaining} requested={Requested}",
                project,
                certificate,
                approvedQty,
                ledger.RemainingQuantity,
                payload.Quantity);
        }

        return Vat5ValidationResult.Succeeded(
            data,
            ledger,
            evaluation,
            remark: parsed.Remark ?? evaluation.Message);
    }

    /// <summary>
    /// Parses a raw successful EIS JSON body and, when eligible, applies VAT relief to the sales request.
    /// </summary>
    public Vat5ReliefProcessingResult ProcessSuccessfulValidationResponse(
        string? rawJson,
        SubmitSalesTransactionRequest salesRequest,
        decimal usageQuantity,
        decimal alreadyConsumedQuantity = 0m)
    {
        ArgumentNullException.ThrowIfNull(salesRequest);
        if (usageQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usageQuantity), "Usage quantity must be greater than zero.");
        }

        var parsed = _responseParser.ParseJson(rawJson, usageQuantity, alreadyConsumedQuantity);
        if (!parsed.Success || parsed.Data is null || parsed.Evaluation is null)
        {
            return Vat5ReliefProcessingResult.Failed(
                parsed.Remark ?? "Unable to parse VAT5 validation response.",
                parsed.ErrorDetail,
                parsed);
        }

        if (!parsed.AllowsReliefSupply)
        {
            return Vat5ReliefProcessingResult.Failed(
                parsed.Evaluation.Message ?? "VAT5 certificate is not eligible for relief supply.",
                parsed.ErrorDetail,
                parsed);
        }

        var relieved = ApplyReliefSupplyToSalesRequest(salesRequest, parsed.Data, usageQuantity);
        return Vat5ReliefProcessingResult.Succeeded(parsed, relieved);
    }

    /// <summary>
    /// Rebuilds a sales request as a relief supply: attaches VAT5 details, sets
    /// <c>isReliefSupply=true</c>, and removes standard VAT from line totals / summary.
    /// </summary>
    public SubmitSalesTransactionRequest ApplyReliefSupplyToSalesRequest(
        SubmitSalesTransactionRequest request,
        Vat5CertificateValidationData certificate,
        decimal usageQuantity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(certificate);
        if (usageQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usageQuantity), "Usage quantity must be greater than zero.");
        }

        var project = FirstNonEmpty(certificate.ProjectNumber)
            ?? throw new InvalidOperationException("Validated certificate is missing projectNumber.");
        var certNumber = FirstNonEmpty(certificate.CertificateNumber)
            ?? throw new InvalidOperationException("Validated certificate is missing certificateNumber.");

        var relievedLines = new List<InvoiceLineItemDto>(request.InvoiceLineItems.Count);
        var taxBuckets = new Dictionary<string, (decimal Taxable, decimal Tax)>(StringComparer.OrdinalIgnoreCase);
        decimal totalVat = 0m;
        decimal invoiceTotal = 0m;

        foreach (var line in request.InvoiceLineItems)
        {
            var isStandard = MraTaxRateCodes.IsStandardVatTier(line.TaxRateId);
            var ratePercent = isStandard
                ? PosTaxCalculator.MalawiStandardVatRatePercent
                : 0m;

            var (net, vat, gross) = PosTaxCalculator.ApplyReliefSupplyLine(
                line.UnitPrice,
                line.Quantity,
                ratePercent,
                isStandardVatTier: isStandard);

            // If caller already supplied discounted totals, keep net after VAT strip for standard tiers.
            if (line.Discount > 0 || (line.Total > 0 && line.TotalVat > 0 && isStandard))
            {
                var netAfterDiscount = PosTaxCalculator.RoundMoney(Math.Max(0m, line.Total - line.TotalVat));
                vat = 0m;
                gross = netAfterDiscount;
                net = netAfterDiscount;
            }

            relievedLines.Add(new InvoiceLineItemDto
            {
                Id = line.Id,
                ProductCode = line.ProductCode,
                Description = line.Description,
                UnitPrice = line.UnitPrice,
                Quantity = line.Quantity,
                Discount = line.Discount,
                Total = gross,
                TotalVat = vat,
                TaxRateId = line.TaxRateId,
                IsProduct = line.IsProduct
            });

            totalVat += vat;
            invoiceTotal += gross;

            var rateId = string.IsNullOrWhiteSpace(line.TaxRateId) ? MraTaxRateCodes.StandardVat : line.TaxRateId.Trim();
            if (taxBuckets.TryGetValue(rateId, out var bucket))
            {
                taxBuckets[rateId] = (bucket.Taxable + net, bucket.Tax + vat);
            }
            else
            {
                taxBuckets[rateId] = (net, vat);
            }
        }

        var taxBreakDown = taxBuckets
            .Select(kvp => new TaxBreakDownDto
            {
                RateId = kvp.Key,
                TaxableAmount = PosTaxCalculator.RoundMoney(kvp.Value.Taxable),
                TaxAmount = PosTaxCalculator.RoundMoney(kvp.Value.Tax)
            })
            .ToArray();

        var header = new InvoiceHeaderDto
        {
            InvoiceNumber = request.InvoiceHeader.InvoiceNumber,
            InvoiceDateTime = request.InvoiceHeader.InvoiceDateTime,
            SellerTin = request.InvoiceHeader.SellerTin,
            BuyerTin = request.InvoiceHeader.BuyerTin,
            BuyerName = request.InvoiceHeader.BuyerName,
            BuyerAuthorizationCode = request.InvoiceHeader.BuyerAuthorizationCode,
            SiteId = request.InvoiceHeader.SiteId,
            GlobalConfigVersion = request.InvoiceHeader.GlobalConfigVersion,
            TaxpayerConfigVersion = request.InvoiceHeader.TaxpayerConfigVersion,
            TerminalConfigVersion = request.InvoiceHeader.TerminalConfigVersion,
            IsReliefSupply = true,
            Vat5CertificateDetails = new Vat5CertificateDetailsDto
            {
                ProjectNumber = project,
                CertificateNumber = certNumber,
                Quantity = usageQuantity
            },
            PaymentMethod = request.InvoiceHeader.PaymentMethod
        };

        var summary = request.InvoiceSummary with
        {
            TaxBreakDown = taxBreakDown,
            TotalVat = PosTaxCalculator.RoundMoney(totalVat),
            InvoiceTotal = PosTaxCalculator.RoundMoney(invoiceTotal),
            AmountTendered = PosTaxCalculator.RoundMoney(
                request.InvoiceSummary.AmountTendered > 0
                    ? Math.Max(request.InvoiceSummary.AmountTendered - request.InvoiceSummary.TotalVat, invoiceTotal)
                    : invoiceTotal)
        };

        return request with
        {
            InvoiceHeader = header,
            InvoiceLineItems = relievedLines,
            InvoiceSummary = summary
        };
    }

    /// <summary>
    /// Records partial certificate usage after a successful relief sale so remaining balance can be reused.
    /// </summary>
    public async Task<Vat5CertificateBalanceLedger> RecordCertificateConsumptionAsync(
        string projectNumber,
        string certificateNumber,
        decimal quantityUsed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateNumber);
        if (quantityUsed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityUsed), "Consumed quantity must be greater than zero.");
        }

        var ledger = await GetBalanceLedgerAsync(projectNumber, certificateNumber, cancellationToken)
            .ConfigureAwait(false)
            ?? new Vat5CertificateBalanceLedger
            {
                ProjectNumber = projectNumber.Trim(),
                CertificateNumber = certificateNumber.Trim(),
                ApprovedQuantity = quantityUsed,
                ConsumedQuantity = 0m
            };

        if (quantityUsed > ledger.RemainingQuantity + 0.0001m)
        {
            throw new InvalidOperationException(
                $"Cannot consume {quantityUsed} from VAT5 certificate {certificateNumber}; remaining={ledger.RemainingQuantity}.");
        }

        ledger = new Vat5CertificateBalanceLedger
        {
            ProjectNumber = ledger.ProjectNumber,
            CertificateNumber = ledger.CertificateNumber,
            ApprovedQuantity = ledger.ApprovedQuantity,
            ConsumedQuantity = PosTaxCalculator.RoundMoney(ledger.ConsumedQuantity + quantityUsed),
            DateOfIssue = ledger.DateOfIssue,
            DateOfExpiry = ledger.DateOfExpiry,
            LastValidatedUtc = ledger.LastValidatedUtc,
            LastConsumedUtc = DateTime.UtcNow
        };

        await SaveLedgerAsync(ledger, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Recorded VAT5 consumption project={Project} cert={Certificate} used={Used} remaining={Remaining}",
            ledger.ProjectNumber,
            ledger.CertificateNumber,
            quantityUsed,
            ledger.RemainingQuantity);
        return ledger;
    }

    public Task<Vat5CertificateBalanceLedger?> GetBalanceLedgerAsync(
        string projectNumber,
        string certificateNumber,
        CancellationToken cancellationToken = default) =>
        ReadLedgerAsync(projectNumber, certificateNumber, cancellationToken);

    public static string BuildBalanceCacheKey(string projectNumber, string certificateNumber) =>
        MraConfigurationKeys.Vat5CertificateBalancePrefix
        + Uri.EscapeDataString(projectNumber.Trim())
        + "."
        + Uri.EscapeDataString(certificateNumber.Trim());

    private async Task<Vat5CertificateBalanceLedger> UpsertLedgerFromValidationAsync(
        string projectNumber,
        string certificateNumber,
        decimal approvedQuantity,
        DateTime? dateOfIssue,
        DateTime? dateOfExpiry,
        CancellationToken cancellationToken)
    {
        var existing = await ReadLedgerAsync(projectNumber, certificateNumber, cancellationToken)
            .ConfigureAwait(false);

        var ledger = new Vat5CertificateBalanceLedger
        {
            ProjectNumber = projectNumber,
            CertificateNumber = certificateNumber,
            ApprovedQuantity = approvedQuantity,
            // Keep prior consumption when re-validating the same certificate for partial reuse.
            ConsumedQuantity = existing is null
                ? 0m
                : Math.Min(existing.ConsumedQuantity, approvedQuantity),
            DateOfIssue = dateOfIssue ?? existing?.DateOfIssue,
            DateOfExpiry = dateOfExpiry ?? existing?.DateOfExpiry,
            LastValidatedUtc = DateTime.UtcNow,
            LastConsumedUtc = existing?.LastConsumedUtc
        };

        await SaveLedgerAsync(ledger, cancellationToken).ConfigureAwait(false);
        return ledger;
    }

    private async Task<Vat5CertificateBalanceLedger?> ReadLedgerAsync(
        string projectNumber,
        string certificateNumber,
        CancellationToken cancellationToken)
    {
        var json = await _configurationRepository
            .GetJsonAsync(BuildBalanceCacheKey(projectNumber, certificateNumber), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Vat5CertificateBalanceLedger>(json, MraJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupt VAT5 balance ledger for {Certificate}; treating as empty.", certificateNumber);
            return null;
        }
    }

    private Task SaveLedgerAsync(Vat5CertificateBalanceLedger ledger, CancellationToken cancellationToken) =>
        _configurationRepository.UpsertJsonAsync(
            BuildBalanceCacheKey(ledger.ProjectNumber, ledger.CertificateNumber),
            JsonSerializer.Serialize(ledger, MraJson.SerializerOptions),
            cancellationToken);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

public sealed class Vat5ValidationResult
{
    public bool Success { get; init; }
    public bool IsCertificateValid { get; init; }
    public bool IsExpired { get; init; }
    public bool CanCoverRequestedQuantity { get; init; }
    public bool AllowsReliefSupply => Success && IsCertificateValid && !IsExpired && CanCoverRequestedQuantity;
    public string? Remark { get; init; }
    public int StatusCode { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public Vat5CertificateValidationData? Certificate { get; init; }
    public Vat5CertificateBalanceLedger? Ledger { get; init; }
    public Vat5CertificateEvaluation? Evaluation { get; init; }
    public decimal RequestedQuantity { get; init; }
    public decimal RemainingQuantity => Ledger?.RemainingQuantity ?? Evaluation?.RemainingQuantity ?? 0m;

    public static Vat5ValidationResult Succeeded(
        Vat5CertificateValidationData certificate,
        Vat5CertificateBalanceLedger ledger,
        Vat5CertificateEvaluation evaluation,
        string? remark) =>
        new()
        {
            Success = true,
            StatusCode = 1,
            Remark = remark,
            Certificate = certificate,
            Ledger = ledger,
            Evaluation = evaluation,
            RequestedQuantity = evaluation.RequestedQuantity,
            IsExpired = evaluation.IsExpired,
            CanCoverRequestedQuantity = evaluation.CanCoverRequestedQuantity,
            IsCertificateValid = evaluation.IsAuthentic && !evaluation.IsExpired
        };

    public static Vat5ValidationResult Failed(
        string remark,
        int statusCode,
        IReadOnlyList<EisApiError>? errors) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            Remark = remark,
            Errors = errors,
            IsCertificateValid = false,
            CanCoverRequestedQuantity = false
        };
}

public sealed class Vat5ReliefProcessingResult
{
    public bool Success { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public Vat5CertificateParseResult? Parse { get; init; }
    public SubmitSalesTransactionRequest? RelievedSalesRequest { get; init; }

    public static Vat5ReliefProcessingResult Succeeded(
        Vat5CertificateParseResult parse,
        SubmitSalesTransactionRequest relievedSalesRequest) =>
        new()
        {
            Success = true,
            Remark = parse.Remark ?? parse.Evaluation?.Message,
            Parse = parse,
            RelievedSalesRequest = relievedSalesRequest
        };

    public static Vat5ReliefProcessingResult Failed(
        string remark,
        string? errorDetail,
        Vat5CertificateParseResult? parse) =>
        new()
        {
            Success = false,
            Remark = remark,
            ErrorDetail = errorDetail,
            Parse = parse
        };
}
