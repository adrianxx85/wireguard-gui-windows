# Baut WG Client und den Installer.
#   build.ps1              -> dist\WGClient-Setup-<Version>.exe            (nutzt die installierte WireGuard-App)
#   build.ps1 -Standalone  -> dist\WGClient-Standalone-Setup-<Version>.exe (bringt tunnel.dll/wireguard.dll mit)
# Ergebnis der App: publish\WGClient.exe (fuer beide Editionen identisch)
param([switch]$Standalone)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "==> App bauen (publish)" -ForegroundColor Cyan
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }
dotnet publish "$root\WGClient\WGClient.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen" }

$isccArgs = @('/Q')
if ($Standalone) {
    $native = "$root\native\bin\amd64"
    & "$root\native\build-native.ps1"
    $manifest = Get-Content -LiteralPath (Join-Path $native 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in @('tunnel.dll', 'wireguard.dll', 'LICENSE-wireguard-nt.txt', 'LICENSE-wireguard-windows.txt')) {
        if ((Get-FileHash -LiteralPath (Join-Path $native $file)).Hash -ne $manifest.Files.$file) {
            throw "Native Datei nach dem Build verändert: $file."
        }
    }
    $isccArgs += '/DStandalone'
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 nicht gefunden. Installieren mit: winget install JRSoftware.InnoSetup" }

Write-Host "==> Installer bauen" -ForegroundColor Cyan
& $iscc @isccArgs "$root\installer\WGClient.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup fehlgeschlagen" }

Get-ChildItem "$root\dist\*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Host "==> Fertig: $($_.FullName)" -ForegroundColor Green }
