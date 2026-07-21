# Albert Retail Terminal — Deployment

## Folder publish (recommended)

```powershell
dotnet publish "src\PointOfSale.App\PointOfSale.App.csproj" `
  -c Release `
  /p:PublishProfile=FolderProfile
```

Output: `publish\AlbertRetailTerminal\` (self-contained win-x64).

## Environment profiles

Set **`ART_ENV`** (or `DOTNET_ENVIRONMENT`) before launch:

| Value | MRA API |
| --- | --- |
| `Sandbox` (default) | `https://dev-eis-api.mra.mw/api/v1/` |
| `Production` | `https://apis.mra.mw/api/v1/` |

```powershell
$env:ART_ENV = "Production"
.\AlbertRetailTerminal.exe
```

Overrides live in `appsettings.{ART_ENV}.json` beside the executable.

## SQL maintenance (production)

```powershell
sqlcmd -S .\SQLEXPRESS -E -i "Scripts\003_ProductionMaintenance.sql"
EXEC dbo.usp_CleanupSyncedOfflineInvoices @RetentionDays = 90;
EXEC dbo.usp_CleanupMraApiAuditLog @RetentionDays = 90;
```

## Logs

| Path | Content |
| --- | --- |
| `Logs/app-*.log` | Serilog application log (rolling) |
| `Logs/MraAudit/mra-audit-*.log` | Scrubbed MRA request/response audit |
| `Logs/Critical/critical-*.log` | Unhandled UI/domain exceptions |
| `dbo.MraApiAuditLog` | Scrubbed MRA JSON audit (SQL) |

Sensitive fields (JWT, secret keys, signatures) are redacted in all audit outputs.
