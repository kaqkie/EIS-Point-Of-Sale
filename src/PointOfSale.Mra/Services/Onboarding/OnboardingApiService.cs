using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Onboarding;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;

namespace PointOfSale.Mra.Services.Onboarding;

public sealed class OnboardingApiService : Http.MraApiClientBase
{
    private const string ActivateTerminalPath = "onboarding/activate-terminal";
    private const string TerminalActivatedConfirmationPath = "onboarding/terminal-activated-confirmation";

    private readonly MraApiOptions _options;

    public OnboardingApiService(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger<OnboardingApiService> logger)
        : base(httpClient, options, logger)
    {
        _options = options.Value;
    }

    public Task<EisApiResponse<ActivateTerminalResponseData>> ActivateTerminalAsync(
        ActivateTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        // Production only: attach vendor x-access-key. Sandbox omits the header.
        var accessKey = MraVendorAccessKeyPolicy.ResolveForActivateTerminal(_options);
        return PostJsonAsync<ActivateTerminalRequest, ActivateTerminalResponseData>(
            ActivateTerminalPath,
            request,
            vendorAccessKey: accessKey,
            cancellationToken: cancellationToken);
    }

    public Task<EisApiResponse<TerminalActivatedConfirmationResponseData>> ConfirmTerminalActivatedAsync(
        TerminalActivatedConfirmationRequest request,
        string terminalActivationCode,
        string secretKey,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<TerminalActivatedConfirmationRequest, TerminalActivatedConfirmationResponseData>(
            TerminalActivatedConfirmationPath,
            request,
            xSignaturePlainText: terminalActivationCode,
            secretKeyForSignature: secretKey,
            cancellationToken: cancellationToken);
}
