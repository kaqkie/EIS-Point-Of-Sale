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
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? addressLines = null,
        string? contactPhone = null,
        string? contactEmail = null)
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
        var resolvedAddress = addressLines?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList() ?? [];
        var resolvedPhone = PosConfigurationService.NormalizeConfiguredValue(contactPhone);
        var resolvedEmail = PosConfigurationService.NormalizeConfiguredValue(contactEmail);

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

        if (resolvedAddress.Count > 0)
        {
            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.MerchantAddress,
                    JsonSerializer.Serialize(resolvedAddress, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (resolvedPhone is not null)
        {
            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.MerchantPhone,
                    resolvedPhone,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (resolvedEmail is not null)
        {
            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.MerchantEmail,
                    resolvedEmail,
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
                // Prefer live get-latest-configs; local seed stays conservative until MRA confirms VAT.
                IsVatRegistered = false,
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
                if (existing is not null)
                {
                    var dirty = false;
                    if (PosConfigurationService.IsPlaceholderTaxpayerTin(existing.Tin))
                    {
                        existing.Tin = resolvedTin;
                        dirty = true;
                    }

                    if (dirty)
                    {
                        await config.UpsertJsonAsync(
                                MraConfigurationKeys.TaxpayerConfiguration,
                                JsonSerializer.Serialize(existing, MraJson.SerializerOptions),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
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
                PhoneNumber = resolvedPhone,
                EmailAddress = resolvedEmail,
                AddressLines = resolvedAddress.Count > 0 ? resolvedAddress : null,
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
        else
        {
            // Backfill merchant header fields onto an existing terminal cache when they were never set.
            try
            {
                var existing = JsonSerializer.Deserialize<TerminalConfigurationDto>(
                    existingTerminal,
                    MraJson.SerializerOptions);
                if (existing is not null)
                {
                    var dirty = false;
                    if ((existing.AddressLines is null || existing.AddressLines.Count == 0) && resolvedAddress.Count > 0)
                    {
                        existing.AddressLines = resolvedAddress;
                        dirty = true;
                    }

                    if (string.IsNullOrWhiteSpace(existing.PhoneNumber) && resolvedPhone is not null)
                    {
                        existing.PhoneNumber = resolvedPhone;
                        dirty = true;
                    }

                    if (string.IsNullOrWhiteSpace(existing.EmailAddress) && resolvedEmail is not null)
                    {
                        existing.EmailAddress = resolvedEmail;
                        dirty = true;
                    }

                    if (dirty)
                    {
                        await config.UpsertJsonAsync(
                                MraConfigurationKeys.TerminalConfiguration,
                                JsonSerializer.Serialize(existing, MraJson.SerializerOptions),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (JsonException)
            {
                // Leave malformed cache alone.
            }
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
                        ChargeMode = "Item",
                        Ordinal = 1,
                        Rate = PosTaxCalculator.MalawiStandardVatRatePercent
                    }
                ]
            };
            await config.UpsertJsonAsync(
                    MraConfigurationKeys.GlobalConfiguration,
                    JsonSerializer.Serialize(global, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Repair stale sandbox caches that stored wrong A rates (statutory is 17.5%).
            try
            {
                var existing = JsonSerializer.Deserialize<GlobalConfigurationDto>(
                    existingGlobal,
                    MraJson.SerializerOptions);
                if (existing?.TaxRates is { Count: > 0 })
                {
                    var dirty = false;
                    foreach (var rate in existing.TaxRates)
                    {
                        if (MraTaxRateCodes.IsStandardVatTier(rate.Id))
                        {
                            if (rate.Rate > 0m
                                && Math.Abs(rate.Rate - PosTaxCalculator.MalawiStandardVatRatePercent) >= 0.05m
                                && rate.Rate is >= 16m and < 17.5m)
                            {
                                rate.Rate = PosTaxCalculator.MalawiStandardVatRatePercent;
                                dirty = true;
                            }

                            if (!string.Equals(rate.ChargeMode, "Item", StringComparison.OrdinalIgnoreCase))
                            {
                                rate.ChargeMode = "Item";
                                dirty = true;
                            }
                        }
                    }

                    if (dirty)
                    {
                        await config.UpsertJsonAsync(
                                MraConfigurationKeys.GlobalConfiguration,
                                JsonSerializer.Serialize(existing, MraJson.SerializerOptions),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (JsonException)
            {
                // Leave malformed cache alone.
            }
        }
    }
}
