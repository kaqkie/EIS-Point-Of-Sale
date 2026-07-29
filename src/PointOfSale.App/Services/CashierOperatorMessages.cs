using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Services;

namespace PointOfSale.App.Services;

/// <summary>
/// Translates raw network / MRA exceptions into cashier-facing operational prompts.
/// </summary>
public static class CashierOperatorMessages
{
    private static readonly IMraEisResponseEvaluator FallbackEvaluator =
        new MraEisResponseEvaluator(NullLogger<MraEisResponseEvaluator>.Instance);

    public static OperatorMessage FromException(Exception exception, bool mraReachable) =>
        FromException(exception, mraReachable, FallbackEvaluator);

    public static OperatorMessage FromException(
        Exception exception,
        bool mraReachable,
        IMraEisResponseEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(evaluator);

        if (exception is MraApiException mra)
        {
            return FromEvaluation(evaluator.EvaluateException(mra), mraReachable);
        }

        return exception switch
        {
            HttpRequestException =>
                new OperatorMessage(
                    "Network connection lost",
                    "Albert Retail Terminal cannot reach the MRA EIS network. " +
                    "Complete the sale in offline mode — it will remain in the sync queue until connectivity returns.",
                    OperatorMessageSeverity.Warning,
                    SuggestOfflineFallback: true),

            TaskCanceledException or OperationCanceledException =>
                new OperatorMessage(
                    "Request timed out",
                    "The MRA request timed out. If the sale was not confirmed, it will stay in the offline queue for retry. " +
                    "Do not resubmit the same invoice until you check Queue Sync.",
                    OperatorMessageSeverity.Warning,
                    SuggestOfflineFallback: true),

            InvalidOperationException ex when ex.Message.Contains("stock", StringComparison.OrdinalIgnoreCase) =>
                new OperatorMessage(
                    "Insufficient stock",
                    Truncate(ex.Message),
                    OperatorMessageSeverity.Error,
                    SuggestOfflineFallback: false),

            InvalidOperationException ex when ex.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("onboarding", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("JWT", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("secret", StringComparison.OrdinalIgnoreCase) =>
                new OperatorMessage(
                    "Terminal not ready",
                    "Complete terminal activation and configuration sync before selling.\n\n" + Truncate(ex.Message),
                    OperatorMessageSeverity.Error,
                    SuggestOfflineFallback: false),

            _ => new OperatorMessage(
                "Sale could not complete",
                Truncate(exception.Message),
                OperatorMessageSeverity.Error,
                SuggestOfflineFallback: !mraReachable)
        };
    }

    public static OperatorMessage FromEvaluation(MraEisResponseEvaluation evaluation, bool mraReachable = true)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.IsSuccess)
        {
            return new OperatorMessage(
                evaluation.OperatorTitle,
                evaluation.OperatorMessage,
                OperatorMessageSeverity.Information,
                SuggestOfflineFallback: false);
        }

        var severity = evaluation.Category switch
        {
            MraEisFailureCategory.ServerError => OperatorMessageSeverity.Warning,
            MraEisFailureCategory.AuthenticationFailure => OperatorMessageSeverity.Warning,
            MraEisFailureCategory.OutdatedConfiguration => OperatorMessageSeverity.Warning,
            _ => OperatorMessageSeverity.Error
        };

        return new OperatorMessage(
            evaluation.OperatorTitle,
            evaluation.OperatorMessage,
            severity,
            SuggestOfflineFallback: evaluation.SuggestOfflineFallback || (!mraReachable && evaluation.IsTransient));
    }

    public static OperatorMessage Quarantined(string? remark) =>
        new(
            "Sale quarantined by MRA",
            "This invoice was rejected and will not block later sales in the queue. " +
            "Open Queue Sync to review details.\n\n" + Truncate(remark ?? "Validation failure."),
            OperatorMessageSeverity.Error,
            SuggestOfflineFallback: false);

    public static OperatorMessage QueuedOffline(string invoiceNumber, bool forcedOffline) =>
        new(
            forcedOffline ? "Sale saved offline" : "Sale queued for sync",
            $"Invoice {invoiceNumber} is stored locally and will submit to MRA in FIFO order. " +
            "A fiscal receipt will print automatically after successful sync.",
            OperatorMessageSeverity.Information,
            SuggestOfflineFallback: false);

    public static OperatorMessage SubmittedOnline(string invoiceNumber) =>
        new(
            "Sale completed",
            $"Invoice {invoiceNumber} was fiscalized online. The thermal receipt is printing.",
            OperatorMessageSeverity.Information,
            SuggestOfflineFallback: false);

    private static string Truncate(string message) =>
        message.Length <= 400 ? message : message[..397] + "...";
}

public enum OperatorMessageSeverity
{
    Information,
    Warning,
    Error
}

public sealed record OperatorMessage(
    string Title,
    string Body,
    OperatorMessageSeverity Severity,
    bool SuggestOfflineFallback);

/// <summary>
/// Validates production secret readiness (DPAPI-backed JWT / terminal secret).
/// </summary>
public interface IProductionSecretGuard
{
    Task EnsureReadyForLiveSalesAsync(CancellationToken cancellationToken = default);
}

public sealed class ProductionSecretGuard : IProductionSecretGuard
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<TerminalDeploymentOptions> _deployment;
    private readonly IOptions<MraApiOptions> _mraOptions;

    public ProductionSecretGuard(
        IServiceScopeFactory scopeFactory,
        IOptions<TerminalDeploymentOptions> deployment,
        IOptions<MraApiOptions> mraOptions)
    {
        _scopeFactory = scopeFactory;
        _deployment = deployment;
        _mraOptions = mraOptions;
    }

    public async Task EnsureReadyForLiveSalesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetService<MraRuntimeEnvironmentState>();
        var isProduction = runtime?.IsLiveProductionActive(_mraOptions.Value)
            ?? _mraOptions.Value.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);

        if (runtime?.FiscalLockoutActive == true)
        {
            throw new InvalidOperationException(
                "MRA fiscal signing certificate lockout is active. Renew credentials from Compliance Audit before live sales.");
        }

        if (!isProduction || !_deployment.Value.RequireEncryptedSecrets)
        {
            return;
        }

        var configurationRepository = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var terminalRepository = scope.ServiceProvider.GetRequiredService<ITerminalRepository>();

        var jwt = await configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException(
                "Production JWT is missing from encrypted configuration. Complete onboarding before live sales.");
        }

        var terminalId = await terminalRepository.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            throw new InvalidOperationException(
                "No activated terminal is registered. Enter the production Terminal Activation Code during onboarding.");
        }

        var terminal = await terminalRepository.GetByIdAsync(terminalId, cancellationToken).ConfigureAwait(false);
        if (terminal is null || string.IsNullOrWhiteSpace(terminal.SecretKey))
        {
            throw new InvalidOperationException(
                "Encrypted terminal secret key is missing. Confirm terminal activation before live sales.");
        }
    }
}
