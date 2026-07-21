# Albert Retail Terminal — Code signing parameters (enterprise)

Copy to a secure CI variable store. Do **not** commit real certificates or passwords.

```powershell
$env:ART_CODE_SIGN_CERT_PATH = "C:\secure\certs\albert-retail-codesign.pfx"
$env:ART_CODE_SIGN_CERT_PASSWORD = "<from vault>"
$env:ART_CODE_SIGN_TIMESTAMP_URL = "http://timestamp.digicert.com"
```

Sign published binaries / MSI:

```powershell
.\Setup\Sign-Release.ps1 -PublishDir .\publish\AlbertRetailTerminal
.\Setup\Sign-Release.ps1 -PublishDir .\publish\Installer
```

Recommended Authenticode policy:
- Digest: SHA-256
- Timestamp: RFC3161
- Certificate: organization code-signing cert trusted by branch workstations (internal CA or public CA)
- Avoid EV prompts at counter by deploying the signing CA via Group Policy beforehand
