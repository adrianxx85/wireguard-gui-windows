# Baut WG Client und den Installer.
# Ergebnis: publish\WGClient.exe und dist\WGClient-Setup-<Version>.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "==> App bauen (publish)" -ForegroundColor Cyan
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }
dotnet publish "$root\WGClient\WGClient.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 nicht gefunden. Installieren mit: winget install JRSoftware.InnoSetup" }

Write-Host "==> Installer bauen" -ForegroundColor Cyan
& $iscc /Q "$root\installer\WGClient.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup fehlgeschlagen" }

Get-ChildItem "$root\dist\*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Host "==> Fertig: $($_.FullName)" -ForegroundColor Green }
