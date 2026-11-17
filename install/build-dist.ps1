# Builds the install kit for another PC: dist\flex-screen\ and dist\flex-screen.zip.
#
#   powershell -ExecutionPolicy Bypass -File install\build-dist.ps1 [-Version 0.21.1]
#
# Copy the zip to the other PC, unzip it and run install.cmd there, or attach it to the
# GitHub release, where installed companions find it. The kit holds a self-contained
# companion (no .NET needed on that PC), the deck's firmware (the companion offers to put
# it on the deck), the VS Code extension and the install/uninstall scripts.
#
# -Version is the release's: the companion's update check compares it with the newest
# GitHub release, so it must match the release's tag (v0.21.1). It defaults to the
# firmware's version (firmware\src\pc.h); give it for a release that only changes the PC
# side (0.16.1 had firmware 0.16.0). Building the firmware needs PlatformIO.
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'flex-screen'
$zip = Join-Path $dist 'flex-screen.zip'
$firmware = Join-Path $root 'firmware'

$firmwareVersion = [regex]::Match((Get-Content (Join-Path $firmware 'src\pc.h') -Raw), 'kFirmwareVersion = "([^"]+)"').Groups[1].Value
if (-not $firmwareVersion) { throw "Couldn't read the firmware's version from firmware\src\pc.h" }
if (-not $Version) { $Version = $firmwareVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be x.y.z (no v), not $Version" }

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Step "Companion $Version, self-contained"
dotnet publish (Join-Path $root 'companion') -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none "-p:Version=$Version" -o (Join-Path $out 'companion')
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# Next to the companion's exe (install.ps1 copies it along), with what the companion checks
# before it sends the image to the deck: its version, size and SHA-256.
Step "Deck firmware $firmwareVersion"
$pio = Get-Command pio -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $pio) { $pio = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe' }
if (-not (Test-Path $pio)) { throw 'PlatformIO not found; the kit needs it to build the deck firmware' }
& $pio run -d $firmware -e deck
if ($LASTEXITCODE) { throw 'pio run failed' }
$bin = Join-Path $firmware '.pio\build\deck\firmware.bin'
$fw = Join-Path $out 'companion\firmware'
New-Item -ItemType Directory -Force $fw | Out-Null
Copy-Item $bin (Join-Path $fw 'deck.bin')
$manifest = [ordered]@{
  version = $firmwareVersion
  file = 'deck.bin'
  size = (Get-Item $bin).Length
  sha256 = (Get-FileHash $bin -Algorithm SHA256).Hash.ToLower()
}
[IO.File]::WriteAllText((Join-Path $fw 'firmware.json'), ($manifest | ConvertTo-Json))

Step 'VS Code extension'
$ext = Join-Path $root 'vscode-ext'
Push-Location $ext
try {
  if (-not (Test-Path node_modules)) {
    npm ci
    if ($LASTEXITCODE) { throw 'npm ci failed' }
  }
  npm run compile
  if ($LASTEXITCODE) { throw 'tsc failed' }
  npm run package
  if ($LASTEXITCODE) { throw 'vsce package failed' }
} finally {
  Pop-Location
}
Copy-Item (Join-Path $ext 'flex-screen.vsix') $out

Step 'Scripts'
foreach ($name in 'install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'README.txt', 'remote-claude-hooks.sh') {
  Copy-Item (Join-Path $PSScriptRoot $name) $out
}
Copy-Item (Join-Path $root 'docs\google-setup.md') $out

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`nKit ready: $zip ($mb MB), flex-screen $Version with deck firmware $firmwareVersion" -ForegroundColor Green
