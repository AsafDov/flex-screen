# flex-screen

A 7" touch deck that sits next to the keyboard and runs the parts of the workday that live
outside the editor: audio, meetings, Claude Code, builds, git, windows, clipboard, Obsidian
and a numpad.

It's a Waveshare **ESP32-S3-Touch-LCD-7** (800×480, capacitive touch) on one USB-C cable,
talking to a small Windows tray app. Six swipe pages sit under one status bar.

```
ESP32-S3 deck ──USB (serial + keyboard)──► Companion (Windows tray app, C#)
                                              ▲            ▲
                         VS Code extension ───┘            └─── Claude Code hooks
```

- **Firmware** (`firmware/`, PlatformIO, Arduino-ESP32 + LVGL 9) draws the UI. The deck is
  also a real USB keyboard and media-key device, so the numpad and play/pause work even
  without the companion.
- **Companion** (`companion/`, .NET 10) does everything that needs Windows, Google or the
  filesystem, pushes state to the deck and runs its actions.
- **VS Code extension** (`vscode-ext/`, TypeScript) reports terminal commands, tasks and the
  active workspace.
- **Claude Code hooks** tell the companion when a Claude session is working, needs you or
  is done.

## Features

### Status bar (on every page)
- Clock and date.
- Next meeting with a countdown, amber from five minutes out.
- Claude Code status (working, needs you, done); tap it to bring that session's window
  forward, whether it's the Claude app, VS Code or a terminal.
- Last build result.
- Keyboard layer (from a Vial keyboard); tap it to pin the layer cheat sheet open.
- Volume (tap to mute) and microphone mute, which mutes every mic so Teams, Zoom and
  Discord all go quiet at once.
- Toasts over any page: a long command finished or failed, Claude needs approval, git
  results.

### Claude approvals (on every page)
- When Claude Code asks for permission (in the Claude app, VS Code or a terminal), a banner
  under the status bar shows what it wants to run, with **Approve**, **Always** (Claude's
  own "don't ask again" rule, when it offers one) and **Deny**. Claude's prompt on the PC
  shows as usual; whichever you answer first wins.
- Turn it off from the tray icon: **Approve Claude from the deck**.

### Context bar (on every page but the Numpad)
- A strip above the tabs with keys for the app in front on the PC, in that app's color. The
  keys each app starts with (a long press shows the rest of its list):
  - **VS Code:** Go to file, Go to symbol, Terminal, Search, Approve, Model. Its list also
    has Build, Debug and stepping, Definition, Rename, Format, Comment, Quick fix, Source
    control, and Claude's own: open Claude, New Claude chat, Accept or Reject changes,
    Mention file.
  - **Claude app:** Approve, Deny, Model, Effort, Usage, New chat.
  - **Chrome and Edge:** Back, Reload, Prev tab, Next tab, New tab, Reopen, Find. On
    **Gmail**, **YouTube** and **Google Calendar** the bar has that site's keys (Compose,
    Reply, Archive…; Play, Back 10 s, Captions…; Today, Week, New event…). Gmail's need its
    keyboard shortcuts turned on in Gmail's settings.
  - **Windows Terminal:** Prev cmd, New tab, Prev tab, Next tab, Split right, Split down;
    its list also has Clear, History, keys that type `git status`, `git pull` or `claude`,
    and Claude's keys.
  - **Explorer:** Up, New folder, Rename, Copy path, **Terminal here**, **VS Code here** (and
    Claude here, Copy folder, Preview pane, views…).
  - **Obsidian:** Daily note, Search, Quick open, Commands, Back, Graph.
  - **Spotify:** Previous, Play/pause, Next, Like, Shuffle, Search.
  - **Teams:** Activity, Chat, Calendar, New chat, Search; during a meeting Mute, Camera,
    Raise hand, Like, Share, Leave (and the other reactions).
  - **Discord:** Switch, Prev channel, Next channel, Unread, Mute, Deafen.
  - **Any other app:** Copy, Paste, Undo, Save, Find, Close, in the color of its icon.
- A key presses its shortcut in that app's window. If the desktop or a menu took the front
  meanwhile, the window comes back first. In VS Code, keys run VS Code's own commands
  through the flex-screen extension, so your own keybindings don't matter.
- **Claude's keys** work wherever Claude Code runs, the app, VS Code or a terminal:
  **Approve** and **Deny** answer the newest request; **Model** and **Effort** show the
  session's and open a list to change them (typed as `/model` or `/effort` into its
  prompt); **Usage** opens your usage page. Claude Code gives the 5-hour figure only to
  terminal status lines, so the key doesn't show it.
- **Long-press a key** to pick another action for its place from that app's list (VS Code
  also has Go to symbol, Go to line, Run, Rename, Format, Comment, Source control…; every
  app gets an "Everywhere" list: Snap left, Screenshot, Lock PC…). One already on the bar
  swaps places with it, which is how you reorder. **Remove** takes the key off, **Reset**
  puts the app's defaults back, and **+** at the end adds a key. Each app's arrangement is
  kept by the companion on that PC.
- Add your own actions to an app's list in settings.json's `contextBar` (see [Settings](#settings)).

### Shortcuts (on every page)
- A launcher button at the right end of the tab bar opens a panel of up to 12 tiles:
  apps, links, deck actions and key combinations, each with its real icon (an app's own,
  a site's) or a symbol. Tap one to run it; tap anywhere else to close.
- An app that's already open comes to the front instead of starting again. Apps are
  found by their Start menu name, Store apps (Spotify, Terminal) included.
- Out of the box: Gmail, Calendar, Today's note, Spotify, VS Code and Terminal. Change
  them per PC from the tray icon's **Shortcuts…** (see [Settings](#settings)).

### Today
- Big clock.
- Now playing: album art, title, artist and app, play/pause, next/previous, volume.
- Up next: the next calendar event with a **Join** button for Meet, Zoom or Teams links.
- Recent: the last five things you did from the deck, as tiles: a window you brought up
  (with its app icon), a note opened, a command run, a meeting joined, Gmail, a Claude
  session, a sound output picked. Tap one to do it again; a window that has closed since
  brings up another window of its app, or starts the app. Most recent on the left; doing
  something already listed moves it there instead of adding it twice.
- How many action items are waiting from mail.

### Audio
- Now playing with large art, progress, shuffle and repeat.
- Output and input device pickers that set the Windows default device.
- Per-app mixer: a fader and mute button for each app playing sound.

### Dev
- Claude banner: what the current session is doing, with an **Open** button.
- Git for VS Code's active repo: branch, ahead/behind, changed files; fetch, pull, push,
  stash, and **Commit…** to open VS Code's Source Control.
- Build card: the last 10 build runs and their results.
- Terminal: the last few commands run in VS Code's terminal, each in full (a long one
  wraps), with ✓/✕ and the time it started. Tap one to run it again in the terminal it
  came from. History is kept per workspace, so it survives restarts.

### Windows
- The 8 most recently used windows with their icons, in taskbar order. Tap to focus (tap
  again to minimize); a minimized window keeps its tile, icon dimmed, and a tap brings it
  back. Hold to close the window, which takes its tile away at once.
- Show desktop and Lock PC.
- FancyZones: switch each monitor's layout (Priority grid, Columns, Rows, Grid or your
  custom layouts), tap a zone on the map to snap the active window into it, tap it again
  to maximize. A monitor picker for multi-monitor setups.

### Capture
- Action items from mail: the Google Tasks your `todo` skill makes from email, with sender
  and due day; tick them off from the deck. **Open Gmail** button.
- Clipboard history: your newest text copies plus pins. Tap to paste into the focused
  window, hold to pin, Clear. Copies that password managers mark as private are never
  recorded, and the history is stored encrypted for your Windows account.
- Obsidian: open today's daily note, or one of the five most recent notes.

### Numpad
- A full USB keypad, with keys that repeat while held, editing keys, and `( ) , =`.
  Num Lock follows the PC's.
- Calculator mode: work it out on the deck, and Enter types the result into the PC.

### Calls
- When an app is using the microphone (Teams, Zoom, Discord, Webex, a browser), a call
  panel pops up with what that app allows: mute, camera, raise hand, reactions, leave,
  volume, and bring the call forward. Hide turns it into a status-bar pill.
- Teams through its local API (real mute, camera and hand state); Zoom through its
  keyboard shortcuts; Discord's deafen through the mixer.

### Keyboard layer cheat sheet
- Hold a layer key for ten seconds and the deck shows that layer laid out like your
  keyboard, read straight from the Vial keyboard's own layout and keymap. Letting go
  hides it; tapping the layer pill keeps it open.

### The deck itself
- Idle: a dim clock after 10 minutes without input on the deck or the PC, screen off
  after 30 (both adjustable). A locked PC shows the clock straight away.
- Four color themes, Base, Vibrant, Ember (warm, orange) and Violet, from the tray menu.
- Firmware updates over the same USB cable (`pio run -t upload`).
- One deck, several PCs: it shows whichever PC it's plugged into. Each PC's companion
  keeps its own settings, Google sign-in, vault and clipboard.

## Setup

### Firmware
Needs PlatformIO (run it from PowerShell, not Git Bash). From the repo root:

```powershell
pio run -d firmware -t upload
```

This updates a deck that already runs flex-screen over its own USB link. For the very
first flash, use the UART port and the ROM bootloader (hold BOOT, tap RESET):

```powershell
pio run -d firmware -e deck-esptool -t upload
```

`backup/` holds the board's factory firmware and how to restore it.

### Companion, extension and hooks
To build an install kit for any Windows PC (no admin rights or .NET needed there):

```powershell
powershell -ExecutionPolicy Bypass -File install\build-dist.ps1
```

Unzip `dist\flex-screen.zip` on the PC and run `install.cmd`. It installs the companion
(started at sign-in), the VS Code extension and the Claude Code hooks; `uninstall.cmd`
removes them. [install/README.txt](install/README.txt) covers the optional parts: Google
Calendar and Tasks, Teams calls and per-PC settings.

### Google Calendar, Tasks and mail
[docs/google-setup.md](docs/google-setup.md) walks through it: your own Google sign-in app
(once), connecting each PC from the tray, how mail action items reach the deck, more than one
Google account, and troubleshooting.

### VS Code Remote-SSH
Working on a Linux server from the deck's PC: the extension runs on the PC even in a
Remote-SSH window (keep it installed there), so terminal commands reach the deck as
they are. For Claude Code running on the server, add `RemoteForward 48151 localhost:48151`
under the server's Host in the PC's `~/.ssh/config`, reconnect, and run
`install/remote-claude-hooks.sh` on the server once. Details in
[install/README.txt](install/README.txt).

### Simulator
The deck's UI also runs on the PC, in a window or headless, driven over a local port with
made-up data: `simulator\build.ps1` builds it (needs Visual Studio's C++ build tools), and
`simulator\test_deck.py` taps through every feature. See [simulator/README.md](simulator/README.md).

### Settings
Optional, per PC, in `%LOCALAPPDATA%\flex-screen\settings.json`:

```jsonc
{
  "palette": "base",                                   // or "vibrant", "ember", "violet"
  "idle": { "clockAfterMinutes": 10, "screenOffAfterMinutes": 30 },  // 0 turns either off
  "layerNames": ["BASE", "NAV", "SYMBOL"],             // your keyboard's layers
  "obsidianVault": "C:\\path\\to\\vault",               // if it isn't found by itself
  "shortcuts": [                                       // the launcher's tiles, up to 12
    { "name": "Gmail", "url": "https://mail.google.com" },
    { "name": "Spotify", "app": "Spotify" },             // a Start menu app, by its name there
    { "name": "Projects", "path": "C:\\Projects" },      // a file, folder or program ("args" too)
    { "name": "Today's note", "action": "obs.today" },   // something the deck does
    { "name": "Snip", "keys": "Win+Shift+S" }            // keys pressed in the window in front
  ]
}
```

A shortcut can also have `"glyph"` (the symbol shown without an icon: `mail`, `calendar`,
`note`, `terminal`, `code`, `music`, `lock`, `desktop`, `folder`, `keys`, `git`, `call`)
and `"icon"` (a picture or program whose icon to use). Actions: `obs.today`, `mail.open`,
`pc.lock`, `desktop.show`, `media.toggle`, `media.next`, `media.prev`, `vol.mute`,
`mic.mute`, `term.focus`, `git.commit`, `git.fetch`, `git.pull`, `git.push`, `git.stash`,
`call.mic`, `call.camera`, `call.hand`, `call.leave`, `call.focus`. The tray icon's
**Shortcuts…** opens this file, with the default list written in if it has none.

Your own actions for the context bar, per app, by its exe name (`chrome`, `spotify`) or
the name the deck shows (`Chrome`), `"*"` for apps without a list of their own. They join
that app's list (long-press a key to pick them) and make up its bar until you rearrange it.
`"contextBar": false` turns the bar off:

```jsonc
"contextBar": {
  "Spotify": { "color": "#1DB954", "keys": [
    { "name": "Like", "keys": "Alt+Shift+B", "glyph": "heart" },
    { "name": "Shuffle", "keys": "Ctrl+S", "glyph": "shuffle" }
  ] },
  "Chrome": [ { "name": "Pin tab", "keys": "Alt+Shift+P", "glyph": "pin" } ]
}
```

A key is `keys` (pressed in that app's window; `"g, i"` presses one after the other),
`text` (typed; `\n` for Enter: `{ "name": "Status", "text": "git status\n" }`), `action`
(as for shortcuts), or `"action": "close"`. Up to 8 per app.
`"approveFromDeck": false` turns the approval banner off (also in the tray menu). Glyphs: `play`, `pause`, `terminal`, `keyboard`,
`code`, `file`, `folder`, `search`, `split-right`, `split-down`, `back`, `forward`, `up`,
`down`, `prev`, `next`, `reload`, `undo`, `redo`, `history`, `plus`, `close`, `copy`,
`paste`, `save`, `edit`, `calendar`, `graph`, `bell`, `chat`, `mic`, `mic-off`, `video`,
`hand`, `screen`, `leave`, `headphones`, `hashtag`, `switch`, `unread`, `heart`, `star`,
`shuffle`, `music`, and more (the list is in `firmware/scripts/gen_font_icons.py`).

## Repository

| Folder | What's in it |
|---|---|
| `firmware/` | PlatformIO project: pages, panel, touch, USB link |
| `companion/` | The Windows tray app |
| `vscode-ext/` | The VS Code extension |
| `protocol/` | [v0.md](protocol/v0.md): every message between the parts |
| `install/` | Install kit builder and install/uninstall scripts |
| `docs/` | Guides: [connecting Google Calendar, Tasks and mail](docs/google-setup.md) |
| `backup/` | Factory firmware backup and restore steps |
| `simulator/` | The deck's UI on the PC, with feature tests and screenshot tours ([README](simulator/README.md)) |

[DESIGN.md](DESIGN.md) has the architecture, the build history and the decisions behind
each feature.
