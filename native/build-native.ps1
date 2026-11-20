# Baut die AMD64-DLLs aus gepinnten Quellen und verifizierten Eingaben.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Tag = 'v1.1.1'
$Commit = 'f0605c3e3dcc5ff801e19158fd6255378b99e17c'
$Repo = 'https://git.zx2c4.com/wireguard-windows'
$cache = Join-Path $PSScriptRoot '.build'
$checkout = Join-Path $cache 'wireguard-windows'
$archives = Join-Path $cache 'archives'
$work = Join-Path $cache ('native-' + [guid]::NewGuid().ToString('N'))
$src = Join-Path $work 'src'
$out = Join-Path $PSScriptRoot 'bin\amd64'
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'

function Expand-VerifiedDependency([string]$file) {
    $pattern = "call :download $([regex]::Escape($file)) (\S+) ([0-9a-f]{64})"
    $line = $buildBat | Where-Object { $_ -match $pattern } | Select-Object -First 1
    if (-not $line) { throw "Download-Eintrag fuer $file fehlt im gepinnten build.bat." }
    $null = $line -match $pattern
    $url, $sha = $Matches[1], $Matches[2]

    $zip = Join-Path $archives $file
    $download = Join-Path $work $file
    if ((Test-Path -LiteralPath $zip) -and (Get-FileHash -LiteralPath $zip).Hash -eq $sha) {
        Copy-Item -LiteralPath $zip -Destination $download
    } else {
        Write-Host "==> Lade $file"
        Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $download).Hash -ne $sha) { throw "Falsche SHA-256-Pruefsumme fuer $file." }
    if (!(Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip).Hash -ne $sha) {
        Copy-Item -LiteralPath $download -Destination $zip -Force
    }

    $target = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($file))
    New-Item -ItemType Directory -Path $target | Out-Null
    & $tar -xf $download -C $target
    if ($LASTEXITCODE -ne 0) { throw "Entpacken von $file fehlgeschlagen." }
    return $target
}

function Find-DependencyFile([string]$base, [string]$relative) {
    $hit = Get-ChildItem -LiteralPath $base -Directory | ForEach-Object { Join-Path $_.FullName $relative } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($hit) { return $hit }
    $direct = Join-Path $base $relative
    if (Test-Path -LiteralPath $direct -PathType Leaf) { return $direct }
    throw "$relative wurde in $base nicht gefunden."
}

$previousEnv = @{}
foreach ($name in @('PATH','GOROOT','GOPATH','GOCACHE','GOMODCACHE','GOOS','GOARCH','CGO_ENABLED','CC','CGO_CFLAGS','GOTOOLCHAIN','GOENV','GOWORK','GOFLAGS','GOSUMDB','GOPRIVATE','GONOSUMDB','GONOPROXY')) {
    $previousEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    New-Item -ItemType Directory -Force -Path $src, $archives | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $checkout '.git'))) {
        git -c advice.detachedHead=false clone -q --depth 1 --branch $Tag $Repo $checkout
        if ($LASTEXITCODE -ne 0) { throw 'git clone fehlgeschlagen.' }
    }
    $sourceZip = Join-Path $work 'source.zip'
    git -C $checkout archive --format=zip "--output=$sourceZip" $Commit
    if ($LASTEXITCODE -ne 0) { throw "Gepinnter WireGuard-Commit $Commit fehlt im Cache." }
    & $tar -xf $sourceZip -C $src
    if ($LASTEXITCODE -ne 0) { throw 'Entpacken der gepinnten Quellen fehlgeschlagen.' }
    $buildBat = Get-Content -LiteralPath (Join-Path $src 'build.bat')

    $go = Find-DependencyFile (Expand-VerifiedDependency 'go.zip') 'bin\go.exe'
    $cc = Find-DependencyFile (Expand-VerifiedDependency 'llvm-mingw-ucrt.zip') 'bin\x86_64-w64-mingw32-gcc.exe'
    $driver = Find-DependencyFile (Expand-VerifiedDependency 'wireguard-nt.zip') 'bin\amd64\wireguard.dll'
    $driverRoot = Split-Path (Split-Path (Split-Path $driver -Parent) -Parent) -Parent
    $signature = Get-AuthenticodeSignature -LiteralPath $driver
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -ne 'WireGuard LLC') {
        throw "Die WireGuardNT-Signatur ist ungueltig: $($signature.Status)."
    }

    $env:PATH = "$(Split-Path $go -Parent);$(Split-Path $cc -Parent);$env:PATH"
    $env:GOROOT = Split-Path (Split-Path $go -Parent) -Parent
    $env:GOPATH = Join-Path $work 'gopath'
    $env:GOCACHE = Join-Path $work 'gocache'
    $env:GOMODCACHE = Join-Path $work 'gomodcache'
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:CGO_ENABLED = '1'
    # Nur den Namen setzen: cgo trennt CC an Leerzeichen; ueber den vorangestellten PATH-Eintrag wird der gepinnte gcc gefunden.
    $env:CC = Split-Path $cc -Leaf
    $env:CGO_CFLAGS = '-O3 -Wall -Wno-unused-function -Wno-switch -std=gnu11 -DWINVER=0x0A00'
    $env:GOTOOLCHAIN = 'local'
    $env:GOENV = 'off'
    $env:GOWORK = 'off'
    $env:GOFLAGS = '-mod=readonly'
    $env:GOSUMDB = 'sum.golang.org'
    $env:GOPRIVATE = ''
    $env:GONOSUMDB = ''
    $env:GONOPROXY = ''

    $built = Join-Path $work 'artifacts'
    New-Item -ItemType Directory -Path $built | Out-Null
    Write-Host '==> tunnel.dll aus gepinnten Quellen bauen (amd64)'
    Push-Location (Join-Path $src 'embeddable-dll-service')
    try {
        & $go build -buildmode c-shared -ldflags="-w -s" -trimpath -o (Join-Path $built 'tunnel.dll')
        if ($LASTEXITCODE -ne 0) { throw 'go build fehlgeschlagen.' }
    }
    finally { Pop-Location }

    Copy-Item -LiteralPath $driver -Destination $built
    Copy-Item -LiteralPath (Join-Path $driverRoot 'LICENSE.txt') -Destination (Join-Path $built 'LICENSE-wireguard-nt.txt')
    Copy-Item -LiteralPath (Join-Path $src 'COPYING') -Destination (Join-Path $built 'LICENSE-wireguard-windows.txt')
    $files = @('tunnel.dll', 'wireguard.dll', 'LICENSE-wireguard-nt.txt', 'LICENSE-wireguard-windows.txt')
    $hashes = @{}
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($file in $files) {
        $artifact = Join-Path $built $file
        $hashes[$file] = (Get-FileHash -LiteralPath $artifact).Hash
        Copy-Item -LiteralPath $artifact -Destination (Join-Path $out $file) -Force
    }
    @{ SourceCommit = $Commit; Files = $hashes } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest.json')
    Write-Host "==> Gepruefte native Dateien: $out" -ForegroundColor Green
}
finally {
    foreach ($name in $previousEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnv[$name], 'Process') }
    if (Test-Path -LiteralPath $work) {
        $resolved = (Resolve-Path -LiteralPath $work).Path
        if ((Split-Path -Parent $resolved) -eq (Resolve-Path -LiteralPath $cache).Path -and
            (Split-Path -Leaf $resolved) -match '^native-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
