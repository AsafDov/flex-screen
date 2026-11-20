# Installs the flex-screen companion on this PC:
#   - the tray app, in %LOCALAPPDATA%\Programs\flex-screen, started at every sign-in
#   - the VS Code extension (terminal commands, builds and git for the deck's Dev page)
#   - Claude Code hooks in ~/.claude/settings.json, for the deck's Claude status (the Claude
#     extension in VS Code reads the same file)
#   - flexctl, for scripts to put a note, a question or a progress bar on the deck, on the
#     PATH (the companion's bin folder, added to your user PATH)
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

Step 'Browser extension'
# Chrome and Edge load an extension that isn't from their store from a folder, which has to
# stay: next to the companion, updated with it.
$ext = Join-Path $dir 'chrome-ext'
New-Item -ItemType Directory -Force $ext | Out-Null
Copy-Item (Join-Path $here 'chrome-ext\*') $ext -Recurse -Force
Write-Host "Optional, once per browser: so the deck's bar knows the site in Chrome or Edge by its address,"
Write-Host "open chrome://extensions (edge://extensions), turn on Developer mode, Load unpacked, and pick"
Write-Host "  $ext"
Write-Host 'Updates come with the companion; the browser picks them up when it restarts.'

Step 'flexctl for scripts'
# flexctl.cmd alone in bin, so PowerShell runs it rather than finding flexctl.ps1 (which
# running scripts being off would stop) on the PATH.
$bin = Join-Path $dir 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $here 'flexctl.ps1') $dir -Force
Copy-Item (Join-Path $here 'flexctl.cmd') $bin -Force
$binOnPath = '%LOCALAPPDATA%\Programs\flex-screen\bin'
$envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
$userPath = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$entries = @($userPath -split ';' | Where-Object { $_ })
if (-not ($entries | Where-Object { $_.TrimEnd('\') -ieq $binOnPath -or [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') -ieq $bin })) {
  $envKey.SetValue('Path', (($entries + $binOnPath) -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
  # Tells Explorer, so terminals opened from now on have it.
  [Environment]::SetEnvironmentVariable('FLEX_SCREEN_PATH_CHANGED', '1', 'User')
  [Environment]::SetEnvironmentVariable('FLEX_SCREEN_PATH_CHANGED', $null, 'User')
  Write-Host "Added $bin to your PATH; terminals opened from now on find flexctl."
} else {
  Write-Host 'flexctl is on your PATH.'
}
$envKey.Close()
Write-Host '  flexctl notify "Backup done"   |   flexctl ask "Deploy?" Yes No   |   flexctl progress 3 12 Uploading'

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
