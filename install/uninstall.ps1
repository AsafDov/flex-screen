# Removes what install.ps1 added: the Claude Code hooks (everything else in
# ~/.claude/settings.json stays), the VS Code extension, the startup and Start menu
# shortcuts, the companion app, and its data (clipboard pins, the Google sign-in).
#
# Run uninstall.cmd, or: powershell -ExecutionPolicy Bypass -File uninstall.ps1
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\flex-screen'
$data = Join-Path $env:LOCALAPPDATA 'flex-screen'
$exe = Join-Path $dir 'flex-screen-companion.exe'
$links = @(
  (Join-Path ([Environment]::GetFolderPath('Startup')) 'flex-screen companion.lnk'),
  (Join-Path ([Environment]::GetFolderPath('Programs')) 'flex-screen companion.lnk')
)

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Find-Code {
  $onPath = Get-Command code.cmd -ErrorAction SilentlyContinue
  if ($onPath) { return $onPath.Source }
  $candidates = @(
    "$env:LOCALAPPDATA\Programs\Microsoft VS Code\bin\code.cmd",
    "$env:ProgramFiles\Microsoft VS Code\bin\code.cmd"
  )
  return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

Step 'Claude Code hooks'
if (Test-Path $exe) {
  $hooks = Start-Process $exe -ArgumentList '--uninstall-hooks' -Wait -PassThru
  if ($hooks.ExitCode -eq 0) { Write-Host "Removed from $HOME\.claude\settings.json." }
  else { Write-Warning "Couldn't update $HOME\.claude\settings.json; see $dir\companion.log" }
} else {
  Write-Host 'The companion is not installed; nothing to remove.'
}

Step 'VS Code extension'
$code = Find-Code
if ($code) {
  & $code --uninstall-extension flex-screen.flex-screen
} else {
  Write-Host 'VS Code not found; skipped.'
}

Step 'Companion app'
$running = Get-Process flex-screen-companion -ErrorAction SilentlyContinue
if ($running) {
  $running | Stop-Process -Force
  Start-Sleep -Milliseconds 800
}
foreach ($path in $links) {
  if (Test-Path $path) { Remove-Item $path -Force }
}
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
Write-Host "`nflex-screen is uninstalled." -ForegroundColor Green
