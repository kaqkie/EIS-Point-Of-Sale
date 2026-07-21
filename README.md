# Point of Sale — MRA EIS Integration

## Phase 1 — Database

```powershell
sqlcmd -S .\SQLEXPRESS -E -i "Scripts\SetupDatabase.sql"
```

Tables: `Terminals`, `Configurations`, `OfflineInvoiceQueue`, `LocalInventory`.

Connection string: `appsettings.json` → `ConnectionStrings:PosDatabase`.

## Phase 2 — Onboarding & configuration

- `src/PointOfSale.Infrastructure/Services/MraApiClient.cs` — JWT + HMAC-SHA512 `x-signature`
- `src/PointOfSale.Infrastructure/Services/TerminalOnboardingService.cs`
  - `ActivateTerminalAsync`
  - `ConfirmTerminalActivationAsync` (persists DPAPI-protected `SecretKey` on `Terminals`)
  - `GetLatestConfigsAsync` — **GET** `configuration/get-latest-configs` per [MRA docs](https://eis-api.mra.mw/docs/request_3.htm)

### WPF host registration

```csharp
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

services.AddPointOfSaleInfrastructure(configuration);
```

```powershell
dotnet build PointOfSale.sln -c Release
```
