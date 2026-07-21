using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Onboarding;
using PointOfSale.Mra.Options;

namespace PointOfSale.Mra.Services.Onboarding;

public sealed class OnboardingApiService : Http.MraApiClientBase
{
    private const string ActivateTerminalPath = "onboarding/activate-terminal";
    private const string TerminalActivatedConfirmationPath = "onboarding/terminal-activated-confirmation";

    public OnboardingApiService(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger<OnboardingApiService> logger)
        : base(httpClient, options, logger)
    {
    }

    public Task<EisApiResponse<ActivateTerminalResponseData>> ActivateTerminalAsync(
        ActivateTerminalRequest request,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<ActivateTerminalRequest, ActivateTerminalResponseData>(
            ActivateTerminalPath,
            request,
            cancellationToken: cancellationToken);

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
