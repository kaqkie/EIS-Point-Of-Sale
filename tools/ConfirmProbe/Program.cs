// Dump live taxpayer/global versions + activated tax links (no secrets).
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Infrastructure;
using PointOfSale.Infrastructure.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(@"c:\Users\Albert Zee\Documents\Projects\Point Of Sale\src\PointOfSale.App")
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Sandbox.json", optional: true)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Error));
services.AddSingleton<IConfiguration>(config);
services.AddPointOfSaleInfrastructure(config);

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var onboarding = scope.ServiceProvider.GetRequiredService<TerminalOnboardingService>();
var latest = await onboarding.GetLatestConfigsAsync();
var t = latest.Configuration?.TaxpayerConfiguration;
var g = latest.Configuration?.GlobalConfiguration;
var term = latest.Configuration?.TerminalConfiguration;
Console.WriteLine($"success={latest.Success} remark={latest.Remark}");
Console.WriteLine($"global v={g?.VersionNo} rates={string.Join(',', g?.TaxRates?.Select(r => $"{r.Id}:{r.Rate}:{r.ChargeMode}") ?? [])}");
Console.WriteLine($"terminal v={term?.VersionNo} site={term?.TerminalSite?.SiteId} label={term?.TerminalLabel}");
Console.WriteLine($"taxpayer v={t?.VersionNo} tin={t?.Tin} vatReg={t?.IsVatRegistered} office={t?.TaxOfficeCode}/{t?.TaxOffice?.Code}");
Console.WriteLine($"activatedIds={string.Join(',', t?.ActivatedTaxRateIds ?? [])}");
Console.WriteLine($"activatedLinks={string.Join(',', t?.ActivatedTaxRates?.Select(a => a.TaxRateId) ?? [])}");
Console.WriteLine($"levies={string.Join(',', t?.ActivatedLevies?.Select(l => $"{l.Id}:{l.Rate}:active={l.IsActive}") ?? [])}");
return 0;
