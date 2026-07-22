# Albert Retail Terminal — First-run checklist (Phase 35)

1. Confirm **SQL Server Express (`.\SQLEXPRESS`)** or **LocalDB (`(localdb)\MSSQLLocalDB`)** is installed.
2. Optionally run elevated: `Setup\Bootstrap-SqlExpressOrLocalDb.ps1 -WriteDeploymentOverride`
3. Launch **Albert Retail Terminal** from the Start Menu or Desktop shortcut.
4. Complete the **First-run setup wizard** (terminal name, branch, MRA sandbox/production, license key).
5. Sign in with the seeded admin account (`admin` / `admin123`) and change the password.
6. Cashier default: `cashier` / `cashier123`.

Default statutory VAT is **17.5%** (Malawi standard rate). Schema migrations run automatically on first launch.
