flex-screen companion
=====================

The PC side of the flex-screen deck (Waveshare ESP32-S3-Touch-LCD-7): media, volume,
mixer and audio devices, Claude Code status, VS Code terminal and build results, and git.

Install
  1. Unzip this folder anywhere (Downloads is fine; the installer copies what it needs).
  2. Double-click install.cmd.
  3. Plug in the deck. The tray icon's menu says "Deck connected on COMn" once it's found.

What install.cmd does (no admin rights needed; run it again to update):
  - copies the companion to %LOCALAPPDATA%\Programs\flex-screen and adds a Startup and a
    Start menu shortcut, so it runs at every sign-in
  - installs the VS Code extension "flex-screen deck" from flex-screen.vsix
  - adds Claude Code hooks to %USERPROFILE%\.claude\settings.json so Claude sessions (the
    CLI and the Claude extension in VS Code) report to the deck; the original file is kept as
    settings.json.before-flex-screen, and nothing else in it changes
  - starts the companion

After installing
  - Reload any VS Code window that was already open (Ctrl+Shift+P, "Reload Window").
  - Claude sessions started after the install report to the deck; restart running ones.
  - The Dev page lists the last commands run in VS Code's terminal; tap one to run it again.

Google Calendar and Tasks (optional, once)
  The Today and Capture pages read your calendar and the todo skill's mail tasks with your
  own Google OAuth client:
  1. In Google Cloud Console (console.cloud.google.com), APIs & Services: enable the
     Google Calendar API and the Google Tasks API.
  2. OAuth consent screen: user type External, then Publish app (In production), or the
     sign-in lapses every 7 days. The app-domain links can stay empty; they only matter
     if you submit the app for Google's verification, which personal use doesn't need.
     Google warns the app is unverified when you sign in: Advanced > continue.
  3. Credentials > Create credentials > OAuth client ID > Application type "Desktop app".
     Download the JSON.
  4. Save it as %LOCALAPPDATA%\flex-screen\google-client.json
  5. Tray icon > "Connect Google Calendar and Tasks..." and approve in the browser.
  The sign-in is kept encrypted for your Windows account only.
  The deck shows every calendar ticked in Google Calendar. To add another Google account's
  calendar, share it with this account (in the other account: Google Calendar > Settings >
  the calendar > Share with specific people > "See all event details"), and accept. For
  that account's mail, forward it to this one (Gmail > Settings > Forwarding and POP/IMAP).

VS Code Remote-SSH (working on a Linux server from this PC)
  The extension runs on this PC even in a Remote-SSH window, so the deck gets the server's
  terminal commands with nothing more to do: keep it installed here (install.cmd does), and
  if you also installed it on the server, that copy can go. The Dev page's git card says
  "Git runs in the SSH host": git status comes from this PC's git, which can't see the
  server's files.
  Claude Code running on the server needs two things, once:
  1. A tunnel back to this PC. In %USERPROFILE%\.ssh\config, under the server's Host entry:
         RemoteForward 48151 localhost:48151
     then reconnect: Ctrl+Shift+P, "Remote-SSH: Kill VS Code Server on Host...", and open
     the folder again.
  2. The hooks on the server. Copy remote-claude-hooks.sh there and run it once, from this
     folder (use the server's name from your SSH config):
         scp remote-claude-hooks.sh myserver:
         ssh myserver sh remote-claude-hooks.sh
     It needs python3 and curl on the server; "sh remote-claude-hooks.sh --remove" undoes it.
  Restart Claude sessions on the server afterwards.

Calls (Teams, Zoom, Discord)
  When an app uses the microphone, the deck shows a call panel with its controls. Teams
  can do the most (mute, camera, raise hand, leave, reactions) once its API is on: in
  Teams, Settings > Privacy > Manage API > Enable API, then tap any control on the deck
  during a meeting and choose Allow in Teams. Zoom and Discord need nothing.

Settings (optional)
  %LOCALAPPDATA%\flex-screen\settings.json, for instance:
    { "palette": "vibrant",
      "idle": { "clockAfterMinutes": 10, "screenOffAfterMinutes": 30 },
      "layerNames": ["BASE", "NAV", "SYMBOL"],
      "obsidianVault": "C:\\Users\\me\\Notes\\MyVault" }
  palette: the deck's colors, "base", "vibrant", "ember" or "violet" (also in the tray menu,
  Deck colors).
  idle: minutes with no touch and no PC input before the clock screen and before the
  screen goes off (0 turns either off); a locked PC shows the clock right away.
  layerNames: the keyboard's layers from 0 (these are the defaults). obsidianVault: only
  if the vault can't be found by itself (normally it's the one Obsidian has open).

Uninstall
  Double-click uninstall.cmd. It removes the hooks (leaving the rest of settings.json),
  the extension, the shortcuts, the app and its data in %LOCALAPPDATA%\flex-screen
  (clipboard pins, the Google sign-in and your google-client.json).

Troubleshooting
  The log is %LOCALAPPDATA%\Programs\flex-screen\companion.log (tray icon > Open log).
  The companion listens on http://localhost:48151 for the hooks and the extension; if
  something else uses that port, the log says so.
