using System.Text.Json;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

/// <summary>
/// Seeds local MRA fiscal identity (TIN + Site) so checkout can proceed after sandbox/first-run
/// onboarding when live <c>get-latest-configs</c> has not yet populated SQL caches.
/// </summary>
public static class LocalFiscalIdentitySeeder
{
    public static async Task SeedAsync(
        IConfigurationRepository config,
        string terminalId,
        string? branchId,
        string? siteId,
        string? taxpayerTin,
        string? tradingName,
        CancellationToken cancellationToken = default)
    {
        var resolvedSiteName = PosConfigurationService.NormalizeConfiguredValue(siteId)
            ?? PosConfigurationService.NormalizeConfiguredValue(branchId)
            ?? "SITE-LOCAL";
        // MRA expects site codes (SITE-…); convert human labels like "City Center".
        var resolvedSite = PointOfSale.Infrastructure.Services.MraFiscalPayloadNormalizer.NormalizeSiteId(resolvedSiteName);
        // Prefer configured TIN; in sandbox allow the developer seed so trial selling can proceed.
        var resolvedTin = PosConfigurationService.NormalizeTaxpayerTin(taxpayerTin, allowSandboxDeveloperTin: true);
        var resolvedBranch = PosConfigurationService.NormalizeConfiguredValue(branchId) ?? "LOCAL";
        var resolvedName = string.IsNullOrWhiteSpace(tradingName)
            ? "Albert Retail Terminal"
            : tradingName.Trim();

        await config.UpsertJsonAsync(
                DeploymentConfigurationKeys.BranchId,
                resolvedBranch,
                cancellationToken)
            .ConfigureAwait(false);

        await config.UpsertJsonAsync(
                DeploymentConfigurationKeys.SiteIdOverride,
                resolvedSite,
                cancellationToken)
            .ConfigureAwait(false);

        if (resolvedTin is not null)
        {
            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.TaxpayerTin,
                    JsonSerializer.Serialize(new { tin = resolvedTin }, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var existingTaxpayer = await config.GetJsonAsync(MraConfigurationKeys.TaxpayerConfiguration, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingTaxpayer) && resolvedTin is not null)
        {
            var taxpayer = new TaxpayerConfigurationDto
            {
                VersionNo = 1,
                Tin = resolvedTin,
                IsVatRegistered = true,
                TaxOfficeCode = "SBX",
                ActivatedTaxRateIds = ["A"]
            };
            await config.UpsertJsonAsync(
                    MraConfigurationKeys.TaxpayerConfiguration,
                    JsonSerializer.Serialize(taxpayer, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(existingTaxpayer) && resolvedTin is not null)
        {
            // Replace placeholder TIN left by older sandbox seeds so receipts pick up the registered value.
            try
            {
                var existing = JsonSerializer.Deserialize<TaxpayerConfigurationDto>(existingTaxpayer, MraJson.SerializerOptions);
                if (existing is not null && PosConfigurationService.IsPlaceholderTaxpayerTin(existing.Tin))
                {
                    existing.Tin = resolvedTin;
                    await config.UpsertJsonAsync(
                            MraConfigurationKeys.TaxpayerConfiguration,
                            JsonSerializer.Serialize(existing, MraJson.SerializerOptions),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (JsonException)
            {
                // Leave malformed cache alone; operator can repair via Terminal Provisioning.
            }
        }

        var existingTerminal = await config.GetJsonAsync(MraConfigurationKeys.TerminalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingTerminal))
        {
            var terminal = new TerminalConfigurationDto
            {
                VersionNo = 1,
                TerminalLabel = terminalId,
                IsActiveTerminal = true,
                TradingName = resolvedName,
                TerminalSite = new TerminalSiteDto
                {
                    SiteId = resolvedSite,
                    SiteName = resolvedSiteName
                },
                OfflineLimit = new OfflineLimitDto
                {
                    MaxTransactionAgeInHours = 72,
                    MaxCummulativeAmount = 5_000_000m
                }
            };
            await config.UpsertJsonAsync(
                    MraConfigurationKeys.TerminalConfiguration,
                    JsonSerializer.Serialize(terminal, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var existingGlobal = await config.GetJsonAsync(MraConfigurationKeys.GlobalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingGlobal))
        {
            var global = new GlobalConfigurationDto
            {
                Id = 1,
                VersionNo = 1,
                TaxRates =
                [
                    new TaxRateDto
                    {
                        // MRA sample get-latest-configs uses id "A" for standard VAT.
                        Id = MraTaxRateCodes.StandardVat,
                        Name = "VAT-A",
                        ChargeMode = "G",
                        Ordinal = 1,
                        Rate = 17.5m
                    }
                ]
            };
            await config.UpsertJsonAsync(
                    MraConfigurationKeys.GlobalConfiguration,
                    JsonSerializer.Serialize(global, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
