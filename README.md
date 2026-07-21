# Point of Sale — MRA EIS Integration

Production-oriented foundation for a WPF POS integrated with the [MRA Electronic Invoicing System API v1](https://eis-api.mra.mw/docs/onboarding.htm).

## Phase 1 — Database

Run against SQL Server Express:

```powershell
sqlcmd -S .\SQLEXPRESS -E -i "database\Scripts\001_CreateMraPosSchema.sql"
```

Creates schema `pos` with:

| Table | Purpose |
| --- | --- |
| `Terminals` | Activation state, JWT/secret key, config version pointers |
| `Configurations` | Versioned global / terminal / taxpayer JSON from MRA |
| `OfflineInvoiceQueue` | FIFO (`FifoSequence`) + quarantine columns |
| `LocalInventory` | Local product/stock cache for Stock Operations |
| `OfflineInvoiceFifoSequence` | Atomic FIFO allocator (`usp_AllocateOfflineFifoSequence`) |

## Phase 2 — C# library (`src/PointOfSale.Mra`)

- **DTOs**: Onboarding + Configuration aligned to MRA JSON (`terminalActivationCode`, `productID`, tax rates, offline limits, etc.)
- **`HmacSignatureService`**: Base64 HMAC-SHA512 (activation confirmation signs the TAC; `PostSignedPayloadAsync` signs JSON bodies for later Sales/Stock phases)
- **`OnboardingApiService`**: `activate-terminal`, `terminal-activated-confirmation` (+ `x-signature`)
- **`ConfigurationApiService`**: `get-latest-configs` (JWT `Authorization` header per MRA samples)
- **`TerminalOnboardingService`**: Activate → persist (via `ITerminalStore`) → confirm

### DI registration (WPF host)

```csharp
services.AddMraEisIntegration(options =>
{
    options.BaseUrl = "https://dev-eis-api.mra.mw/api/v1/";
    options.ProductId = "MRA-desktop/{your-guid}";
    options.ProductVersion = "1.0.0";
});
services.AddSingleton<ITerminalStore, SqlTerminalStore>(); // implement against pos.* tables
```

### Build

```powershell
dotnet build PointOfSale.sln
```

## Next phases (planned)

- **Utilities** — VAT 5 validation, invoice numbering helpers
- **Sales** — `submit-sales-transaction`, offline queue drain, last online/offline transaction reconciliation
- **StockOperations** — inventory sync with `LocalInventory`

Implement `ITerminalStore` in a `PointOfSale.Infrastructure` project using ADO.NET/Dapper against the SQL script above.
