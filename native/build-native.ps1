# Baut tunnel.dll (embeddable-dll-service aus wireguard-windows) und holt die signierte wireguard.dll (wireguard-nt).
# Ergebnis: native\bin\amd64\tunnel.dll, wireguard.dll und die Lizenztexte.
#
# Quellen und Pruefsummen stammen aus dem build.bat des gepinnten wireguard-windows-Tags,
# damit Go, llvm-mingw und wireguard-nt exakt den offiziellen Versionen entsprechen.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Tag    = 'v1.1.1'
$Commit = 'f0605c3e3dcc5ff801e19158fd6255378b99e17c'
$Repo   = 'https://git.zx2c4.com/wireguard-windows'

$root  = $PSScriptRoot
$build = Join-Path $root '.build'
$src   = Join-Path $build 'wireguard-windows'
$deps  = Join-Path $build 'deps'
$out   = Join-Path $root 'bin\amd64'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------- Quellcode ----------
New-Item -ItemType Directory -Force $build | Out-Null
if (-not (Test-Path (Join-Path $src '.git'))) {
    Step "wireguard-windows $Tag klonen"
    git -c advice.detachedHead=false clone -q --depth 1 --branch $Tag $Repo $src
    if ($LASTEXITCODE -ne 0) { throw 'git clone fehlgeschlagen' }
}
$head = (git -C $src rev-parse HEAD).Trim()
if ($head -ne $Commit) { throw "wireguard-windows steht auf $head statt $Commit ($Tag)." }

# ---------- Abhaengigkeiten (Go, llvm-mingw, wireguard-nt) ----------
$buildBat = Get-Content (Join-Path $src 'build.bat')
function Get-Dependency([string]$file) {
    $line = $buildBat | Where-Object { $_ -match "call :download $([regex]::Escape($file)) (\S+) ([0-9a-f]{64})" } | Select-Object -First 1
    if (-not $line) { throw "Download-Eintrag fuer $file nicht in build.bat gefunden." }
    $null = $line -match "call :download $([regex]::Escape($file)) (\S+) ([0-9a-f]{64})"
    $url, $sha = $Matches[1], $Matches[2]

    $target = Join-Path $deps ([IO.Path]::GetFileNameWithoutExtension($file))
    $marker = Join-Path $target '.sha256'
    if ((Test-Path $marker) -and (Get-Content $marker) -eq $sha) { return $target }

    Step "Lade $file"
    $zip = Join-Path $deps $file
    New-Item -ItemType Directory -Force $deps | Out-Null
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha) { Remove-Item $zip -Force; throw "Pruefsumme von $file stimmt nicht ($actual statt $sha)." }

    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $target
    if ($LASTEXITCODE -ne 0) { throw "Entpacken von $file fehlgeschlagen" }
    Remove-Item $zip -Force
    Set-Content $marker $sha
    return $target
}

function Find-Dir([string]$base, [string]$relative) {
    $hit = Get-ChildItem $base -Directory | ForEach-Object { Join-Path $_.FullName $relative } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $hit -and (Test-Path (Join-Path $base $relative))) { $hit = Join-Path $base $relative }
    if (-not $hit) { throw "$relative nicht in $base gefunden." }
    return $hit
}

$goDir   = Get-Dependency 'go.zip'
$llvmDir = Get-Dependency 'llvm-mingw-ucrt.zip'
$ntDir   = Get-Dependency 'wireguard-nt.zip'

$goRoot  = Split-Path (Find-Dir $goDir 'bin\go.exe') -Parent | Split-Path -Parent
$llvmBin = Split-Path (Find-Dir $llvmDir 'bin\x86_64-w64-mingw32-gcc.exe') -Parent
$ntRoot  = Split-Path (Split-Path (Find-Dir $ntDir 'bin\amd64\wireguard.dll') -Parent) -Parent | Split-Path -Parent

# ---------- tunnel.dll bauen ----------
Step 'tunnel.dll bauen (amd64)'
$env:PATH        = "$goRoot\bin;$llvmBin;$env:PATH"
$env:GOROOT      = $goRoot
$env:GOPATH      = Join-Path $build 'gopath'
$env:GOOS        = 'windows'
$env:GOARCH      = 'amd64'
$env:CGO_ENABLED = '1'
$env:CC          = 'x86_64-w64-mingw32-gcc'
$env:CGO_CFLAGS  = '-O3 -Wall -Wno-unused-function -Wno-switch -std=gnu11 -DWINVER=0x0A00'
$env:GOTOOLCHAIN = 'local'

New-Item -ItemType Directory -Force $out | Out-Null
Push-Location (Join-Path $src 'embeddable-dll-service')
try {
    go build -buildmode c-shared -ldflags="-w -s" -trimpath -o (Join-Path $out 'tunnel.dll')
    if ($LASTEXITCODE -ne 0) { throw 'go build fehlgeschlagen' }
}
finally { Pop-Location }
Remove-Item (Join-Path $out 'tunnel.h') -ErrorAction SilentlyContinue

# ---------- wireguard.dll + Lizenzen ----------
Step 'wireguard.dll und Lizenzen uebernehmen'
Copy-Item (Join-Path $ntRoot 'bin\amd64\wireguard.dll') $out -Force
Copy-Item (Join-Path $ntRoot 'LICENSE.txt') (Join-Path $out 'LICENSE-wireguard-nt.txt') -Force
Copy-Item (Join-Path $ntRoot 'include\wireguard.h') (Join-Path $build 'wireguard.h') -Force
Copy-Item (Join-Path $src 'COPYING') (Join-Path $out 'LICENSE-wireguard-windows.txt') -Force

$sig = Get-AuthenticodeSignature (Join-Path $out 'wireguard.dll')
Write-Host "    wireguard.dll Signatur: $($sig.Status) ($($sig.SignerCertificate.Subject))"
Get-ChildItem $out | ForEach-Object { Write-Host "    $($_.Name)  $([math]::Round($_.Length / 1KB)) KB" }
Write-Host "==> Fertig: $out" -ForegroundColor Green
