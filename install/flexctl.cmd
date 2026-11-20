@echo off
rem flexctl: a note, a question or a progress bar on the flex-screen deck, from any script.
rem See flexctl.ps1 beside the companion (one folder up) for how to use it.
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0..\flexctl.ps1" %*
exit /b %ERRORLEVEL%
