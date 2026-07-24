# Albert Retail Terminal — Deployment

## Folder publish

```powershell
dotnet publish "src\PointOfSale.App\PointOfSale.App.csproj" `
  -c Release `
  /p:PublishProfile=FolderProfile
```

Output: `publish\AlbertRetailTerminal\` (self-contained win-x64).

## MSI installer (Phase 10)

Requires [WiX Toolset SDK 5](https://wixtoolset.org/) (`WixToolset.Sdk`).

```powershell
# Optional Authenticode env (see Setup\CodeSigning.md)
$env:ART_CODE_SIGN_CERT_PATH = "C:\secure\certs\albert-retail-codesign.pfx"
$env:ART_CODE_SIGN_CERT_PASSWORD = "<from vault>"

.\Setup\Build-Installer.ps1 -ProductVersion 1.0.0 -ConfigureFirewall
```

Produces:
- `publish\AlbertRetailTerminal\` — published app + SQL scripts
- `publish\Installer\AlbertRetailTerminal.msi` — per-machine MSI (SQL Express `SQLEXPRESS` launch condition)

Post-install (if not using `-ConfigureFirewall`):

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\Albert Retail Terminal\Setup\ConfigureFirewall.ps1"
```

### First-launch database provisioning

On startup, `DatabaseBootstrapService` (idempotent) ensures SQL Express is reachable and creates:
`Terminals`, `Configurations`, `OfflineInvoiceQueue`, `LocalInventory`, plus later migrations / audit log.
Schema version is stored in `dbo.Configurations` (`Schema.Version`).

Manual scripts remain under `Scripts\` for DBA use.

## Auto-updates

Configure `ApplicationUpdate` in `appsettings.Production.json`:

| Setting | Purpose |
| --- | --- |
| `Enabled` | Turn on feed polling |
| `FeedUrl` | HTTPS JSON manifest (see `Setup\update-feed.example.json`) |
| `CheckIntervalMinutes` | Background poll interval |
| `StageOnlyDuringBusinessHours` | Download/stage only; apply on next restart |

Flow: background download → SHA-256 verify → stage under `%LocalAppData%\AlbertRetailTerminal\Updates\` → apply on next launch (cashiers keep working until restart).

Optional web/ClickOnce-style publish:

```powershell
.\Deployment\Package-ClickOnce.ps1 -ProductVersion 1.0.0
```

MSIX sideload package (requires Windows SDK `MakeAppx.exe`):

```powershell
.\Deployment\Package-Msix.ps1 -ProductVersion 1.0.0.0
```

Host `Deployment\Msix\AlbertRetailTerminal.appinstaller` on HTTPS for in-store updates.

## Terminal provisioning wizard (Phase 25)

Nav: **Terminal Setup** (Store Manager / Administrator).

1. **Prepare deployment** — creates `Logs/`, `Backups/`, fiscal archive folders, binds hardware fingerprint, verifies SQL Express via bootstrap.
2. **Activate with MRA** — enter TAC, branch/site, taxpayer TIN; calls MRA `onboarding/activate-terminal` + confirmation; JWT and secret key stored with DPAPI in `dbo.Configurations`.

Packaging metadata: `src/PointOfSale.App/Deployment/InstallerConfiguration.cs` and `Deployment/InstallerConfiguration.targets`.


Nav: **Analytics**

- Daily / weekly / monthly tax reconciliation by MRA rate codes (standard 17.5%, zero-rated, exempt)
- Shift open/close, cash-in/out, Z-report with fiscalized invoice list
- CSV + print/PDF export for managers and tax officers

SQL: `Scripts\004_AnalyticsAndShifts.sql` (also applied on first launch via database bootstrap for shift tables).

```powershell
sqlcmd -S .\SQLEXPRESS -E -i "Scripts\004_AnalyticsAndShifts.sql"
```

## MRA compliance certification (Phase 11)

Admin panel: **MRA Compliance** in the left rail.

1. Run **Run Certification + Export** against the configured MRA environment (requires activated terminal + connectivity for live steps).
2. Automated mock suite: `Tests/Compliance/MraCertificationRunner.cs` (xUnit) writes `Logs/MraCertificationAudit.json`.
3. Packages land in `Documents\AlbertRetailTerminal\CompliancePackages\MraCompliancePackage_[TerminalId]_[DateTime].zip` containing audit JSON, schema snapshot, execution report, SQL scripts, and recent logs.

```powershell
dotnet test "Tests\PointOfSale.Tests.csproj" -c Release --filter "FullyQualifiedName~MraCertification"
```

## Environment profiles

Set **`ART_ENV`** (or `DOTNET_ENVIRONMENT`) before launch:

| Value | MRA API |
| --- | --- |
| `Sandbox` (default) | `https://dev-eis-api.mra.mw/api/v1/` |
| `Production` | `https://eis-api.mra.mw/api/v1/` |

```powershell
$env:ART_ENV = "Production"
.\AlbertRetailTerminal.exe
```

## Production go-live checklist

1. Set `ART_ENV=Production`.
2. Fill `TerminalDeployment` (Branch/Site; TAC only for first activation).
3. Pair `ThermalPrinter`.
4. Build/sign MSI; deploy SQL Express + firewall rule.
5. Complete onboarding (DPAPI secrets).
6. Point `ApplicationUpdate:FeedUrl` at your internal update host.
7. Smoke-test sale + receipt.

## Cashier shortcuts (Checkout)

| Key | Action |
| --- | --- |
| F2 | Add selected product |
| F5 | Exact cash tender |
| F8 | Open offline queue |
| F9 | Reprint last fiscal receipt |
| F12 | Complete sale |

## SQL maintenance

```powershell
sqlcmd -S .\SQLEXPRESS -E -i "Scripts\003_ProductionMaintenance.sql"
EXEC dbo.usp_CleanupSyncedOfflineInvoices @RetentionDays = 90;
EXEC dbo.usp_CleanupMraApiAuditLog @RetentionDays = 90;
```

## Logs

| Path | Content |
| --- | --- |
| `Logs/app-*.log` | Serilog application log |
| `Logs/MraAudit/mra-audit-*.log` | Scrubbed MRA audit |
| `Logs/Critical/critical-*.log` | Unhandled exceptions |
| `dbo.MraApiAuditLog` | SQL audit |

Sensitive fields are redacted in all audit outputs.
