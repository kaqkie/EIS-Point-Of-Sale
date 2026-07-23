@echo off
REM Convenience wrapper — bare "dotnet run" from the repo root also works.
dotnet run --project "%~dp0AlbertRetailTerminal.Host.csproj" %*
