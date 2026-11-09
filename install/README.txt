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
    CLI, the Claude app and the Claude extension in VS Code) report to the deck, and ask the
    deck too when they need a permission (Claude's own prompt still shows; whichever you
    answer first wins; "Approve Claude from the deck" in the tray menu turns it off); the
    original file is kept as settings.json.before-flex-screen, and nothing else in it
    changes
  - starts the companion

After installing
  - Reload any VS Code window that was already open (Ctrl+Shift+P, "Reload Window").
  - Claude sessions started after the install report to the deck; restart running ones.
  - The Dev page lists the last commands run in VS Code's terminal; tap one to run it again.

Google Calendar and Tasks (optional, once)
  google-setup.md in this folder is the full guide, with screens to look for, how mail
  items reach the deck, and troubleshooting. In short:
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

Shortcuts
  The grid button at the right end of the deck's tab bar opens up to 12 shortcuts. Out
  of the box: Gmail, Calendar, Today's note, Spotify, VS Code and Terminal. To change
  them, tray icon > "Shortcuts...": settings.json opens with the list written in. Each
  is one of:
    { "name": "Gmail", "url": "https://mail.google.com" }      a link
    { "name": "Spotify", "app": "Spotify" }                    a Start menu app, by its name
                                                               there (brought to the front
                                                               if it's open)
    { "name": "Projects", "path": "C:\\Projects" }              a file, folder or program
                                                               (optional "args")
    { "name": "Today's note", "action": "obs.today" }          a deck action
    { "name": "Snip", "keys": "Win+Shift+S" }                  keys, into the window in front
  Optional on any: "glyph" (the symbol without an icon: mail, calendar, note, terminal,
  code, music, lock, desktop, folder, keys, git, call) and "icon" (a picture or program
  whose icon to use). Actions: obs.today, mail.open, pc.lock, desktop.show,
  media.toggle/next/prev, vol.mute, mic.mute, term.focus, git.commit/fetch/pull/push/stash,
  call.mic/camera/hand/leave/focus. Save the file and the deck has them within seconds.

Context bar
  The strip above the deck's tabs has keys for the app in front, in its color: VS Code,
  Chrome and Edge, Windows Terminal, Obsidian, Teams (another set during a meeting) and
  Discord have their own; any other app gets Copy, Paste, Undo, Save, Find and Close. A key
  presses its shortcut in that app's window. Long-press a key to pick another action for
  its place from that app's list (one already on the bar swaps places with it), remove
  it, or reset the app's keys; "+" adds a key. The companion keeps each app's keys in
  %LOCALAPPDATA%\flex-screen\context-bar.json. To add your own actions to an app's list,
  in settings.json:
    "contextBar": {
      "Spotify": { "color": "#1DB954", "keys": [
        { "name": "Like", "keys": "Alt+Shift+B", "glyph": "heart" } ] }
    }
  by exe name ("spotify") or the name the deck shows ("Spotify"), "*" for apps without a
  list of their own; they're the app's keys until you rearrange them on the deck. Keys are "keys", "action" (as for shortcuts) or
  "action": "close". "contextBar": false turns the bar off. The README lists the glyphs.

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
