# Installs the flex-screen companion on this PC:
#   - the tray app, in %LOCALAPPDATA%\Programs\flex-screen, started at every sign-in
#   - the VS Code extension (terminal commands, builds and git for the deck's Dev page)
#   - Claude Code hooks in ~/.claude/settings.json, for the deck's Claude status (the Claude
#     extension in VS Code reads the same file)
#
# Run install.cmd, or: powershell -ExecutionPolicy Bypass -File install.ps1
# Running it again updates everything in place. No admin rights needed.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$dir = Join-Path $env:LOCALAPPDATA 'Programs\flex-screen'
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

# A kit copied from another PC or downloaded is marked as from the internet; Windows would
# then warn about every file in it.
Get-ChildItem $here -Recurse -File | Unblock-File

Step 'Companion app'
$running = Get-Process flex-screen-companion -ErrorAction SilentlyContinue
if ($running) {
  $running | Stop-Process -Force
  Start-Sleep -Milliseconds 800  # until Windows lets go of the exe
}
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item (Join-Path $here 'companion\*') $dir -Recurse -Force
$shell = New-Object -ComObject WScript.Shell
foreach ($path in $links) {
  $link = $shell.CreateShortcut($path)
  $link.TargetPath = $exe
  $link.WorkingDirectory = $dir
  $link.Description = 'flex-screen deck companion'
  $link.Save()
}
Write-Host "Installed in $dir; starts at sign-in (and from the Start menu)."

Step 'VS Code extension'
$code = Find-Code
if ($code) {
  & $code --install-extension (Join-Path $here 'flex-screen.vsix') --force
  if ($LASTEXITCODE) { Write-Warning 'The VS Code extension did not install.' }
  else { Write-Host 'Open VS Code windows pick it up after a reload (Ctrl+Shift+P, "Reload Window").' }
} else {
  Write-Warning "VS Code not found. Once it's installed, run: code --install-extension `"$here\flex-screen.vsix`""
}

Step 'Claude Code hooks'
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
  Write-Warning 'curl.exe is missing (Windows 10 1803 and later include it); the hooks call it.'
}
$hooks = Start-Process $exe -ArgumentList '--install-hooks' -Wait -PassThru
if ($hooks.ExitCode -eq 0) {
  Write-Host "Added to $HOME\.claude\settings.json (your original is kept as settings.json.before-flex-screen)."
  Write-Host 'Claude sessions started from now on report to the deck.'
} else {
  Write-Warning "Couldn't update $HOME\.claude\settings.json; see $dir\companion.log"
}

Step 'Starting the companion'
Start-Process $exe -WorkingDirectory $dir
Write-Host "`nDone. The companion is in the tray; plug in the deck." -ForegroundColor Green
