# Builds the install kit for another PC: dist\flex-screen\ and dist\flex-screen.zip.
#
#   powershell -ExecutionPolicy Bypass -File install\build-dist.ps1
#
# Copy the zip to the other PC, unzip it and run install.cmd there. The kit holds a
# self-contained companion (no .NET needed on that PC), the VS Code extension and the
# install/uninstall scripts. The deck itself needs nothing: its firmware doesn't care which
# PC it's plugged into.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'flex-screen'
$zip = Join-Path $dist 'flex-screen.zip'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Step 'Companion: self-contained build'
dotnet publish (Join-Path $root 'companion') -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $out 'companion')
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

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

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`nKit ready: $zip ($mb MB)" -ForegroundColor Green
