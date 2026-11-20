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
  - puts flexctl on your user PATH (%LOCALAPPDATA%\Programs\flex-screen\bin), for scripts
    to show a note, ask a question or show their progress on the deck (see flexctl below)
  - starts the companion

After installing
  - Reload any VS Code window that was already open (Ctrl+Shift+P, "Reload Window").
  - Claude sessions started after the install report to the deck; restart running ones.
  - The Dev page lists the last commands run in VS Code's terminal; tap one to run it again.

Updates
  The deck's firmware: this kit carries it. When the deck runs older firmware (it was
  last updated on another PC, say), the tray icon says so and the deck shows "Firmware
  0.21.0 is ready - tap to update". Tap it, or tray icon > "Update the deck's firmware".
  It goes over the same USB cable in about half a minute, with the progress on the deck,
  then the screen goes dark for a few seconds while the deck writes it; keep it plugged in
  meanwhile. If the update stops on the way, nothing changes on the
  deck. If the new firmware can't start, unplug the deck and plug it back in: it goes back
  to the firmware it had.
  The companion: it looks for a newer flex-screen release on GitHub a minute after it
  starts and every 6 hours after (tray icon > "Check for updates" looks now). When there
  is one, the tray icon says so; "Update flex-screen to ..." downloads the release's kit,
  checks it, and installs it as install.cmd does. The companion restarts by itself a
  minute later and then offers the deck its new firmware. Reload VS Code windows for the
  new extension.

Google Calendar and Tasks (optional, once)
  google-setup.md in this folder is the full guide, with screens to look for and
  troubleshooting. In short:
  The Today and Capture pages read your calendar and Google Tasks (ticking off and adding
  tasks too) with your own Google OAuth client:
  1. In Google Cloud Console (console.cloud.google.com), APIs & Services: enable the
     Google Calendar API and the Google Tasks API.
  2. OAuth consent screen: user type External, then Publish app (In production), or the
     sign-in lapses every 7 days. The app-domain links can stay empty; they only matter
     if you submit the app for Google's verification, which personal use doesn't need.
     Google warns the app is unverified when you sign in: Advanced > continue.
  3. Credentials > Create credentials > OAuth client ID > Application type "Desktop app".
     Download the JSON.
  4. Save it as %LOCALAPPDATA%\flex-screen\google-client.json
  5. Tray icon > "Google accounts" > "Google" > "Sign in..." and approve in the browser.
  The sign-in is kept encrypted for your Windows account only.
  The deck shows every calendar ticked in Google Calendar. For another Google account (a
  work one beside your own), tray icon > "Google accounts" > "Add a Google account...": its
  calendar joins the deck's and its task lists the Tasks card. google-setup.md has the
  details (test users, a work domain's own client).
  Tray icon > "Calendars" also adds a Microsoft 365 or Outlook calendar, or a calendar link
  (an .ics address: a published Outlook calendar, a school's or a team's), picks which of
  each account's calendars go on the deck, and hides events (declined, all-day, free, or by
  words in the title). calendars.md has the details. On the deck, tap Up next for the
  whole day.

Other task sources (optional): Microsoft To Do, Jira, GitHub, Obsidian
  task-sources.md in this folder is the guide. Tray icon > "Task sources": Microsoft To Do
  needs an app registration of your own (like Google's); Jira a site and an API token (or
  a personal access token); GitHub a token, or the GitHub CLI's sign-in. Obsidian's latest
  daily note is read on its own from the vault Obsidian has open; with more than one vault
  on this PC, pick it in "Task sources" > "Obsidian daily note". A source not set up is left out.

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
  2. The hooks on the server (and the deck's tools for Claude Code there). Copy
     remote-claude-hooks.sh there and run it once, from this
     folder (use the server's name from your SSH config):
         scp remote-claude-hooks.sh myserver:
         ssh myserver sh remote-claude-hooks.sh
     It needs python3 and curl on the server; "sh remote-claude-hooks.sh --remove" undoes it.
     Run it again after updating from a kit older than 0.16.3: the hooks now also send the
     model of Claude's last reply (read from the session's transcript on the server), for
     the deck's Model key, and run Claude Code's own /usage there after each turn (at most
     every 3 minutes, in the background, through ~/.claude/flex-screen-usage.sh), for the
     Usage key's "5h 42%". And again after updating from a kit older than 0.19.0: it
     registers the deck's tools with Claude Code there (deck_ask, deck_notify,
     deck_progress) and allows them without a prompt. And after updating from one older
     than 0.20.0: it adds the hook behind the session board's Stop.
  Restart Claude sessions on the server afterwards. Since 0.29.0 it also writes
  ~/.local/bin/flexctl there, for scripts on the server (see flexctl below).
  With Claude Code's chat tab in front in VS Code, the deck's bar turns into Claude's. That
  needs Claude in an editor tab (its default, "Open in New Tab"), not in the side bar.

flexctl (scripts talk to the deck)
  From any script, scheduled task or terminal opened after the install:
    flexctl notify "Backup done" -level done -from Backup     a note, until OK is tapped
    flexctl ask "Deploy to production?" Yes No -wait 15       waits, prints the button tapped
    flexctl progress 3 12 "Uploading"                         "Uploading 3/12" in the status line
    flexctl progress -clear
  Levels: info, done, warning, error. Exit codes: 0 done, 1 dismissed on the deck, 2 no
  answer in time, 3 the companion or the deck isn't there, 4 wrong arguments. On a
  Remote-SSH server, ~/.local/bin/flexctl takes the same with --level, --from, --wait.

Calls (Teams, Zoom, Discord)
  When an app uses the microphone, the deck shows a call panel with its controls. Teams
  can do the most (mute, camera, raise hand, leave, reactions) once its API is on: in
  Teams, Settings > Privacy > Manage API > Enable API, then tap any control on the deck
  during a meeting and choose Allow in Teams. Zoom and Discord need nothing.

Shortcuts
  The grid button at the right end of the deck's tab bar opens up to 12 shortcuts. Out
  of the box: Gmail, Calendar, Today's note, Spotify, VS Code and Terminal. To change
  them, tray icon > "Shortcuts...": the profile opens with the list written in. Each
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
  it, or reset the app's keys; "+" adds a key. Hold the app's name to keep the bar on that
  app while others come to the front (amber, with a lock); hold again to let it follow
  again. The companion keeps each app's keys in
  %LOCALAPPDATA%\flex-screen\context-bar.json. To add your own actions to an app's list,
  in the profile:
    "contextBar": {
      "Spotify": { "color": "#1DB954", "keys": [
        { "name": "Like", "keys": "Alt+Shift+B", "glyph": "heart" } ] }
    }
  by exe name ("spotify") or the name the deck shows ("Spotify"), "*" for apps without a
  list of their own; they're the app's keys until you rearrange them on the deck. Keys are "keys", "action" (as for shortcuts) or
  "action": "close". "contextBar": false turns the bar off. The README lists the glyphs.
  In Chrome and Edge, Gmail, YouTube, Google Calendar, Jira and GitHub have keys of their
  own, and so do the profile's own "sites" (the README shows how). An installed web app
  (Install as app) shows under its own name and gets its site's keys.

Browser extension (optional)
  Without it, the bar tells a site by the tab's title. With it, by the tab's address, on
  every page. Once per browser: chrome://extensions (or edge://extensions), turn on
  Developer mode, Load unpacked, and pick %LOCALAPPDATA%\Programs\flex-screen\chrome-ext
  (install.cmd puts it there). It sends only the address and title of the tab in front,
  to this PC's companion, never for an incognito tab.

Profiles and settings (optional)
  Settings live in a profile: %LOCALAPPDATA%\flex-screen\profiles\<name>.json. Tray icon >
  "Profile: ..." switches between profiles (work and home, say), makes a new one, opens
  the profile's file, and can keep the profiles in a synced folder (OneDrive, Dropbox) so
  every PC has the same ones. Sign-ins stay on each PC: a profile names the accounts, and
  each PC signs in to them once. A profile, for instance:
    { "palette": "vibrant",
      "idle": { "clockAfterMinutes": 10, "screenOffAfterMinutes": 30 },
      "layerNames": ["BASE", "NAV", "SYMBOL"] }
  palette: the deck's colors, "base", "vibrant", "ember" or "violet" (also in the tray menu,
  Deck colors).
  idle: minutes with no touch and no PC input before the clock screen and before the
  screen goes off (0 turns either off); a locked PC shows the clock right away.
  layerNames: the keyboard's layers from 0 (these are the defaults).
  This PC's own settings are in %LOCALAPPDATA%\flex-screen\machine.json (tray icon >
  Profile > "Open this PC's settings"):
    { "profile": "work",
      "profilesFolder": "C:\\Users\\me\\OneDrive\\flex-screen",
      "obsidianVault": "C:\\Users\\me\\Notes\\MyVault" }
  obsidianVault: the vault the deck uses (normally the one Obsidian has open); the tray's
  "Task sources" > "Obsidian daily note" sets it. Any profile setting written here wins
  on this PC only ("jira": null turns the profile's Jira off on this PC).
  The settings.json of earlier versions becomes the "default" profile when this version
  first starts; the old file is kept as settings.json.before-profiles.

Uninstall
  Double-click uninstall.cmd. It removes the hooks (leaving the rest of settings.json),
  the extension, the shortcuts, the app and its data in %LOCALAPPDATA%\flex-screen
  (clipboard pins, the profiles kept there, the sign-ins and your google-client.json). A
  profiles folder elsewhere (a synced one) stays.

Troubleshooting
  The log is %LOCALAPPDATA%\Programs\flex-screen\companion.log (tray icon > Open log).
  The companion listens on http://localhost:48151 for the hooks and the extension; if
  something else uses that port, the log says so.
