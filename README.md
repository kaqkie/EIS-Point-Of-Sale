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
dotnet clean PointOfSale.sln
```

> Tip: The repo root has both `PointOfSale.sln` and `AlbertRetailTerminal.Host.csproj`, so bare `dotnet clean` / `dotnet build` need the `.sln` argument (MSB1011). Bare `dotnet run` works and launches the terminal via the host project.

### Local run (solution root)

From the repository root, launch the WPF terminal without `-p` flags:

```powershell
cd "C:\Users\Albert Zee\Documents\Projects\Point Of Sale"
dotnet run
```

This uses `AlbertRetailTerminal.Host.csproj`: it builds `PointOfSale.App`, sets the working directory to `src/PointOfSale.App` (for `appsettings.json`, SQL scripts, and logs), then starts `AlbertRetailTerminal`. In Cursor/VS Code, **F5** uses `.vscode/launch.json` to debug `PointOfSale.App` directly (default build task: `.vscode/tasks.json`).

Equivalent explicit command:

```powershell
dotnet run --project src/PointOfSale.App/PointOfSale.App.csproj
```
