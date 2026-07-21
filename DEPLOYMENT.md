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

## Production go-live checklist

1. Set `ART_ENV=Production` so `appsettings.Production.json` loads live MRA URLs.
2. Fill `TerminalDeployment` in that file:
   - `BranchId` / `SiteId` for the outlet
   - `TerminalActivationCode` only for first-time activation (remove after onboarding)
   - Keep `RequireEncryptedSecrets: true` so JWT and terminal secrets must exist as DPAPI-protected values in SQL
3. Pair the thermal printer under `ThermalPrinter`:
   - `ConnectionMode`: `Spooler` (Windows queue) or `Serial` (`COM3`, etc.)
   - `PaperWidthMm`: `80` or `58` (sets characters/line and layout)
   - `PrinterName`: optional Windows queue name; empty uses the default printer
4. Complete onboarding so the terminal secret + JWT are stored via DPAPI (`ISecretProtector`).
5. Smoke-test: one sandbox sale, then one production sale with receipt + QR verify URL.

Never commit real activation codes or secrets — production secrets belong in encrypted SQL storage after activation.

## Cashier shortcuts (Checkout)

| Key | Action |
| --- | --- |
| F2 | Add selected product |
| F5 | Exact cash tender |
| F8 | Open offline queue |
| F9 | Reprint last fiscal receipt |
| F12 | Complete sale |

Offline / MRA failures surface as operator dialogs with optional offline-queue fallback.

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
