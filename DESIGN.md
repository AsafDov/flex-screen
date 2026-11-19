# flex-screen — design

A 7" touch deck that sits next to the keyboard and runs the parts of the workday that
live outside the editor: audio, meetings, Claude Code, builds, git, windows, clipboard,
Obsidian, and a numpad.

UI reference: the **ESP32 Desk Companion – 7" UI** canvas
(https://claude.ai/artifact/Dh5LJ4fLcCeHGErS4omf3o): six swipe pages (Today, Audio,
Dev, Windows, Capture, Numpad) under one shared status bar (clock, next meeting,
Claude state, build result, keyboard layer, volume, mic mute). Since the side rail the
page tabs and the clock are a rail down the left, the status line runs beside it, and
the context bar runs along the whole bottom (`layout-0` tags the last commit before it).
Since 0.26.1 the Numpad is no page but a pop-up from the shortcuts panel, and the rail
has five buttons.

## Hardware

Waveshare **ESP32-S3-Touch-LCD-7**, touch version (AliExpress item 1005009051165419).

- 7" RGB panel, **800×480**, GT911 capacitive touch
- ESP32-S3 (QFN56, rev 0.2), dual-core LX7, Wi-Fi + BLE 5, 8 MB embedded octal PSRAM for
  the frame buffers, 16 MB quad flash
- CH422G IO expander (backlight, panel/touch reset, SD select)
- Two USB-C ports: native USB (used for the PC link) and UART (flashing/logs)
- Out of the box, the factory demo shows up on the native port for about a second after
  boot (as COM8) and then drops off. The first flash goes through the UART port, or
  through download mode (hold BOOT, tap RESET).

**The canvas is drawn at 1024×600** (Waveshare's separate "7B" board). Ours is the same
7" glass at 800×480, so scaling the layout by 0.78 keeps every button the same physical
size; only pixel density drops. Text the canvas sets below ~15px gets bumped so nothing
renders under 12px.

## Architecture

```
┌──────── ESP32-S3 board ────────┐   USB-C (native)    ┌──────────── Windows PC ─────────────┐
│ LVGL UI: status bar + 6 pages  │ ◄─ CDC serial ────► │ Companion (C# / .NET 10, tray app)  │
│ GT911 touch                    │    JSON lines       │  audio · media · windows · zones    │
│ USB composite: CDC + HID       │ ── HID keys ──────► │  calendars · tasks · git · vault    │
└────────────────────────────────┘    (numpad, media)  │  clipboard · profiles · sign-ins    │
                                                       │  localhost HTTP/WebSocket  ◄──┐     │
                                                       └───────────────────────────────┼─────┘
             VS Code extension (TypeScript) ── workspace, terminal runs, tasks ────────┤
             Claude Code hooks ─────────────── working / needs you / done ─────────────┤
             Browser extension (MV3) ───────── the active tab's address ───────────────┤
             PowerShell profile hook (opt.) ── Windows Terminal command results ───────┘
```

- **Firmware** draws the UI and turns touches into actions. It's a USB composite device:
  a serial channel to the companion, plus a real HID keyboard and media-key device, so the
  numpad, play/pause and volume keys work even when the companion isn't running.
- **Companion** is the brain on the PC. Everything that needs Windows APIs, Google or the
  filesystem lives here. It pushes state to the deck and runs the deck's actions.
- **VS Code extension** reports what only VS Code knows: the active workspace, every
  terminal command (start, end, exit code, duration), and task results. It also runs
  commands the deck sends (saved buttons, replay last, Ctrl+C, focus Source Control).
- **Claude Code hooks** post session state to the companion with `curl`.
- **Browser extension** (`chrome-ext/`, optional) reports the active tab's address and
  title over a WebSocket to `/browser`, so the context bar knows the site on any page.
- **Settings are data, in layers**: the profile (`profiles\<name>.json`, which can live in
  a synced folder) names the accounts, calendars, task lists, shortcuts, context bar keys
  and sites; `machine.json` is this PC's (which profile, where the profiles are, the
  Obsidian vault, and any profile setting it overrides); `Secrets` keeps the sign-ins,
  tokens per site and private calendar links, DPAPI-encrypted for the Windows user. Calendar
  and task sources are built from the profile's `accounts` at run time (`ICalendarProvider`,
  `ITaskProvider`), and the browser's sites from `sites.json` plus the profile's `sites`, so
  a new account, calendar or site needs no new firmware or release.

## Features

| # | Feature | Page | Runs in | How |
|---|---|---|---|---|
| 1 | Audio controls | Today, Audio | Companion (HID fallback) | Core Audio endpoint volume; HID media keys when the companion is down |
| 2 | Now playing | Today, Audio | Companion | Windows media session API: title, artist, art, position, source app, transport |
| 3 | Audio mixer | Audio | Companion | Core Audio per-app sessions: volume and mute per process |
| 4 | Audio source select | Audio | Companion | List output/input devices; set the default via `IPolicyConfig` (as SoundSwitch/EarTrumpet do) |
| 5 | Mute / unmute | Status bar | Companion | Mic: mute the default capture device, so Teams/Zoom/Discord all go quiet at once. Speakers: master mute |
| 6 | Git operations | Dev | Companion | `git` in VS Code's active repo: branch, ahead/behind, changed files, fetch, pull, push, stash, switch branch; *Commit…* focuses VS Code's commit box |
| 7 | Terminal | Dev | VS Code ext | Shell-integration events give each command's text, exit code and duration. The **last few commands, tap to run again**, the newest one's output, Ctrl+C, and a toast + status-bar flash when a long command finishes or fails |
| 8 | Claude status | Status bar, Dev | Hooks → companion | `UserPromptSubmit`/`PreToolUse` = working, `Notification` = needs you, `Stop` = done; per session and project. Tap = focus that VS Code window |
| 9 | Change active window | Windows | Companion | Enumerate app windows the way Alt+Tab does, with icons; tap = focus, hold = close; Show desktop is Win+D over HID |
| 10 | Build status | Status bar, Dev | VS Code ext | Task exit codes plus terminal runs of build commands; last 10 results |
| 11 | FancyZones | Windows | Companion | Read PowerToys' FancyZones layouts from `%LOCALAPPDATA%`, compute each zone's rectangle per monitor, snap the window with `SetWindowPos`; switch layouts by writing `applied-layouts.json` and posting FancyZones' reload message |
| 12 | Clipboard stack | Capture | Companion | Clipboard listener keeps the last 20 text clips plus pins; tap = paste. Skips clips that password managers mark as excluded from history |
| 13 | Obsidian | Capture | Companion | The vault Obsidian has open (or machine.json's `obsidianVault`): today's note (`obsidian://` URI), Clipboard → Inbox (append to the file), recent notes (from `.obsidian/workspace.json`), quick capture |
| 14 | Calendar | Status bar, Today | Companion | Every calendar source in the profile (one `ICalendarProvider` each: Google accounts through the Calendar API, Microsoft 365 through Graph, iCalendar links expanded by the companion), merged and filtered: next-meeting countdown, Up next with its Join button, and the whole day behind it (tap Up next), each event in its calendar's color |
| 15 | Tasks | Capture, Today | Companion | Open tasks from the task sources (Google Tasks, Microsoft To Do, the latest Obsidian daily note, Jira and GitHub issues; one `ITaskProvider` each): All tasks (every task of the lists chosen in the tray, where it opens), Scheduled (every dated task) or one of those lists, grouped by due day; tap = open (Open in its source), box = tick off (not Jira's or GitHub's issues), + = add one with the on-screen keyboard. Today has a count pill |
| 16 | Keyboard layer | Status bar | Corne firmware + companion | The Corne runs QMK with Vial, which has no "current layer" query, so a small firmware addition answers a custom raw-HID request with the active layer. The companion polls it (pausing while the Vial app is open so their replies don't cross) and maps layer numbers to names (BASE, LOWER, RAISE, ADJUST). Hidden when the Corne isn't plugged in |
| 17 | Numpad | Pop-up (Shortcuts) | Firmware only | USB HID keypad keys plus a calculator mode; no companion needed |
| 19 | Context bar | Every page | Companion | Keys for the app in front (the window list's foreground, by exe), in its color, picked on the deck from the app's bank of actions (its integration's, `companion/Integrations/<App>/`) (long press) and kept per app; the profile's `contextBar` adds actions. In a browser, a site's own keys (`sites.json` and the profile's `sites`), told by the tab's address (browser extension), an installed web app's name, or the title. Keys are `SendInput` combos into that window (refocused first if it lost the front), deck actions, or closing it |
| 18 | Shortcuts launcher | Every page (the rail's foot) | Companion | Up to 12 tiles from the profile: links, Start menu apps (found in `shell:AppsFolder` by name, focused if open, else started with `explorer shell:AppsFolder\<id>`), files and folders, deck actions, key combinations (`SendInput`). Icons from the shell (`IShellItemImageFactory`) and sites' `/favicon.ico` |
| 20 | Edge glow | Every page, the idle clock | Firmware (state from the companion) | Four 10 px strips on LVGL's system layer, solid at the edge and fading inward, that blink twice and fade out (2.8 s) when something starts: amber when a Claude session goes to `input` or an approval or question comes; red when a build turns `failed` (one under 10 minutes old); blue when `call` gets an app |
| 21 | Diagnostics | Overlay (hold the clock) | Firmware only | Firmware version and reset reason, uptime, internal RAM and PSRAM (`heap_caps_*`), frames per second and draw time (LVGL's refresh events), the link's counters and last error (`pc::linkStats()`) |

Extras on the canvas: **Lock PC** (`LockWorkStation`) and the **Headset** quick switch are
easy. **Focus mode** and the **Claude usage meter** have no clean data source yet. They're
parked until we find one.

## Protocol (v0)

Full spec: [protocol/v0.md](protocol/v0.md). Newline-delimited JSON over USB CDC. The companion owns the state
and pushes a full snapshot per topic whenever that topic changes. The deck sends actions.

```jsonc
// deck → PC
{"t":"hello","fw":"0.1.0","w":800,"h":480}
{"t":"act","a":"media.toggle"}
{"t":"act","a":"mixer.vol","app":"Spotify.exe","v":40}
{"t":"act","a":"term.replay"}

// PC → deck
{"t":"state","topic":"media","d":{"title":"Weightless","artist":"Marconi Union","app":"Spotify","playing":true,"pos":192,"len":489,"art":"a1f3…"}}
{"t":"img","id":"a1f3…","w":96,"h":96,"fmt":"rgb565","b64":"…"}
{"t":"toast","level":"error","text":"npm run build failed · exit 1 · 42 s"}
```

Topics: `status` (clock, claude, build, kbd, vol, mute, next meeting), `media`, `audio`,
`mixer`, `windows`, `zones`, `git`, `term`, `clipboard`, `obsidian`, `calendar`, `tasks`.
Images (album art, window icons) are sent once by content hash and cached on the deck.

## Repository layout

```
protocol/    message schema shared by every part
firmware/    PlatformIO project (Arduino-ESP32 3.3, LVGL 9): screens, panel, touch, USB link
companion/   C# / .NET 10 tray app; also installs the Claude Code hooks (--install-hooks)
vscode-ext/  TypeScript VS Code extension
chrome-ext/  the browser extension (Chrome, Edge): the active tab's address, for the context bar
install/     install kit for another PC: build-dist.ps1, install/uninstall scripts
dist/        build-dist.ps1's output (flex-screen.zip)
docs/        user guides (google-setup.md: Calendar and Tasks; calendars.md: other calendars and filters; task-sources.md: the other task sources; also in the install kit)
simulator/   the firmware UI on the PC (MSVC), with a Python driver, feature tests and screenshot tours
qmk/         (phase 6) Corne keymap addition (layer report) for vial-qmk
```

## Installing on another PC

The deck needs nothing per PC. For the PC side, run `install\build-dist.ps1` here: it
publishes the companion self-contained (one 56 MB exe, no .NET needed there) and stamped
with the release's version, builds the deck firmware into `companion\firmware\` (with a
`firmware.json` giving its version, size and SHA-256), packages the VS Code extension and
zips it all with the scripts into `dist\flex-screen.zip`. On the other PC, unzip and
double-click `install.cmd`, which (without admin rights):

- copies the companion to `%LOCALAPPDATA%\Programs\flex-screen` and adds Startup and
  Start menu shortcuts,
- installs `flex-screen.vsix` with `code --install-extension`,
- runs `flex-screen-companion.exe --install-hooks`, which merges the hooks into
  `~/.claude/settings.json` (keeping a `settings.json.before-flex-screen` backup); the
  Claude extension in VS Code reads the same file as the CLI,
- starts the companion.

Running it again updates in place; `uninstall.cmd` reverses all of it. Since 0.21.0 the
companion does both updates itself: it offers the deck the firmware from its kit, over
the link, and installs newer kits from the GitHub releases (build phases, "Updates
across PCs"). `pio run -d firmware -t upload` is for working on the firmware.

## Toolchain on this PC

| Need | Status |
|---|---|
| PlatformIO core | upgraded to 6.2.0 (the pioarduino build, which reports `6.2.0.post1`) |
| ESP32 platform | pioarduino **55.03.311** (Arduino-ESP32 3.3.11, ESP-IDF 5.5.5). Pinned because 55.03.312 and later read `6.2.0.post1` as older than 6.2.0 and refuse to install |
| The deck's PlatformIO folder | `~/.platformio-deck` (`core_dir`, from 0.27.0): the deck rebuilds ESP-IDF with its own settings into its packages, so it doesn't share `~/.platformio`. The first build there downloads the toolchain and ESP-IDF and rebuilds it, ~25 min; changing `custom_sdkconfig` rebuilds it (~15 min); `src/` alone ~3 min |
| Windows long paths | on. Arduino-ESP32's libraries package can't unpack without it |
| Node 26 (VS Code extension) | installed |
| .NET 10 SDK (companion) | installed, 10.0.403 |

Gotchas found in phase 0:

- Run `pio` from PowerShell, not Git Bash. The platform's toolchain installer refuses
  MSYS ("MSys/Mingw is not supported"), so the compiler never gets installed.
- The devkit board file hard-codes `ARDUINO_USB_MODE=1`; `platformio.ini` replaces its
  `extra_flags` to get mode 0 (TinyUSB) without a redefinition.
- The UART port can't reset the chip, so flashing over it needs BOOT + RESET by hand, at
  460800 baud (921600 corrupts reads).

Gotchas found in phase 1:

- `pio run -t upload` sends the firmware over the deck's own serial link
  (`firmware/scripts/ota_upload.py`, ~15 s for 1 MB): the running firmware writes the
  spare app slot and restarts into it. `deck_port.py` stops the companion (it holds the
  port) and starts it again after. *(Changed in phase 2; see below.)*
- `pio run -e deck-esptool -t upload` is the ROM-bootloader fallback, for a first flash
  or a build that won't boot. Rebooting into the bootloader by software (a 1200-baud
  open) wedged the chip's USB-Serial-JTAG port in 3 of 6 tries ("Write timeout" until a
  replug), with or without a settle delay or esptool's own reset. Holding BOOT while
  tapping RESET always worked; after flashing, tap RESET to start the new firmware.
- Expander IO5 (`USB_SEL`) must stay low; high switches GPIO19/20 to CAN and kills USB.
- Arduino's CDC drops bytes past a 256-byte receive buffer while the loop is drawing;
  the deck sets 16 KB.
- The deck can only write while the host's DTR and RTS are up, and a line-state change
  can be lost if the port opens while Windows is still installing the driver. The
  companion drops and raises both until the deck says `hello`.
- Arduino's CDC also reboots into the bootloader when DTR/RTS move like esptool's reset,
  which ordinary port opens and the handshake above can trip (and a half-finished
  sequence leaves the port looking closed). The firmware turns that off and keeps only
  the 1200-baud reboot.
- Arduino's 8 KB loop stack is too small for LVGL's recursive rendering; the firmware
  asks for 32 KB (`SET_LOOP_TASK_STACK_SIZE`). Panics write an ELF core dump to the
  `coredump` partition (0xFF0000, 64 KB). Read it from the bootloader with
  `esptool read-flash 0xFF0000 0x10000 core.bin` and decode it with
  `python -m esp_coredump --chip esp32s3 info_corefile --core core.bin --core-format raw
  --gdb <xtensa-esp32s3-elf-gdb> .pio/build/deck/firmware.elf`. The deck's `hello` also
  reports its last reset reason.
- Don't name a C++ namespace `link`: it collides with POSIX `link()`.

Gotchas found in phase 2:

- Firmware updates: Arduino's `Update.progress()` only moves when a 4 KB sector is
  flushed, and that happens on the *next* write, so it can't be the "bytes received"
  check (0.2.4 counts bytes itself). From pyserial on Windows, dropping and raising
  DTR/RTS leaves the deck seeing the port as closed; the uploader just opens and syncs.
- The companion sends state only after the deck's `hello`: a deck that has just booted
  isn't reading yet, and the album-art burst (~46 KB at 128 px, ~87 KB now) overflowed its 32 KB buffer.
- The GT911 shipped with a 3 s idle timeout (0x8055) before slow scanning, which made
  the first touch after a pause lag and quick taps miss. The firmware sets 15 s, the
  maximum; the controller stores it, so it's written only when it differs.
- Windows' "current" media session is whichever app changed state last, so the Spotify
  app and Spotify-in-Chrome fought over the deck. The companion picks: a playing session,
  a media app over a browser, otherwise the one shown last.
- Chrome first reports its own icon as the thumbnail, then the page's artwork; art is
  reloaded on every media-properties change and deduplicated by content hash.
- App (session) volume is linear amplitude while master is audio-tapered. App faders
  first mapped through `level^1.75` so 50% sounded like 50% on master, but then the deck
  read Chrome 53 where Windows' volume mixer said 33; since 2026-11-07 they show the
  session's own value, as Windows does.
- One level per app, on every output: Windows keeps a session per output device an app
  has played on, each with its own volume, and Settings' Volume mixer showed (and moved)
  Chrome's and Flow Launcher's sessions on the monitor's audio although both follow the
  default device (Speakers), where the deck read them. The companion now reads every
  active output's sessions: a deck fader sets all of an app's sessions, a session that
  changed between two reads (Windows' mixer, or the app's own control) sets the app's
  level, the default device's winning a tie, and the rest are brought into line. An app
  seen for the first time takes the level heard on the default device.
- Endpoint volume notifications arrive on several threads and can overtake each other;
  the companion re-reads the real level (at most every 30 ms) instead of trusting them.
- Switching the default device takes 0.5–2 s in Windows and fires the change
  notification once per role; it runs on its own thread (latest tap wins), and the deck
  lights the tapped button immediately.
- Smart Shuffle isn't reachable: Windows media sessions only have shuffle on/off, and
  Spotify's public Web API doesn't expose it either. Spotify's desktop UI isn't visible
  to UI Automation (or MSAA) unless a screen reader is running, so its own shuffle button
  can't be pressed from outside either. What's left (stealing focus for its keyboard
  shortcut, or modding Spotify with Spicetify) was ruled out.
- The GT911 now and then reports one empty frame mid-press, which LVGL read as a lift
  and a second tap; on a toggle (mic mute) the two cancelled out. A lift counts only
  after 40 ms without a finger, and the status bar's toggles ignore a repeat within
  300 ms.
- Mixer fader order is fixed: Spotify, Chrome, Teams/Discord, System, Flow Launcher,
  then anything else (busiest first). Up to 5 apps next to Master.
- Windows' media session has no queue, so "up next" isn't possible without Spotify's
  Web API; the Audio page uses the space for 180 px album art instead.

Gotchas found in phase 3:

- Claude Code hooks run `curl.exe` in exec form (`command` + `args`, no shell, so no Git
  Bash needed) with `async: true`: Claude never waits, and a stopped companion just
  costs a failed curl.
- `Notification` fires for permission prompts and also for the "waiting for your input"
  reminder a minute after Claude stops; only the first (or one that arrives mid-work)
  means "needs you".
- A hook's `cwd` follows Claude around; a session keeps the project it started in.
  Projects are the git root's folder name, else the folder's.
- VS Code's terminal shell integration gives each command's text, output and exit code;
  shells without integration (cmd.exe) give no exit code, shown as plain "Done".
- VS Code's extension host has Node's global `WebSocket`, so the extension has no
  dependencies. An `HttpListener` on a `localhost` prefix needs no URL ACL or admin.
- The Dev page cost ~38 KB of internal RAM (free internal heap ~135 KB on 0.2.5, ~97 KB
  on 0.3.0): LVGL allocates widgets with `malloc`, which keeps small blocks internal.
  Fixed in 0.4.0, see phase 4.
- `pio` isn't on PATH in VS Code terminals that opened before PlatformIO's extension
  started, and the active terminal can be in any folder: the first build from the deck
  failed in 0.1 s. Deck commands now run in the extension's own terminal at the
  workspace root, and saved PlatformIO commands use `~/.platformio/penv/Scripts/pio`.
- LVGL's Montserrat is ASCII plus symbols, so "·", "…", dashes and accented letters drew
  as boxes. `scripts/gen_font_extra.py` renders Latin-1, Latin Extended-A, basic
  Cyrillic and that punctuation from LVGL's own Montserrat-Medium.ttf with Pillow
  (`src/fonts/font_extra.c`, ~80 KB of flash); Montserrat falls back to it, and it to
  DejaVu for Hebrew and Arabic. Text from the PC must use `title_font`/`body_font`/
  `small_font`, not the bare built-ins.

Gotchas found in phase 4:

- LVGL's heap is in PSRAM (`src/lv_mem_psram.c`, a custom stdlib): free internal RAM went
  from ~97 KB to ~195 KB. While a frame renders, allocations (draw tasks, scratch buffers)
  come from internal RAM instead; the heap's location turned out to matter little (an
  internal-only heap was 3-6% faster).
- LVGL objects take presses by default, so the boxes inside buttons (the window tiles'
  icon and text, the mic button's icon) swallowed taps and long presses: tiles only
  reacted on their padding, and hold-to-close never fired. `box()` now makes containers
  non-clickable.
- A label with `LV_LABEL_LONG_MODE_DOTS` only ellipsizes once its height is fixed;
  otherwise it wraps and grows. `ellipsize()` sets one line's height.
- Drawing is bound by PSRAM bandwidth. With the panel scanning out 32 MB/s, PSRAM gives
  ~22 MB/s fill and ~12 MB/s copy (octal, 80 MHz, 32-byte cache lines), so a full redraw
  takes ~100 ms on an empty page and 170-250 ms on the real ones (`{"t":"bench"}` reports
  this). Creating the panel from core 0, so its bounce-buffer interrupt runs beside LVGL
  instead of interrupting it, made redraws 25% faster but the picture flickered: the
  refills then race LVGL for PSRAM and sometimes lose. The panel is created from setup()
  (core 1) again, with 20-line bounce buffers. -O2 gains a few %. What didn't help: LVGL's
  hot loops in IRAM (-10% but 115 KB of internal RAM) and rendering 32-line strips in
  internal RAM (slower, since both frame buffers then need copying). So page changes
  don't animate:
  a swipe or tab tap switches at once, one redraw instead of a slide at ~5 fps, and pages
  only touch what changed (`setText`, `setTextColor`… skip unchanged values). A 64-byte
  data cache line, or running code and constants from PSRAM, would need rebuilding
  Arduino's libraries (pioarduino's `custom_sdkconfig`); that's the next lever.
- Swipes: LVGL hands a gesture to the nearest parent that doesn't pass gestures on, and
  every object passes them on by default, so swipes went past the tab view to the screen.
  The tab view's content stops them (`lv_obj_set_gesture_bubble(content, false)`).
- Focusing a window: Windows only lets the app with the last input hand over the focus.
  Tapping Alt (the usual trick) arrived at the new window as "open the menu"; a zero-pixel
  mouse move counts as input without side effects, with `AttachThreadInput` as fallback.
- Snapping resizes every window: the Claude app and Windows 11's Notepad draw their own
  frames and don't set `WS_THICKFRAME`, and checking it (as FancyZones' code does) only
  moved them.
- Two kinds of window can't be snapped, and the deck says so with a toast: apps running
  as administrator (Device Manager; Windows' UIPI refuses `SetWindowPos` with error 5
  unless the companion runs elevated too), and apps that hold their own size, like the
  Claude app while maximized by its own title bar: it's a translucent Electron window,
  not natively maximized, and it overrides the new size before `SetWindowPos` returns.
  Restoring it in the app first makes it snappable.
- `POST /test` on the companion fires deck actions (focus, close, snap, layout, bench)
  for testing without the deck; see protocol/v0.md.
- The companion is per-monitor DPI aware (`ApplicationHighDpiMode` PerMonitorV2), so
  window bounds, work areas and zones are all physical pixels on every monitor.
- Alt+Tab's filter: visible, titled, not cloaked (`DWMWA_CLOAKED` hides windows on other
  virtual desktops and suspended Store apps), not a tool window, unowned unless
  `WS_EX_APPWINDOW`. Store apps run in ApplicationFrameHost; the app's own process owns
  a child window. Tiles show the 8 most recently used windows, ordered by when they
  first appeared, so they don't reshuffle on every focus change.
- Window icons: `Icon.ToBitmap()` keeps 32-bit icons' alpha (drawing the HICON into a
  bitmap loses it). They go to the deck as RGB565A8, deduplicated by content hash.
- FancyZones' zones are recomputed from its own files with PowerToys' formulas
  (`FancyZonesLayouts.cs`, from `LayoutConfigurator.cpp`): priority grid tables for 1-10
  zones, grid/columns/rows with spacing on the outer edges and between zones, focus,
  and custom grid/canvas layouts. Work area, not the full monitor.
- FancyZones watches `applied-layouts.json`'s write time, resets to defaults if it reads
  a half-written file, and reloads on the registered message
  `{2ef2c8a7-e0d5-4f31-9ede-52aade2d284d}` posted to its `SuperFancyZones` window. The
  companion writes a temp file, renames it over, and posts the message.
- Snapping follows FancyZones: restore a maximized window, subtract the invisible
  resize borders (DWM extended frame bounds) except at the top, and set the position
  twice so a window moving to a monitor with another scale ends up the right size.
- Monitors match FancyZones' entries by EDID id ("DEL4240") and PnP instance from
  `EnumDisplayDevices(..., EDD_GET_DEVICE_INTERFACE_NAME)`; names ("DELL P2722H") come
  from the EDID in the registry.
- The first boot after an update has twice reported reset reason `watchdog` (0.2.4 and
  0.3.0); later boots don't, and nothing crashed. Not chased yet.

## Build phases

0. **Tooling**: install the .NET 10 SDK; build and flash a USB test firmware (CDC serial +
   keyboard + media keys) and see it show up on the PC. **Done 2026-11-06**: the factory
   flash is backed up in `backup/`; `firmware/` enumerates on the native port as
   "flex-screen deck" (keyboard, consumer control, CDC serial) and sends
   `{"t":"hello","fw":"phase0","psram":8388608}` every second. It stays connected, so
   the native port dropping off earlier was the factory demo's doing.
1. **Skeleton, end to end**: give the deck its own USB product ID (it currently reuses
   Espressif's 303A:1001, the same as the chip's built-in USB-Serial-JTAG) and flash over
   the native port (1200-baud reset into the bootloader) so one cable does everything;
   bring up the panel, touch and USB link on the board;
   protocol v0; the status bar and six page tabs at 800×480; the companion runs in the
   tray and connects. First live feature: now playing, play/pause and master volume.
   *Done when tapping pause on the deck pauses Spotify.* **Done 2026-11-06** with
   firmware 0.1.2 (LVGL 9.6, direct mode on two PSRAM frame buffers): on the device,
   play/pause toggled Chrome's media session within ~20 ms each tap, the volume slider
   drove master volume (rate-limited while dragging), and tab swipes ran without a
   reboot. Tested against Chrome rather than the Spotify app (same Windows media-session
   path). The mute tap on the volume pill hasn't been tried yet.
2. **Audio**: mixer, output/input select, mic and speaker mute, Today page media and clock.
   **Done 2026-11-06** (firmware 0.2.5): Audio page (180 px art, progress,
   shuffle/repeat, devices, mixer), mic button, album art on Today, firmware updates over
   the link. Two on-device rounds; everything confirmed working except the last fixes
   (mic debounce, fader order, bigger art), which haven't been tried yet.
3. **Dev**: VS Code extension (terminal runs, tasks, replay, saved commands, Ctrl+C),
   build status and finish/fail toasts, git panel, Claude Code hooks and banner.
   **Built 2026-11-06** (firmware 0.3.0): Dev page (Claude banner, git card, build card
   with last 10 runs, terminal card with saved commands, replay and Ctrl+C), Claude and
   build pills in the status bar (tap → Dev), toasts over any page. Checked on the PC
   side: hooks fire for live sessions, the extension connects from VS Code, and a
   terminal run reaches the deck as a toast. Not yet tried by hand on the deck. Install
   kit for a second PC in `install/`.
4. **Windows**: window switcher with icons, FancyZones layouts and snapping.
   **Built 2026-11-06** (firmware 0.4.0): Windows page with the 8 most recent windows
   (icons, tap = focus, hold = close) and Show desktop; FancyZones panel with Priority /
   Columns / Rows / Grid per monitor (tap again for more zones), a zone map to snap the
   active window, and a monitor picker. The design's fourth layout, Focus, was swapped
   for Rows: Focus stacks overlapping windows, which a zone map can't show usefully.
   Zone math checked against the live layouts (DELL P2722H and LEN T24i-10, priority
   grid 3). First on-device round: taps and holds on tiles missed, focus opened the
   window's menu, long titles wrapped over tiles, snapping only moved the Claude app, and
   swiping between pages crawled; fixed in 0.4.7 (see the phase 4 gotchas), snapping
   and focus checked through the companion's test hook. Second round: swipes did nothing,
   the screen flickered, and Device Manager and Claude ignored snaps; 0.4.8 delivers
   swipes, moves the panel interrupt back to LVGL's core, and the companion explains
   unsnappable windows. Waiting on a third look on the deck.
5. **Google + Capture**: **Built 2026-11-07** (firmware 0.6.0). Capture page: mail tasks
   (the todo skill's Google Tasks tagged `[todo src=gmail:…]`, with sender and due day;
   tap the box to tick one off), the clipboard stack (tap to paste, hold to pin, Clear),
   and Obsidian (today's note and the five most recent notes; quick capture and
   clipboard → Inbox were cut on request).
   Today page: up next with a Join button, later today, and the mail-task count; the
   status bar shows the next meeting's countdown, amber from five minutes out. Google is
   read with the user's own OAuth client ("Desktop app" JSON at
   `%LOCALAPPDATA%\flex-screen\google-client.json`, then tray menu → Connect Google
   Calendar and Tasks); the refresh token is DPAPI-encrypted for the Windows user. Not
   connected yet: the client file is the user's to create. Not yet tried by hand.
6. **Numpad + keyboard layer**: **Built 2026-11-07** (firmware 0.5.0). Numpad page: a
   USB keypad (keys held while pressed, so Windows repeats them; Num Lock follows the
   PC's light and turns on by itself for a digit), editing keys, and `( ) , =` typed by
   the companion as Unicode; Calculator mode evaluates on the deck and Enter types the
   result. Keyboard layer (named BASE, NAV, SYMBOL; `layerNames` in
   `%LOCALAPPDATA%\flex-screen\settings.json` overrides): no Corne firmware change after all. Its receiver answers VIA
   over raw HID (protocol 9, Vial 6, 8 layers, unlocked), so the companion polls which
   keys are held (VIA's switch-matrix query, 25 Hz) and reads those keys from the keymap,
   and works the layer out as QMK would (MO/TT/OSL/LM held, LT held past 200 ms, TG/TO/DF
   presses, tri-layer keys); the status bar shows it, lit off the base layer. Read-only,
   paused while the Vial app is open, and nothing but the layer is kept. The wireless
   receiver does report held keys. The matrix is 4 rows by 12 columns (both halves side
   by side), so VIA sends two bytes a row; decoding one byte a row made letters look like
   layer keys. The companion now takes the width from the keymap (one past the last
   column with a key) and logs each layer key's press. Replies are matched to their
   request, since every app with the interface open sees every reply. First try felt unresponsive: the thumb keys are
   `TG(1)`, `TG(2)` and `LT(1, Space)`, and a toggle has to be caught while it's down, which
   40 ms polling missed on quick taps. The query takes ~1 ms, so it now polls as fast as
   Windows sleeps (~15 ms), and an LT key counts as held as soon as another key goes down
   (QMK's hold-on-other-key-press) instead of after the 200 ms tapping term. A toggle the
   companion missed (it wasn't running, or the Vial app had the keyboard) stays out of
   step until the next one.

Gotchas found in phase 5:

- The Obsidian vault moved (Google Drive renamed `Documents` to `06 Notes`) and Obsidian's
  own vault list still had the old path, so Obsidian answered "vault not found". The
  companion takes `obsidianVault` from settings.json if set, else Obsidian's open vault,
  else looks for a folder of the same name with a `.obsidian` inside, up to three levels
  under the nearest folder that still exists; then it points Obsidian's vault list
  (`%APPDATA%\obsidian\obsidian.json`) at the new folder, only while Obsidian is closed
  (it keeps the list in memory). Notes open with `obsidian://open?path=…`.
- LVGL only shortens a label with "…" when the label has a fixed width. Labels given the
  rest of a flex row but left content-sized ran out of their boxes (clipboard rows,
  Obsidian's recent notes, layout buttons); `fillRow()` fixes their width, and
  `setTextUpTo()` sizes the meeting pill and toasts to their text up to a maximum.
- Clipboard: pins are saved to `%LOCALAPPDATA%\flex-screen\clipboard-pins.json`, the
  recent clips to `clipboard-recent.bin` there, DPAPI-encrypted for the Windows user
  (they used to be in memory only, and every companion restart emptied the deck's
  list). Clips marked
  `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` or
  `CanIncludeInClipboardHistory = 0` (password managers) are never recorded. The
  clipboard must be touched on the UI thread, so deck actions are posted to the
  listener window.
- Google Tasks' API has no "completed today" filter that keeps open tasks too, so the
  companion asks for open tasks and keeps the ones ticked from the deck in view
  (struck through) until midnight.

Gotchas found after phase 6:

- Claude Code hooks don't say which app runs the session. The companion looks up the
  hook request's TCP connection (`GetExtendedTcpTable`), finds curl's process, and
  walks its parents to the first one with a window: the Claude app, VS Code or a
  terminal. The status bar's Claude pill, the Dev banner and the approval toast bring
  that window up.
- Palettes: "base" (the design canvas) and "vibrant". Colors are read when pages build,
  and some page styles are computed at startup, so the deck saves the choice (NVS) and
  restarts into it; `settings.json` "palette" or the tray's Deck colors menu picks it.
- Idle: the backlight is on/off only (CH422G pin), so the idle clock is dim grey on black,
  then the backlight goes off. Idle means no touch on the deck and no input on the PC
  (`GetLastInputInfo`); a locked PC shows the clock at once. A tap wakes the deck and is
  swallowed by the clock screen. Timings: `settings.json` "idle".
- Tapping the FancyZones zone a window already fills maximizes it; a tap on a zone
  snaps a maximized window back into it.
- Calls (firmware 0.8.0): a call is an app using the microphone right now, which Windows
  records per app (`HKCU\…\CapabilityAccessManager\ConsentStore\microphone`, a stop
  time of 0; `webcam` the same for the camera), so Teams (a Store app, `MSTeams_…`),
  Zoom, Discord, Webex and browsers are noticed without their help. A panel pops over
  the pages with what each allows; Hide turns it into a status-bar pill. Teams: its
  local API on ws://127.0.0.1:8124 (protocol 2.0.0, as Stream Deck uses), with real
  mute/camera/hand state, leave and reactions, once "Manage API" is on in Teams'
  privacy settings and the first action is allowed there (the token is DPAPI-encrypted).
  Zoom has no API, and no shortcuts for its drawing tools, but its toolbars name every
  button and its state for screen readers (firmware 0.18.0, `ZoomControl.cs`, checked on
  Zoom Workplace 7.1.9): the meeting toolbar (`ZPControlPanelClass`, or
  `ZPFloatToolbarClass` while sharing) gives mute ("Mute, currently unmuted, Alt+A…" vs
  "Unmute…"), video, Whiteboards and Annotate; the annotation toolbar
  (`ZPAnnotatePanelClass`) and its menus (`ZPMenuClass` by title: Draw, Color, Stamp,
  Spotlight, Clear Drawing) give every tool with its check. These native ones are read
  through MSAA and pressed by posting a click to the window at the button's spot, which
  leaves the mouse alone and takes no focus; Escape posted to a menu closes it without
  ending the annotation. The whiteboard is a WebView2 page in the meeting window
  (`Chrome_WidgetWin_1` "Zoom Whiteboard"), read through UI Automation in one cached
  pass (~15 ms): a tool's ExpandCollapse picks it and opens its panel, then folds it
  again; the selected tool has `_selected_` in its class, and the eraser's group is
  named "Stroke eraser" or "Pixel eraser". Panels (firmware 0.18.1): the pen has 24
  swatches, the marker 6 (see-through, so the same names are other colors), both a
  Weight slider 1–20; the laser (in the header, left out when the window is narrow)
  has a menu of 6 dark colors (`colorMenuItem`, chosen by `_selected_`); the eraser
  has its kind switch, and a Weight slider only as the pixel eraser. Colors and
  weights only show in an open panel, so the companion notes each panel when it's
  open (Zoom's or its own), opens an unseen tool's panel once for a moment, and keeps
  the eraser's per kind. Palettes are known by name per tool, sampled on screen;
  printing Zoom's window doesn't capture its web content, so a color Zoom adds later is
  sampled from the screen only where Zoom is what's showing. Closing a blank board asks "save this blank whiteboard?",
  answered Discard. All of it runs on one STA thread, a step at a time. Zoom
  Workplace's own window (`ZPPTMainFrmWndClassEx`) names its navigation tabs "Home 1 of
  6", "Chat 3 of 6" (MSAA page tabs), and Home is a web page whose instant-meeting
  button is "Meet" ("Back to meeting" while in one): the context bar's New meeting and
  Chat press those. Raise hand is
  still Alt+Y into the meeting window (Zoom 7's `ConfMultiTabContentWndClass`, or the
  floating toolbar while sharing); leaving still needs confirming in Zoom, since a
  host's dialog also offers "End for all". Discord's API needs approval for voice control, so mic is
  Windows' mute and Deafen also mutes Discord in the mixer.
- Keyboard layers after the 12-column fix: LT keys count as held after 200 ms, or once
  another key is pressed and released inside them (QMK's permissive hold). Counting any
  key pressed meanwhile flashed NAV on while rolling from Space to the next letter.
- Layer cheat sheet (firmware 0.9.0): holding a layer key for a second (from its press)
  shows that layer laid out like the keyboard; letting go hides it, a tap dismisses it,
  and tapping the layer pill pins it open, following the layer. The layout is the
  keyboard's own: Vial keyboards carry their vial.json, xz-compressed, readable with
  Vial's definition commands (0xFE 0x01 size, 0xFE 0x02 blocks; replies don't echo).
  .NET has no LZMA, so `Xz.cs` is the spec decoder, checked by the stream's CRC64. The
  W-CORNE's is 46 keys on a 4x12 matrix: 3x6 a side, two extra keys on each inner edge
  (the left ones are TG(SYMBOL) and TG(NAV)), three more on the far right, and the
  thumbs. The keymap comes in one go through VIA's keymap buffer (28 bytes a request).
  Labels are made on the PC (`KeyLabels.cs`, QMK v6 keycodes, US legends), so the deck
  only draws strings; long layer names shorten on keys (SYMBOL is SYM).
- Round of 2026-11-07 evening (firmware 0.10.0, extension 0.2.0):
  - `/deck` on the companion switches the deck's page and returns a PNG of its screen
    (the frame buffer on show, RGB565 in base64 lines, ~2.5 s), so layouts are checked
    on the real board from the PC.
  - Mixer: the mute buttons stood below the mixer's edge. A margin on a flex-grow item
    isn't subtracted when LVGL shares out the free height, so the faders' 8 px margins
    made each column 16 px too tall; the fader now sits in a padded holder that grows.
  - Buttons sized to their label (Open on the Claude banner, Focus) showed blank: a
    label's max width as a percentage of a content-sized parent resolves to 0.
  - Clipboard: "TAP TO PASTE" in the header sat against Clear (taps on it cleared the
    list); the hint moved to the bottom. Copies were lost when the clipboard was busy at
    the moment of the change notice (Windows' own clipboard history reads every copy
    too): the companion now reads with plain Win32 calls a moment after the notice,
    retries for about a second while it's busy, polls the clipboard's sequence number
    as a fallback, and logs why a copy wasn't listed (never what it was).
  - Windows page: tiles in the taskbar's order. UI Automation reads the taskbar's
    buttons ("Appid: …"); a window matches by its AppUserModelID property, its packaged
    app id, its exe under a known folder, "*PID…", or else the exe name inside the id
    ("md.obsidian"). Read every 2 s, and at once when a window opens or closes.
  - Dev terminal: the saved command buttons and Rebuild are gone; the card lists the
    last four commands (✓/✕, when), tap one to run it again, next to the newest one's
    output. The extension keeps them per workspace, so they're back after a companion
    restart or a VS Code reload (the deck used to show "No commands yet" then).
  - Calendar: every calendar ticked in Google Calendar's list (reader access or more),
    not just the primary one, so a second Google account's calendar shows once it's
    shared with the signed-in one; its mail comes in by Gmail forwarding, for the `todo`
    skill. A meeting on two calendars shows once (same iCalUID and start). Chosen over a
    second sign-in on the companion: no second token, and Google Calendar's own ticks
    decide what the deck shows.
- Firmware 0.10.1: the layer cheat sheet waits for ten seconds of holding (was one), on
  request; tapping the layer pill still opens it at once.
- Firmware 0.11.0:
  - Dev terminal: only the last few commands, each in full (wrapping to two lines; a row
    that would run past the card is left out) with the time it started, tap to run
    again. The output box, status, Ctrl+C and Focus went, on request.
  - Today: "Later today" became **Recent**, the last five things done from the deck
    that can be done again (window, note, command, meeting, Gmail, Claude session,
    output device), as tiles. The companion records them as the deck's actions come in
    (`RecentActions.cs`); doing one already listed doesn't add or move it, so tiles stay
    under the finger. A window is one tile per window, but a closed window's tile is
    taken over by a new window of the same exe, so a restarted app doesn't leave a
    stale twin; tapping a closed window's tile brings up another window of its app or
    starts the exe. Kept DPAPI-encrypted across restarts. The meeting tile keeps its
    link, since the event leaves the calendar's view once it's over.
- Firmware 0.11.1, the startup overflow: on connecting, the companion sent album art,
  eight window icons and every topic at once (from several threads), the deck's 32 KB
  receive buffer overflowed while it built its pages, and a lost snapshot stayed lost
  until that topic next changed (the Recent card and the Claude pill came up empty).
  Now: `DeckLink` holds writes to ~64 KB/s after an 8 KB burst; the hello burst is
  paced, art last; when the deck logs an overflow, a bad line or a bad image part, the
  companion sends every topic again 1.5 s later (at most every 5 s; art again only if a
  part of it was lost; icons not, the deck asks for those). The deck skips a state line
  identical to its topic's last one (FNV-1a of the line, forgotten when the port opens
  or on hello): without that, each resend's keymap rebuilt the cheat sheet, the stall
  overflowed the next burst, and the resends looped every ~6 s. A loss or two right
  after the companion starts remains (the deck builds every page at once then), now
  repaired within seconds. A screenshot (`/deck` shot) stops the deck reading for
  ~2.5 s, so anything sent meanwhile is lost and resent.
- Firmware 0.12.0, companion and extension 0.2.1:
  - Terminal commands didn't reach the deck: the VS Code window still ran extension
    0.1.0 (installed side by side; a window keeps what it started with until reloaded),
    which sends each command's start and end but no history, and the list came only
    from history. The companion now builds the list from starts and ends too, and a
    history replaces it when one comes. The extension says its version in its hello,
    and the companion logs it.
  - Recent: most recent first, on request; doing one already listed moves it to the
    front (it used to keep its place).
  - Windows: the shown 8 are the most recently in-front windows, tracked per poll,
    instead of the first 8 front to back: Windows sends a minimized window to the back,
    so minimizing (a tap on the window in front) made its tile vanish. A minimized
    window's icon is dimmed. Hold = close: the deck hides the tile at once and the
    companion leaves the window out for 5 s, then shows it again if it's still open.
  - Two more themes, Ember and Violet, from the tray's Deck colors. The Up next card's
    tint, and the soft text on highlighted tiles, come from the theme now
    (`kHighlight`, `kHighlightLine`, `kHighlightText`) rather than a fixed blue.
- The simulator (`simulator/`, on request, 2026-11-08): the firmware's UI and protocol
  code compiled for Windows with MSVC, stand-ins for the board, display, USB, HID,
  Preferences and heap, a window or headless, and a TCP port in place of USB. A Python
  driver plays the companion with made-up data (`fixtures.py`), taps and drags, checks
  what the deck sends (`test_deck.py`, 67 checks) and screenshots every page per data
  set and theme (`tour.py`). What it found, fixed in firmware 0.12.1 and 0.12.2:
  - Status bar: a long meeting, Claude needing approval, a failed build and the layer
    pushed the volume and mic buttons off the screen. The pills now sit in their own
    clipped area and give way (layer, then build, then a shorter meeting) until they
    fit; a call's pill coming or going fits them again.
  - Git strip: a long branch wrapped onto three lines and pushed the buttons out of the
    card; "Commit" read "Com…"; "Last:…" stood alone when the chips left no room.
    `setTextUpTo()` keeps a label to one line (it capped the width but let the height
    grow) and can be given a new limit for the same text.
  - Mail tasks: five rows ran past the card and pushed Open Gmail off it; rows that
    don't fit are left out (the badge counts them all).
  - Audio: output and input buttons were a fixed 124 px ("Headpho…", "Micropho…", the
    third past the card's edge); they're as wide as their names now. A fader skipped a
    level from the PC while touched or just after, but still marked it seen, so the
    level was lost; at boot nothing counted as touched for the first second, so after a
    theme restart the mixer stayed blank. Skipped levels are tried again next loop.
  - A tap or drag shown before the PC confirms (mic, volume) stayed on the guess when no
    report came; it goes back to the PC's word after 1.5 s (mic) or the drag's hold.
  - No playback device (volume -1) showed "Muted" and the old slider level; it shows
    "--". Before the companion's first Google report, Today said "Calendar unavailable";
    it says "Checking the calendar…". Offline, Today kept the last track's art and the
    meeting pill (for up to 15 s), and the numpad named the last foreground app.
  - Windows: the FancyZones map showed a stray placeholder until a monitor arrived (its
    "seen" value started at the "no monitor" value); the hint was cut short.
  - Themes: the working Claude banner, the build card's running state, the Output tile
    and the numpad's operator keys were fixed blues; they follow the theme now.
  - Seen on the board meanwhile: bytes lost in the startup burst could leave an album
    art part that still parsed, shorter, which decoded fine and left a band of noise
    across the art. A part that decodes to the wrong length is a bad part now, and the
    companion sends the art again after any reported loss.
  - Not deck bugs: due dates were a day off in the simulator only (the PC's time zone
    in `mktime()`; the simulator runs in UTC like the deck).
- Remote-SSH (the work PC works on a Linux server; extension 0.2.2):
  - Installed on the server, the extension ran there, and its localhost:48151 was the
    server's: nothing reached the companion. `extensionKind` is `["ui", "workspace"]`
    now, so VS Code runs the PC's copy even in a remote window; terminal events and
    sending text work across (the terminal API reaches every extension host). Its hello
    says `remote`, and a remote folder goes as its own path; the companion names the repo
    but doesn't run the PC's git on it ("Git runs in the SSH host"). Not yet tried against
    a real SSH remote.
  - Claude Code on the server: its hooks need the companion, which is only on the PC's
    localhost. An SSH reverse tunnel (`RemoteForward 48151 localhost:48151` in the PC's
    ssh config, which Remote-SSH uses) makes the server's localhost:48151 the PC's, and
    `install/remote-claude-hooks.sh` adds the same hooks there with the server's curl
    (python3 merges the JSON, as ClaudeHooks.cs does on Windows), as one shell command
    line rather than Windows' `command` + `args` exec form, which older Claude Code
    versions may not take. Confirmed working on the work PC (2026-11-08): the extension
    installed on Windows (installing from a Remote-SSH window puts it on the server), the
    tunnel, and the hooks added on the server. The hook request then
    arrives from the PC's ssh process, whose parent is VS Code, so the Claude pill brings
    up that window. `.gitattributes` keeps `.sh` files LF in Windows checkouts.
  - Audio: Headphones come right after Speakers among the outputs (on request).
- Firmware 0.13.0 and 0.13.1, the shortcuts launcher (on request):
  - A button at the right end of the tab bar, on every page, opens a panel over the page
    with up to 12 square tiles, six to a row (an app's or site's icon, else a symbol, and
    the name on up to two lines; 0.13.1 made them square and smaller, from four wider ones
    to a row, on request), right above the button. A tap runs one and closes the panel; a tap anywhere else,
    the button included, closes it, through a see-through layer over the screen so the tap
    doesn't also reach the page. The button is a plain object, not an `lv_button`: the
    tabview counts every button in its bar as a tab. The panel grows up from the tab bar
    with the number of rows; idle closes it.
  - The list lives in the companion's settings.json (`shortcuts`), so each PC has its
    own; the tray's **Shortcuts…** opens the file, writing the defaults in first (Gmail,
    Calendar, Today's note, Spotify, VS Code, Terminal). Kinds: `url`, `app` (a Start
    menu name, so Store apps such as Spotify and Windows Terminal work without knowing
    their package ids), `path`, `action` (deck actions that need no arguments) and `keys`.
  - An app that's open comes to the front instead of starting again: its windows are
    matched by the same rules the taskbar order uses (the window's or package's
    AppUserModelID, the exe under a known folder, the exe's name in the id), then by the
    window list's app name. One started from the background could open behind the window
    in front, so the companion first takes the "last input" with a zero-pixel mouse move
    and passes the foreground on (`AllowSetForegroundWindow`), and brings the new window up
    when it appears (up to 8 s).
  - Icons: the shell's picture of the Start menu item, file or folder (premultiplied
    alpha undone), and for links the site's own `/favicon.ico` (the frame nearest 32 px,
    PNG or bitmap), fetched from the site itself, never a favicon service. Google Calendar
    has none there, so its tile shows today's date. The deck's icon store grew from 24 to
    50, and an icon a Recent or shortcut tile shows is kept like a listed window's.
  - Simulator: 19 new checks (opening, a tile's action, closing both ways without
    reaching the page, every page, 12 tiles and the 13th left out, a long name cut at two
    lines, the empty list, offline) and launcher shots in every tour.
  - Flashing: `platformio.ini` named no default environment, so `pio run -t upload` (the
    README's command) also ran the recovery env after updating the deck. That rebooted
    the deck into its ROM bootloader and then, having inherited the OTA uploader's
    `upload_command`, couldn't flash it, so the deck stayed in the bootloader until an
    esptool reset. `default_envs = deck` now, and `deck-esptool` really uses esptool.

- Firmware 0.14.0, the context bar (on request, after a mockup; "a flex-bar like bar…
  with actions available on the current window focused"):
  - A strip above the tab bar on every page but the Numpad, with up to 8 keys for the app
    in front, in that app's color: strongest behind its icon and name at the left, fading
    across the bar (option A of two mockups; the other colored each key by its kind). The
    pages give up 54 px for it (the tabview content's bottom padding), and take it back
    while there's no bar (offline, the Numpad, `"contextBar": false`).
  - Built-in sets for VS Code, Chrome and Edge, Windows Terminal, Obsidian, Teams (another
    set during a meeting, using the call panel's own controls) and Discord (asked for while
    it was being built); Copy, Paste, Undo, Save, Find and Close for anything else. An app
    without a color of its own gets its icon's main hue.
  - A key presses its shortcut in the bar's window: the desktop, the taskbar or a menu
    taking the front keeps the last app's keys, and a key brings that window back first.
  - Icons: a 15 px icon font generated from the FontAwesome file LVGL ships
    (`scripts/gen_font_icons.py`, 72 icons by name), since LVGL's built-in symbols lack
    search, split, history and the rest. "split-down" is "split-right" turned a quarter.
  - Keys are as wide as their icon and name plus an even share of the rest, so "Find"
    makes room for "Commands"; if they can't all fit, their padding tightens, then only the
    widest are cut ("Mute" had become "M…" when every key was cut evenly).
  - The pages, 338 px tall with the bar: Today's mail line became a pill in the Recent
    card's heading; Audio's art is drawn at 128 px; window tiles have the icon beside the
    name; Obsidian's recent notes, Dev's terminal rows and the zone map refit whenever the
    page's height changes (they had been laid out for the taller page).
  - Simulator: on a crash it prints the stack with file and line (DbgHelp and the build's
    .pdb). That found the one bug of the round: `fillRow()` set a label's long mode on a
    plain box (a Windows tile's text column), writing past the box, so the simulator
    crashed now and then at start-up deep in LVGL. `fillRow()` now leaves anything but a
    label alone. 14 new checks (100 in all), including that no page runs into the bar.

- Firmware 0.15.0, the context bar arranged on the deck (on request: "long pressing a
  button should unroll a small menu above of actions to choose from", rather than editing
  and rebuilding to reorder VS Code's keys or swap Build for Go to symbol):
  - Each app has a bank of actions: VS Code 21 (Go to symbol, Go to line, Run, Definition,
    Rename, Format, Comment, Quick fix, Source control…), the browsers 19, Terminal 18,
    Obsidian 13, Teams 8 (14 in a meeting), Discord 16; and for every app the everyday 22
    (cut, redo, select all, snap left/right, maximize, task view, screenshot, emoji, desktop,
    lock…), less any the app has under the same name. The defaults are the earlier sets.
  - A long press on a key opens a menu above the bar: the app's actions, then
    "Everywhere", five across, scrolling past the status bar; this key's action filled in
    the app's color, the others on the bar outlined. Picking one puts it in that place; one
    already on the bar swaps places with it, which is how keys are reordered. Remove and
    Reset (the app's defaults) are in its heading; "+" at the end of the bar adds a key
    while there are fewer than 8. A tap still runs a key (short click; the long press
    doesn't).
  - The companion owns the layouts, per app key (the exe, or "ms-teams:call" in a
    meeting), in `context-bar.json` beside settings.json, so they survive restarts and
    belong to that PC. An edit names the app the deck's bar showed and is ignored if the
    bar has moved on. settings.json's actions join the app's bank and are its default bar.
  - The list ignored drags that started between items (a `box()` takes no presses, so the
    press went to the menu, which doesn't scroll); it takes them now.
  - Checked: the placement rules and the saved file from the companion's own code against
    the real window in front; 15 new simulator checks (115).

- Firmware 0.16.0, companion and extension 0.3.0: Claude on the deck, and better keys per
  app (on request: "claude should have an approve button, model and effort buttons, 5hr
  usage status… think of other things that are relevant for each app, as well as the
  explorer one", and "all of the claude features to work on the claude in the vs code
  extension as well"):
  - Approvals through Claude Code's PermissionRequest hook, the one way to answer a
    prompt from outside that works the same in the terminal, the Claude app and VS Code.
    The hook (sync, through `cmd /c curl … || exit /b 0` so a stopped companion ends it
    quietly) posts to `/claude/permission`, which the companion holds open until the deck
    answers, Claude moves on (the tool's PostToolUse, the turn ending) or 575 s pass.
    Claude's own prompt shows meanwhile: two remote-approval tools and a Claude Code
    feature request describe exactly that ("whichever answers first"), which the docs don't
    say outright. "Always" sends Claude's own `permission_suggestions` back as
    `updatedPermissions`. A banner on every page, under the status bar; the old "needs your
    approval" toast only for requests the deck can't answer. Checked end to end with a
    made-up request against the real companion and deck (allow with the rule, and deny).
  - Model and effort: hooks report effort on tool events (`effort.level`) and the model at
    SessionStart and in the new PostModelSwitch hook (`to_model`); the bar's Model and
    Effort keys show the session's in the front window's app, and open a list whose choice
    is typed as `/model …` or `/effort …`. In VS Code, Ctrl+Esc toggles between the editor
    and Claude, which could have typed into code, so the extension runs
    `claude-vscode.focus` first.
  - Usage: Claude Code gives the 5-hour figure only to terminal status lines, not to hooks,
    the app or VS Code; the key opens the usage page instead of showing it. Reading it
    from Claude's sign-in files is out of bounds.
  - VS Code's keys run VS Code commands through the extension (0.3.0, `command` messages,
    limited to workbench, editor, Claude Code and Markdown commands), so they work with any
    keybindings and in Remote-SSH windows; an older extension gets the keys.
  - Explorer: the open folder, from the shell's window list (the tab whose name starts its
    window title), for Terminal here, VS Code here, Claude here and Copy folder.
  - Browsers: Gmail, YouTube and Google Calendar get their own keys and color by the tab's
    title (a layout of their own, "chrome:gmail").
  - New key kinds: `text` (typed, "\n" for Enter) and sequences ("g, i").
  - 15 new simulator checks (130).

- Companion 0.16.1 (firmware 0.16.0 and extension 0.3.0 unchanged): Claude's keys fixed
  (reported: "The model button doesnt work. it also doesnt show the current model", "The
  usage button doesnt show the 5hr usage precentage and pressing the button should open
  claudes "/usage" not the website", and "claude didnt say that claude needed me"):
  - The model, shown from the start: hooks name it only at SessionStart (not always) and on
    a switch, so a session started earlier showed "Model", and tapping it crashed on the
    empty name. The companion now reads it from the session's transcript (every hook gives
    `transcript_path`): the last 256 KB, the last main-conversation reply's `model`, cached
    by the file's length; a switch a hook reported after that reply wins until the next
    one.
  - Usage on the key: Claude Code gives the 5-hour figure only to terminal status lines,
    not to hooks, the app or VS Code, and keeps no copy of it. With the user's OK ("Yes,
    read the token"), the companion reads the sign-in token from
    `~/.claude/.credentials.json` and asks the endpoint `/usage` reads (`GET
    api.anthropic.com/api/oauth/usage`, `anthropic-beta: oauth-2025-04-20`; undocumented)
    every 4 minutes and 20 s after a turn ends: "5h 42%" on the key. The token is never
    logged, kept or refreshed by the companion: renewing it itself would rotate the refresh
    token and could sign Claude Code out.
  - On this PC the file's token had expired two days earlier: the Claude app signs in on
    its own, and only Claude Code in VS Code or a terminal renews the file, so the key
    stayed blank (reported: "the usage button doesnt show it"). For an expired token the
    companion runs Claude Code itself, headless: `claude -p /usage
    --no-session-persistence --settings {"disableAllHooks":true}`, at most every 20
    minutes, from the temp folder, without the CLAUDE*/ANTHROPIC* variables of whatever
    started the companion. It makes no model call, keeps no transcript, shows no session
    on the deck (hooks off), prints "Current session: 67% used · resets …", and renews the
    token the way Claude Code always does, so the endpoint works again after it. Its
    executable: `~/.local/bin/claude.exe`, else the newest one in VS Code's Claude
    extension, else npm's. When Claude Code's own sign-in ends (the file's
    refreshTokenExpiresAt; on this PC three days after this change), the log says to run
    `claude auth login`.
  - A tap on Usage types `/usage` into the session (like Model and Effort; VS Code focuses
    Claude's input first) instead of opening the usage web page.
  - "Claude needs you" for questions too: AskUserQuestion and ExitPlanMode are "input"
    from their PreToolUse to their PostToolUse; a turn whose end-of-turn wrap-up asks
    something (the Claude app's tool, seen in PreToolUse) ends as "input", not "done". The
    toast says "Claude has a question" for those.
  - Checked: the model and the usage figure against this PC's Claude sessions, on the
    simulator's bar ("5h 69%"); a made-up question session showed "Claude needs you".

- Companion 0.16.2 and extension 0.4.0 (firmware 0.16.0 unchanged): Claude's bar in VS
  Code, and Remote-SSH sessions placed in their window (reported from the work PC: "it
  still doesnt recognise that i am in a claude code chat in the vs code remotely connect
  on my work pc, so it doesnt show the claude bar"):
  - VS Code had one bar, VS Code's, with a few of Claude's keys in it; nothing switched to
    Claude's. The extension now reports whether Claude Code's chat tab is in front: the
    active tab of the active editor group is a `TabInputWebview` whose viewType has
    `claudeVSCodePanel` (Claude's own keybindings use `activeWebviewPanelId ==
    'claudeVSCodePanel'`). It sends `{t:"claude", front}` on tab and group changes, and
    `claude` in `hello`. The tab API is the workbench's, so a Remote-SSH window works the
    same though Claude Code's extension runs on the server.
  - Then the bar is "Claude Code" (layout key `code:claude`), in Claude's color: Approve,
    Deny, Model, Effort, Usage, New chat; its list adds Stop (Esc), Claude's input, Accept
    and Reject changes, Another chat, Reopen chat, Focus view, then VS Code's keys.
  - Not seen: Claude in VS Code's side bar. Claude sets `claude-vscode.sideBarActive` for
    that, but extensions can't read another's context keys or tell which view has focus.
    Claude's default is the editor tab (`claudeCode.preferredLocation: "panel"`).
  - Remote sessions and their window: the hooks post through the SSH tunnel, so the
    connection's process is ssh.exe, whose parents may not lead to VS Code (Remote-SSH can
    run ssh from a detached local server). A session whose `cwd` is a server path ("/…")
    now goes to the VS Code window with that folder open first (`VsCodeHub.HasFolder`),
    VS Code's main process, which owns all its windows; then the connection, as before. A
    local session the connection didn't place falls back to the folder too.
  - The model for a remote session: its transcript is on the server. The server's async
    hooks (install/remote-claude-hooks.sh) now read the last main-conversation reply's
    `model` there (the last 256 KB, as the companion does locally) and send it as an
    `X-Claude-Model` header; the companion takes it if it looks like a model id.
  - The reply's model now counts from when the companion first sees it change (on each
    hook), not from the transcript's write time: a `/model` switch followed by a prompt
    wrote to the transcript and let the old reply's model win again.
  - Checked: a test program named Code.exe, loading the built companion by reflection,
    with its own hidden window as VS Code's: VS Code's bar, then Claude's (Approve, Deny,
    Haiku 4.5, High, 5h 12%, New chat) and back; a made-up remote session placed by folder,
    its model from the header, a switch winning until the next reply, a bad header ignored,
    another folder not placed (20 checks). The server hook command generated by the script
    and run in sh against a test listener: the model from a made-up transcript, skipping a
    subagent's reply and a quoted model in a tool's input; no header without a transcript.
    Not checked in a real VS Code window: VS Code isn't on this PC's screen to test with.

- Companion 0.16.3 (firmware 0.16.0, extension 0.4.0 unchanged): the Usage key for Claude
  Code on a Remote-SSH server (reported from the work PC: "the usage isnt coming"; the
  Claude session on the server found that no hook payload carries the figure):
  - The companion reads usage on the PC, from Claude Code's sign-in there; on the work PC
    Claude Code runs on the server, so the PC has none, and hooks never carry the figure
    (only a status line's input has `rate_limits.five_hour`).
  - A status line was the server Claude's suggestion, but a project's own `statusLine`
    replaces a user one (that repo has one), so it wouldn't run there.
  - Instead `remote-claude-hooks.sh` writes `~/.claude/flex-screen-usage.sh`, run by the
    SessionStart and Stop hooks (async, 120 s): at most every 3 minutes (a stamp file
    beside it), Claude Code's own `/usage` headless, as the companion does on the PC
    (`-p /usage --no-session-persistence --settings {"disableAllHooks":true}`, from /tmp,
    without the session's CLAUDE*/ANTHROPIC* variables except CLAUDE_CONFIG_DIR). The
    executable: `$CLAUDE_CODE_EXECPATH` (Claude Code sets it for what it runs: the binary
    running the hook), else `claude` on the PATH, `~/.local/bin/claude`, or the newest in
    `~/.vscode-server/extensions`. It posts `{"five_hour", "seven_day"}` to
    `/claude/usage` through the tunnel. No token is read by our code there.
  - The companion shows a server's reading and holds off its own while they keep coming
    (15 minutes), so two accounts' figures don't take turns. The label goes back to
    "Usage" once the last reading is 5 hours old.
  - Checked: the script installed into a scratch home; its Stop hook's usage command run
    as Claude Code would, here in Git Bash with this PC's Claude Code: /usage took 3.3 s,
    the companion logged "usage 5h 90% (from a server's Claude Code)"; run again at once,
    it stopped at the 3-minute stamp. Not checked on the work server itself.

- Firmware 0.16.4 and companion: the mixer usable again under the context bar (reported:
  "The mixer is a bit slow to response, and due to the new bar, its a bit unusable. the
  faders are way too small"):
  - With the bar under the page, each channel's value, fader, name and mute icon left
    the fader ~25 px of travel for 0–100, four steps a pixel, with a 36x14 knob. Now the
    app's name is its mute button (amber while muted; the level shows the mute sign and
    the fader's fill greys out), the "MIXER" heading row is gone, the device rows are
    36 px (were 40) and the knob is 40x20: ~100 px of travel.
  - The Now Playing card's shuffle and repeat buttons are side by side: stacked beside the
    128 px art, the second was cut off.
  - The companion handled each fader message on the thread that reads the deck's link,
    reading every output's sessions before taking the next message. That's ~3 ms on this
    PC, but more with many apps and devices, and a drag sends one every 60 ms. Now a
    move sets the app's level and wakes the mixer thread, which applies the latest level
    once; the link thread doesn't wait (0 ms, was 3–4 ms here).
  - The simulator's default port, 48160, fell inside a port range Windows had reserved
    (48098–48197; Hyper-V/WinNAT reserve ranges at times): `FLEX_SIM_PORT` picks another
    for the Python tools.
  - Checked: on the simulator, all 130 checks (the Teams name mutes it; a drag on
    Spotify's fader sets it: 10 levels over a 0.6 s drag); the companion's mixer timed by
    loading the built companion, setting an app to the level it had. Not flashed: the
    deck wasn't on this PC.

- Companion: the layer pill and cheat sheet follow whichever Corne is in use (reported:
  two Cornes plugged in, the deck only followed one of them):
  - Both answer VIA on raw HID: W-CORNE (the wireless receiver, 55D4:0461) and
    Cornelious RP2040-Zero (wired, FEED:C0E1), same Vial keyboard id, 8 layers, 4x12. The
    companion opened the first that answered and stopped looking.
  - Now every VIA keyboard is opened (new ones looked for every 5 s) and polled, each
    with its own held keys, toggles and default layer, so a layer toggled on one
    stays with that keyboard. The deck shows the one a key last went down on: its
    cheat sheet (when the layout differs), then its layer. The other's state is
    kept, so switching back shows its layer straight away. If the shown one goes, the other
    takes over.
  - Cornelious is Vial-locked (W-CORNE is unlocked), and a locked Vial keyboard answers
    the held-keys query with no keys held, so it can't move the layer until it's unlocked
    (the Vial app's Security > Unlock, holding r0c0 + r3c9; it stays unlocked until it
    restarts). No firmware change is needed for it. The companion logs the lock state
    when it opens a keyboard, and checks a locked one again every 30 s.
  - Checked: the built companion's log with both plugged in (both opened, Cornelious
    reported locked, W-CORNE shown); the simulator with the two keyboards' kbd and
    keymap messages in turn (pill NAV then BASE, cheat sheet after a 10 s hold). Not
    checked: typing on Cornelious once it's unlocked. Deck firmware unchanged.

- Companion and deck firmware: how long a layer key is held before the cheat sheet shows
  is a setting, and the tray opens the settings file (asked for: "a configure option to
  open the configuration/settings file, and one of the configurations should be the time
  to present the cheat sheet"):
  - settings.json's `cheatSheetSeconds` (default 10, 0 to 600; 0: the sheet only opens
    from the layer pill) goes to the deck in the `settings` message as `cheat_hold_s`,
    which replaces cheat.cpp's fixed 10 s. A companion that doesn't send it leaves 10 s.
  - The tray's **Open settings file** writes in the everyday settings the file doesn't
    have (palette, idle, layerNames, cheatSheetSeconds) at their current values, so
    they're there to change, then opens it in the app set for .json (Notepad if none).
    A file with a mistake in it is opened as it is, to be fixed. Writing the file drops its
    comments, as the Deck colors menu already did.
  - Checked: AddMissing run against this PC's settings.json (only `palette` in it) wrote
    the four, a change to 3 read back as 3, then the file was put back; the simulator's
    133 checks, with new ones for a 6 s setting (not at 2 s, shown at 6.5 s) and 0 (not
    after 60 s). Firmware 0.16.5 (the phone over Bluetooth, meant for
    0.17.0, was taken out again: Gadgetbridge didn't pass the phone's notifications on).

- Tasks on Capture, firmware 0.17.0 (asked for: "a task manager integrated with my google.tasks.
  Needs to pull tasks from several places, one being Google Tasks itself … The AR from mail
  should be gone … see all tasks, mark them finished, and a button to create tasks"):
  - The mail action items are gone: Capture's *From mail* card (and its Open Gmail
    button), Today's "n to do from mail" pill, and the `tasks` field of the `google`
    message. GoogleSource reads only the calendar now and lends its sign-in
    (`SendAsync`) to the task source; the consent already covered Tasks read and write,
    so nobody signs in again.
  - Companion: `TaskSource` merges `ITaskProvider`s (id, name, read, tick, add; a task is
    source, list, id, title, notes, due, done, parent). `GoogleTasks` is the first: every
    list, open tasks with paging, subtasks, PATCH to tick, POST to add. Read every 2 min,
    right after each change, and on the deck's ↻. Tasks ticked from the deck stay, ticked,
    until midnight, so a wrong tap can be undone. A failed tick sends the snapshot again
    with a new `rev`, since the deck skips a state line it has just seen and would keep
    its own early tick.
  - Deck: a Tasks card on Capture, where the mail card was (a tab of its own was tried
    first and turned down: "I do not want another tab.. it should be in the capture tab").
    The clipboard (236 px) and Obsidian (186 px) cards got narrower to make room. Header
    with the open count, refresh and +; the lists as chips with open counts; the tasks
    grouped Overdue / Today / Tomorrow / This week / Later / No date / Done, each row with
    its list (in All), day and the notes' first line; the whole row ticks it. + opens a
    full-screen form with LVGL's keyboard, list chips (starting on the list on show) and
    due chips (none, today, tomorrow). Today's pill opens Capture.
  - Checked: the simulator's 147 checks (new: grouping, filters, tick/untick, refresh,
    the form typing on the keyboard, Add with list and due, Cancel, signed out); on the
    deck, the user's 24 open tasks shown, a task added through the companion's `/test`
    hook appeared under Today in Projects, ticking it marked it completed in Google
    Tasks (the test task was deleted after). Not checked: tapping on the real panel. The line is
    capped at 40 tasks and 7000 bytes (Hebrew is escaped six bytes a letter).
- Claude's questions off the approval banner (companion only, firmware unchanged)
  - Reported: "when I answered a question for claude, the approbe deny notification still
    stays". Claude Code runs the PermissionRequest hook for AskUserQuestion too, so the deck
    showed Approve/Deny for a question. Answering at the PC adds the answers to the tool's
    input, so PostToolUse's input no longer equalled the request's and nothing settled it
    until the turn ended (or 575 s passed).
  - Now AskUserQuestion is never held for the deck (Approve can't pick an answer): the
    session's "input" state gives the pill's *needs you* and the "Claude has a question"
    toast instead. And a request settles when every field it asked about matches what the
    tool ran with, so extra fields added at the PC (a question's answers, a plan's file)
    don't keep it up.
  - Checked against the running companion with made-up hooks: an AskUserQuestion request
    returned at once (204); an ExitPlanMode request went as soon as PostToolUse came with
    an extra input field, and the deck showed no banner after.
- Scheduled and chosen lists on the Tasks card, firmware 0.17.1 (asked for: "i want the
  option to only have the scheduled and stared and chosen list to have there"):
  - The card opens on **Scheduled** (every open task with a due date, from any list), in
    place of All; then a chip for each list chosen in the tray's new *Task lists on the
    deck* menu (settings.json `hiddenTaskLists`, "src/id"; new lists show until unticked).
    An unticked list's undated tasks aren't sent; its dated ones still are, for Scheduled.
    `tasks` gives each list `show` and a `sched` count; Today's pill counts what's sent.
  - No Starred: the Tasks API has no starred field (checked in its discovery document),
    so the user chose to leave it out.
  - + from Scheduled starts on the first shown list, due today, so the new task shows
    where it was added. A list unticked while on show sends the card back to Scheduled.
    `tasks` goes again whenever settings.json changes, so editing `hiddenTaskLists` by
    hand works too.
  - Checked: the simulator's 154 checks (new: Scheduled leaves out undated tasks, a
    hidden list has no chip but its dated task shows, + from Scheduled adds to the first
    list due today, a list hidden while on show goes back to Scheduled); on the deck,
    firmware 0.17.1 opened on Scheduled (4 open), and putting the first list in
    `hiddenTaskLists` took its chip away while its dated tasks stayed (then put back).
    Not checked: clicking the tray menu itself, tapping on the panel.
- A task's popup and plainer Scheduled rows, firmware 0.17.2 (asked for: "The scheduled
  tasks should only show the task name, date due and status, with days overdue if
  overdue. clicking a task should pop up the contents of it."):
  - Scheduled rows: the title, then "Fri 6 Nov  ·  2 days overdue" (or "To do", "Done");
    no list name or notes. A list's own rows keep the day and the notes' first line.
  - Tapping a row opens the task over everything: the whole title, list, due day and
    status (amber when overdue), the notes (scrolling when long), **Mark done** / **Mark
    not done** and **Close**; a tap beside it closes it. The box at the row's start is
    now its own 44 px button that ticks without opening.
  - The notes come only when asked (`task.open` → `task`), so the `tasks` line keeps its
    40 tasks: the companion sends the whole title and the notes without the todo tag,
    up to 1500 characters and under 7000 bytes. "Loading…" until they arrive.
  - Checked: the simulator's checks (new: Scheduled rows, the box ticks without opening,
    a tap opens the popup and asks for the notes, title and notes fill in, Mark done,
    overdue status, no notes, tap beside and Close); on the deck, firmware 0.17.2 shows
    "Sun 15 Nov · To do" rows under Scheduled, and the companion took a `task.open` for
    a real task through `/test`. Not checked: the popup on the real panel (it needs a tap).
- Claude talks to the deck, firmware 0.19.0 (picked from the ideas list: "lets start with
  1"):
  - The companion serves an MCP server on `/mcp` (Streamable HTTP, stateless, JSON-RPC by
    hand: no SDK for three tools). `deck_ask` holds the call until the tap; `deck_notify`
    and `deck_progress` return at once. The server's `instructions` (which Claude Code puts
    in the system prompt) say the tools are for when the user may be away, not for chat.
  - Claude Code ends an MCP call after 60 s unless the server's own entry sets `timeout`
    (read from its bundle: the per-server field overrides `MCP_TOOL_TIMEOUT`, and progress
    notifications don't extend it). `--install-hooks` registers the server with
    `claude mcp add-json --scope user` and `"timeout": 3600000`, rather than editing
    `~/.claude.json`, which Claude Code rewrites constantly. Its fetch also gives up
    waiting for the first byte, so a question's answer is an event stream opened at once
    with a comment every 10 s; a failed write is how the companion learns Claude stopped
    waiting (Esc), and the question leaves the deck.
  - The tools are allowed with `mcp__flex-screen` in `permissions.allow`: otherwise each
    first use would raise an approval banner for showing something on the deck.
  - A tool call doesn't say which session made it. Its PreToolUse hook does (same tool and
    arguments, matched within 1.5 s; the hook is asynchronous and can come after the
    call); without hooks, the only session at work. That gives the card its project and
    the progress its session.
  - Claude's own AskUserQuestion: its PermissionRequest hook answered with
    `{"behavior":"allow","updatedInput":{…, "answers":{"<question>":"<label>"}}}` (labels
    joined with ", " for several), the shape the Agent SDK documents for answering it.
    Only choice questions with 2-4 options go to the deck; text or number questions stay
    at the PC. It replaces 0.16.x's rule that questions never wait for the deck.
  - The deck shows one card under the status bar (under the approval banner when both
    show): the newest question, else the newest note. Notes keep for 30 minutes, three
    at most.
  - The "Claude has a question" toast now waits 1.5 s: the question's PreToolUse hook
    arrives just before its PermissionRequest, and the toast came 80 ms ahead of the card.
  - Checked: 23 new simulator checks (223); headless `claude -p` calling `deck_ask`,
    answered through `/test` 75 s later ("The user tapped "Beta" on the deck."), with the
    project told by its hook; `deck_progress` and `deck_notify` through the registered
    server, the note photographed on the real deck; an AskUserQuestion in the Claude app
    answered from the deck's side through `/test` (it returned "Blue"); made-up hooks for
    the toast. Not checked: tapping the card on the real panel, and Remote-SSH.
- Claude session board, firmware 0.20.0 (the next of the picked ideas: "Yes, session
  board"):
  - An overlay, not a seventh page (the tab bar is full): the status bar's Claude pill
    opens it (it used to bring up the top session, which a row does now), and so does the
    Dev page's banner (its Open button still brings up the top session).
  - Up to 8 sessions (was 4). The app comes from the window's process the hooks already
    find (`claude` → Claude app, `Code` → VS Code, terminals → Terminal; a server's path
    adds "SSH"); the model from the session's last reply, else what a hook named.
  - Stop: keys can't aim at one session (the Claude app shows one of several; Esc goes to
    whatever is focused), so a second PreToolUse hook, synchronous, asks `/claude/stop`
    before every tool call and gets `{"continue": false}` once the deck asked. Measured
    66 ms a call through `cmd /c curl` (35 ms for curl alone; cmd keeps a stopped
    companion quiet). It stops before the next step, not mid-reply. Hooks are read when a
    session starts, so sessions already open don't have it.
  - Usage burn-down: the companion keeps each reading of the 5-hour figure for the current
    window (a new reset time, or the figure falling, starts over) in
    `usage-history.json`, so the chart survives restarts. The pace is the last hour's
    readings (at least 10 minutes apart); below 0.5% an hour there's no forecast. The deck
    draws the window in 5-minute steps (an LVGL chart): readings carried forward to now,
    the pace ahead in grey to 100%.
  - Checked: 21 new simulator checks; on this PC a headless Claude run told to run six
    6-second commands (36 s at least) stopped through `/test` at 16 s and ended at 25 s,
    2.7 s after the request, at its next step; the usage history filled from real
    readings (57%, 58%). Not checked: the board on the real panel (it opens with a tap),
    and Remote-SSH.
- App integrations as modules, companion only (agreed 2026-11-15 on outside advice: make
  the app integrations modular before adding more features; the task sources went first):
  - Each app the deck knows is an `IAppIntegration` in `companion/Integrations/<App>/`, as
    each task source is an `ITaskProvider`. It gives its detection (the exe names of its
    windows), its context (what the window is doing that has keys of its own: Gmail,
    YouTube or Google Calendar in Chrome and Edge by the tab's title, Claude's tab in VS
    Code as the extension says, a meeting in Teams or Zoom), its actions (the bank and the
    default keys per context, what a key shows right now, and how its own kinds of key
    run: VS Code commands through the extension, Explorer's open folder, Claude's Model
    and Effort lists and slash commands) and its state (the deck topics it feeds, for the
    record: the log lists them at start).
  - Eleven so far: VS Code, the Claude app, Windows Terminal, Chrome and Edge (one
    `BrowserApp`, with `WebSites` for Gmail, YouTube and Calendar), Explorer, Obsidian,
    Spotify, Teams, Zoom, Discord. Shared keys: `ClaudeKeys` (Approve, Deny, Model,
    Effort, Usage, wherever Claude Code runs) and `MeetingKeys` (next meeting, Join, Mute,
    Camera, New meeting). A new app is a folder and a line in `AppIntegrations.All`.
  - `ContextBar` keeps what every app shares: the everyday bank, settings.json's
    `contextBar`, the layouts arranged on the deck and their editing, and pressing keys,
    typing text or closing the window. It asks the integration of the window in front for
    the rest.
  - Calls: `CallSource` moved to `Integrations/Meetings/` and `ZoomControl` to
    `Integrations/Zoom/`; Teams' local API client is its own class (`TeamsApi`, in
    `Integrations/Teams/`), and so is the Alt-shortcut sender for Zoom's meeting window
    (`ZoomShortcuts`). They work as before.
  - Nothing changes for the deck: the same keys and ids, the same bar keys
    ("chrome:gmail", "ms-teams:call"), so layouts saved in `context-bar.json` load as
    before, and the same messages. The data sources (`ObsidianSource`, `VsCodeHub`, the
    Claude ones) stay where they were for now.
  - Checked: the companion builds with no warnings. A throwaway harness, compiled once
    against master's sources and once against these, printed the same 10,600 lines byte
    for byte: every app's bar under several window titles and 18 call, meeting, mic and
    Claude states; settings.json variants; saved layouts; and what each key in each bank
    does when tapped (aimed at a window that doesn't exist, so nothing was typed). The
    simulator's checks pass. Not checked: on the real deck, and with live Teams and Zoom
    calls (that code moved without changing).
  - Found on the way and left as it was (this change keeps behaviour): a `contextBar`
    entry written as a plain list of keys (`"Chrome": [ … ]`, as the README shows) throws
    when the bar reads its color, so that app's bar can't be made.
- Updates across PCs, firmware 0.21.0 (picked from the ideas list: "Self-update across
  PCs"). The deck moves between PCs, each move risked a companion and firmware that don't
  match, and flashing needed PlatformIO on that PC:
  - The deck's firmware travels in the install kit (`firmware\deck.bin` next to the
    companion, with `firmware.json`: version, size, SHA-256) and the companion sends it
    over the link it already holds, with the protocol `ota_upload.py` has used since phase
    1. No esptool, no ROM bootloader, no second program wanting the COM port: the
    bootloader route is the one that wedged the chip's USB in phase 1, and it's only
    needed for a deck that can't run flex-screen at all, which needs a person at the board
    (BOOT + RESET) anyway. So esptool isn't bundled; the recovery steps point to
    `-e deck-esptool` on a PC with PlatformIO. The app image is all an update writes:
    bootloader and partition table stay as first flashed, so a change to either would
    still need that route.
  - Offered when the deck's `hello` names an older version than the kit's: a balloon from
    the tray icon, a menu item, and a toast on the deck whose tap starts it. A deck with
    newer firmware than the companion knows makes the companion look for a newer release.
  - Never a half-written deck: the companion checks the image before sending (SHA-256 per
    `firmware.json`, ESP32 image magic, the ESP32-S3 chip id, ESP-IDF's app description),
    holds back every other message meanwhile (state pushed during flash writes could
    overflow the deck's buffer), checks each ack's byte count, and sends the image's MD5,
    which `Update.end()` checks along with the image's checksum and SHA-256 (read back from
    flash) before it switches slots. Until then the old firmware is the boot slot, so any
    failure on the way (an `ota_error`, a timeout, an unplug) changes nothing; the deck
    gives up an update that goes quiet for 20 s.
  - A new firmware that can't start: Arduino-ESP32's bootloader has app rollback on
    (`CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE`), but Arduino marks a new image good as soon as
    it starts. 0.21.0 overrides `verifyRollbackLater()` and keeps itself only when the
    companion's `sync` arrives (or after 10 minutes up), so a firmware that crashes, or
    boots but can't talk over USB, is undone by the next restart or replug. This guards
    updates *to* 0.21.0 and later; checked in the linked image (`verifyRollbackLater`
    returns 1, from `main.cpp.o`), not yet by booting a bad image.
  - The deck shows a progress screen from `ota` to the restart (the loop stalls while
    flash is written, and a frozen page invites an unplug). 0.20.0 has none, so the
    companion also sends a toast first.
  - The companion: `build-dist.ps1` stamps it with the release's version (default the
    firmware's; `-Version` for PC-only releases, as 0.16.1 was), and it compares that
    with GitHub's latest release (the repo is public, so no sign-in; 60 checks an hour per
    address, and it looks a minute after starting and every 6 hours). A newer release
    with `flex-screen.zip` attached (it's attached by hand after publishing, so one
    without it isn't offered yet) is offered the same way. On OK it downloads the zip,
    checks its size and GitHub's SHA-256 digest, unpacks it, checks the kit's companion
    says it's that version (else an update would offer itself forever), and starts a small
    script that waits for the companion to quit and runs the kit's own `install.ps1`, so
    the install is exactly install.cmd's. If that fails, the script starts a companion
    again; a marker file tells the next companion how it went. A build from the repo
    (`0.0.0-dev`) never looks.
  - For testing: `FLEX_SCREEN_DECK=tcp:…` points the companion at the simulator, whose
    OTA now works in memory; such a companion runs beside the real one (its own instance
    lock, port 48152). `FLEX_SCREEN_UPDATE_FEED` points the update check at another URL;
    `/test` takes `fw.update` and `update.check`.
  - Checked: 15 new simulator checks (the progress screen and its version and percent,
    taps held off, an out-of-order part, an update that stops coming given up after 20 s,
    a non-image refused, a whole image taken and the restart). `build-dist.ps1` made a
    73.5 MB kit with companion 0.21.0 and firmware 0.21.0. End to end on this PC, beside
    the installed companion: a 0.20.9 companion talking to the simulator found a v0.21.0
    release on a local stand-in for GitHub's API, asked (the dialog answered through UI
    Automation), downloaded and checked the real kit, quit, and the kit's installer (a
    stand-in that installs into a test folder instead of the real one) started 0.21.0,
    which said "updated from 0.20.9 to 0.21.0" and cleared the download. That companion
    then sent the real 1.35 MB image to the simulator in about 5 s; with its firmware
    marked 0.21.1 it reported the deck coming back on 0.21.0 ("didn't start"), marked
    0.21.0 it reported success, and with the simulator killed mid-update, "nothing
    changed". The firmware builds; its rollback hook is the one linked.
  - Not checked: an update on the real deck (it was on the other PC): the USB link's
    speed, the progress screen on the panel, 0.20.0 taking the image with an MD5 it
    ignores, and the rollback after a bad image. Nor the real `install.ps1` run by the
    update (the test used a stand-in, so this PC's install stayed as it was), the tray
    balloons and menu by hand, or the download from GitHub itself (the release has no
    0.21.0 kit yet).
- More task sources, firmware 0.22.0 (built as 0.19.0 on its own branch, which was left
  unmerged while 0.19.0-0.21.0 shipped from master; merged on 2026-11-17 when the deck
  showed no daily-note tasks. Asked for, after outside advice to keep sources
  modular and details on demand: Microsoft To Do, "Jira. will be needed on my work pc",
  GitHub issues, "Obsidian Tasks - extract the tasks from the last daily notes"; Jira and
  GitHub "could show their project, priority and status"):
  - Each source is one `ITaskProvider` class; `TaskSource` and the deck know nothing of
    them. A source not set up on a PC reads as `setup` and is left out, so home (Google,
    To Do, Obsidian) and work (Jira, GitHub) run the same companion. Status: `ok` if any
    source read, else the likeliest reason (`error`, `signin`, `setup`) and `from`, the
    source it's about, for the deck's message.
  - A task carries `Url`, `Key`, `Project`, `Priority`, `Status` and `Summary` (the row's
    line, chosen by its source). A list says whether it takes new tasks (`add`) and whether
    its tasks tick off from the deck (`tick`).
  - Jira and GitHub are look-and-open only (asked: "View and open only"): no box on their
    rows, no Mark done, not in the New task form's lists; the popup's **Open in Jira**
    (`task.link`) opens the issue's page. Jira: Cloud (email + API token, `/rest/api/2/
    search/jql`) or Server/Data Center (personal access token, `/rest/api/2/search`),
    `assignee = currentUser() AND statusCategory != Done` by default. GitHub: the search
    API (`is:open is:issue assignee:@me`), github.com or Enterprise, a token or the GitHub
    CLI's; priority and status from labels, due from the milestone.
  - Microsoft To Do: Graph with the user's own app registration (as for Google), a
    loopback PKCE sign-in (`OAuthLoopback`), every list, tick and add (not into Flagged
    email), dates asked for in this PC's time zone so they don't slip a day.
  - Obsidian: only the latest daily note (asked: "Latest note only", for notes that carry
    the open tasks over), found by trying the Daily notes plugin's file name for each day
    back from today. `[ ]` and `[/]` are open; subtasks by indent, indented bullets as
    notes, the heading as its project, the Tasks plugin's 📅/⏳ and priority. A task's id
    is a hash of its text (without box and ✅ date) and which copy of that text it is, so
    ticking keeps it; ticking rewrites the box (and adds the Tasks plugin's ✅ date when
    that plugin is on). New tasks go at the end of that note.
  - Details stay on demand: the `tasks` line only gains `add`/`tick` per list and `from`;
    `meta` and `link` come with `task` when a task is opened. The deck holds 12 lists
    (was 8) for several sources' chips.
  - Tokens are DPAPI-encrypted per source (`Secrets`), never logged or sent to the deck;
    the tray's Task sources menu checks a Jira or GitHub token before keeping it. The
    task messages moved out of TrayApp into `TaskMessages`.
  - Checked: companion and simulator build; the simulator's checks (new: an issue's row
    has no box and a tap there opens it, its popup has no Mark done and shows the source's
    line and Open in Jira, which sends `task.link`; the issues list's + offers only lists
    that take tasks; a daily note task ticks; the signed-out and nothing-connected
    messages). Not checked: any source against its real service, nor on the deck.
  - After a code review (2026-11-17): a source failing in any way (a mistyped site in
    settings.json) is logged and keeps its last tasks, instead of stopping every source's
    refresh. A source that needs signing in again while others read is said on the card
    (`warn`, amber: "Sign in to Google Tasks again (tray menu)."), where before it just went
    missing behind Obsidian's tasks. Obsidian notes are changed only once they've been
    still for 2.5 s (Obsidian saves a moment after each keystroke), read and written back
    at once, and tried again if anything wrote in between; a task is unticked in the note
    it was read from (yesterday's, after today's note appears). The latest daily note is
    looked for from today back to the last one found (the vault may be on a cloud drive).
    The GitHub CLI is asked for a token every 5 minutes at most while it has none. The
    task popup's title, details and notes scroll together above its buttons, so a long
    title can't push them off. Google's sign-in now uses `OAuthLoopback` and `Secrets`
    (same google-token.bin; its redirect address lost its trailing slash). Checked: 212
    simulator checks (new: the sign-in notice, a long popup keeps its buttons), 18 checks
    of the Obsidian note code on a throwaway vault, companion and firmware builds. The
    cheat sheet check (`bar: layer pill pins the cheat sheet`) passes and fails from run
    to run, on master's build too.
  - Merged with master (2026-11-17), after "it doesn't pull the tasks from my daily note"
    on the work PC: the source had never shipped (0.20.0 and 0.21.0 came from master), and
    that PC's daily note is in its own work vault. The companion took the first vault it
    found and kept it until restart. Now it looks again every poll: settings.json's
    `obsidianVault`, else the vault Obsidian has open. The tray's Task sources › Obsidian
    daily note is a submenu: its tasks on or off, the vaults Obsidian knows on this PC
    (`ObsidianSource.KnownVaults`), the vault Obsidian has open (clears the pick), or
    another folder (it must have `.obsidian`). A picked vault whose folder moved is looked
    for once per pick, and the setting follows it. If it's not found, the open vault is
    used. The daily notes' folder and format come from Periodic Notes when that plugin is
    on and makes them, else from the core plugin. The log names the note read, or where
    today's would be.
  - Checked: 29 checks in a throwaway harness (the companion's sources, with settings.json
    and Obsidian's vault list redirected to a scratch folder), using the user's own note as
    pasted: its two `- [ ]` tasks under "🎯 Today's Objective", the brain dump left out,
    tick and untick rewriting only the box, the open vault, a pick, back to automatic,
    following the vault Obsidian opened, a moved pick found and the setting updated, a
    gone pick falling back, Periodic Notes' nested folder, seven ways of writing the note
    (CRLF, `*`/`+`/numbered bullets, a list in the front matter, a BOM, nested under a
    bullet), and yesterday's note when today's isn't made yet. The companion, firmware and
    simulator build. Not checked: the work vault itself (not on this PC), the tray submenu
    by hand, or on the deck.
- Profiles and accounts, companion only (2026-11-17; asked for: "abstract away data that may
  be changing between user to user, and pc to pc, like implementing user specific and
  configurable profiles", first of four steps: profiles and accounts, then calendar
  providers and filters, then context packs and a Chrome link, then one firmware release
  that makes the deck a generic renderer):
  - Settings in two layers over the defaults: a profile (`profiles\<name>.json`: who you
    are and what the deck shows, nothing tied to one PC, nothing secret) and this PC's
    `machine.json` (the profile in use, `profilesFolder`, `obsidianVault`). Any setting in
    `machine.json` wins over the profile's on this PC (`"jira": null` turns it off there).
    `Settings.Set` writes this PC's own keys and the ones `machine.json` already overrides
    there, everything else into the profile. Its API stayed, so the sources didn't change.
  - The profiles folder can be a synced one (tray: Profile › Keep the profiles in…; the
    profiles not there are copied in), so PCs share profiles. Writes are whole files (written
    beside, then moved over) so a sync never reads half of one. A profile file that can't be
    read keeps its last good settings, and a write never overwrites it. A file is read
    again when its time moves and while that time is recent: a file time only moves every
    ~15 ms, and two writes that close together (the companion's, then a sync client's) were
    taken for one. The first test runs caught it.
  - Migration: the old `settings.json` becomes `profiles\default.json` (with `"$schema"`
    first) and `machine.json` (its `obsidianVault`) the first time; the old file is kept as
    `settings.json.before-profiles`. One with a mistake in it is left in place and tried
    again once it's saved. A fresh PC writes nothing until a setting is set.
  - Switching (tray: Profile: … › a profile) is seen at the next tick (`Settings.Version`,
    now a counter): the deck shows a *Profile: work* toast, and Google, the tasks and the
    vault are read again at once rather than at their next turn. Same when the accounts
    change by hand.
  - Accounts: the profile's `accounts`, by id; without any, one Google account, `google`,
    which keeps `google-token.bin` and the `google` task source id (so `hiddenTaskLists`
    keys and the deck's `src` stay as they were). `GoogleAccount` is one sign-in (its own
    `<id>-token.bin`, its own OAuth `client` file or the shared `google-client.json`,
    `calendar` and `tasks` on unless false; the consent shows Google's account picker).
    `GoogleSource` reads every account's calendars and merges them as before (a meeting on
    several shows once); an account that can't be read keeps its last events. Each account
    with tasks on is a `GoogleTasks` source of its own ("Google Tasks (Work)"), and
    `TaskSource` takes a function for its sources so they follow the profile. It learns
    who signed in from the primary calendar's id, and warns when two accounts are the same
    Google account. Tray: Google accounts (also Task sources › Google Tasks): each account
    with its state, Sign in, its calendar or tasks on the deck, Sign out on this PC, Remove
    from the profile; Add a Google account (a name, optionally its own client file).
  - Tokens per site: Jira's under its site, GitHub's under its host, Microsoft To Do's under
    its app registration (`Secrets.LoadFor`). A work profile's Jira and a home one's each
    keep their own, and switching never sends one site another's token. The token kept
    under the plain name before becomes the first site's that asks. To Do's access token
    in memory is dropped when the app registration changes.
  - `AppPaths.Data` is the one data folder; `FLEX_SCREEN_DATA` points it elsewhere, so a
    companion started for testing leaves the PC's own files alone.
  - `companion/profile.schema.json`: every profile setting, for completion and checks in
    editors (the profile's `"$schema"` points at it on GitHub).
  - Checked: 62 checks in a throwaway harness on a scratch data folder, all passing (the settings ones on five runs in a row).
    They cover the migration (with a comment and a trailing comma), a broken settings.json
    migrated once fixed, and a fresh PC writing nothing. They cover which file each write
    goes to, machine.json overrides and `null`, hand edits, a broken profile kept and left
    alone, accounts, new, copied and missing profiles, a synced profiles folder, and per-site
    tokens with the old token moved. Then the dev companion on a copy of this PC's data folder (`FLEX_SCREEN_DATA`),
    talking to a stand-in deck over `FLEX_SCREEN_DECK`. It migrated the real settings.json,
    read the real Google calendar (2 events) and tasks (the 3 hidden lists still hidden),
    switched to a work profile with a second, unsigned Google account (in 3 s: its colors,
    the *Profile: work* toast, the Tasks card's "Google Tasks (Work)" sign-in warning), and
    back. That run found the switch going unnoticed when the deck's hello had sent the
    settings first: the profile is now compared every tick. Not checked: the tray menus by
    hand, a second Google account actually signed in, Jira, GitHub or To Do across two
    sites, a synced folder with a real sync client, or on the deck.
  - Released as v0.22.1 (companion only, firmware 0.22.0), 2026-11-17.
- Calendars from anywhere, the whole day, and All tasks, firmware 0.23.0 (2026-11-17; step
  2 of the profiles plan, "calendar providers and filters", plus "the taskbar should show
  first a tab with All Tasks from enabled lists, with the scheduled ones on top" and "i
  want to get a view of the whole day, maybe when clicking the up next"):
  - Calendar sources are `ICalendarProvider`s, as task sources are `ITaskProvider`s, made
    from the profile's accounts by `CalendarSources`: `GoogleCalendar` per Google account
    (what `GoogleSource` read before; `GoogleSource` now only keeps the accounts and their
    sign-ins), `MicrosoftCalendar` (Microsoft Graph's calendarView, times asked in UTC, its
    own sign-in with Calendars.Read and User.Read, the account's clientId or To Do's) and
    `IcsCalendar` (an iCalendar link, read at most every 4 minutes). `CalendarSource`
    merges them: each copy filtered on its own, then a meeting once (by UID and start),
    soonest first, at most 24. A source that can't be read keeps its last events.
  - `Ics` expands a feed itself: unfolded lines, escaped text, times in UTC, by TZID (IANA,
    Windows, or Outlook's "(UTC+01:00) Amsterdam, …" display names), floating and all-day;
    RRULE DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, BYDAY (with ordinals),
    BYMONTHDAY, BYMONTH; EXDATE; moved and cancelled occurrences (RECURRENCE-ID). Not
    BYSETPOS, BYWEEKNO, BYYEARDAY or RDATE. A rule's walk stops once a period starts past
    the span, so one that never matches ends at once.
  - Which calendars: each account's `"calendars"` `{hide, show}` (by id or name) over the
    source's own choice (ticked in Google Calendar; Outlook's main calendar). Filters: the
    profile's `"calendarFilter"` (`hideTitles`, words or /regex/; `hideAllDay`;
    `hideDeclined`, on by default as before; `hideFree`). Tray: Calendars, with each source,
    its calendars to tick, its sign-in, link or removal, the filters, and adding a Google
    account, a Microsoft 365 calendar or a link (read once before it's kept). A link's
    address is kept on the PC (`<id>-url` in `Secrets`), as it works as a password; a public
    one can be the account's `"url"` in the profile.
  - The deck gets today from midnight (also what's over) and tomorrow: up to 24 events, those
    not over first so firmware before 0.23.0 (8 kept) keeps the ones to come, each with a
    12-digit deck id (Outlook's ids are ~150 characters) and `cal`, the calendar's name or
    the account's label. The line stays under 7000 bytes by dropping those over first.
  - Day view (firmware, `dayview.cpp`): tapping Up next opens today's timed events in
    order, those over dimmed, a "Now 10:14" line before the next one, the one going on
    picked out ("Now, until 10:32 · Work"), "In 18 min" within the hour, Join on each with
    a link that isn't over; today's all-day events as pills under the date, which stay in
    sight while the list opens scrolled to now; then tomorrow's. It rebuilds when the
    calendar changes and every 30 s. `Google::next()` no longer depends on the order the
    events come in. The deck says "Connect a calendar" rather than Google's name.
  - Tasks card: All tasks is the first chip and where it opens: every task of the lists on
    the deck, grouped by due day (dated first, undated last), each row starting with its
    list's name when there are several. Scheduled is second; a list taken off the deck goes
    back to All tasks; another chip's list starts at the top. Found on the way: a drag that
    started on a heading or between rows didn't scroll the list (the box took no presses),
    so with All tasks' longer list the undated ones were out of reach; the list and the chip
    row now take presses, as the task popup's body did.
  - Checked: 38 calendar checks in a throwaway harness (every time kind, Outlook zone names,
    folding and escapes, each repeat rule, EXDATE, moved and cancelled occurrences, New
    York's clock change, a rule that never matches, the filters, calendar choice and its
    writing, merging and dedup across sources, deck ids, Graph's event shapes); the 62
    settings checks again; the simulator, 289 checks (new: the All tasks chip, the list
    scrolled to its end from a heading, the day view's order, labels, now line, all-day
    strip, Join, Close and a tap beside it); the deck firmware builds for the ESP32 (RAM
    29%, flash 20%). End to end: the dev companion on a copy of this PC's data, with a
    local iCal feed and a title filter, sent the stand-in deck this PC's real Google events
    merged with the feed's: today's from midnight, the filtered one gone, a daily event in
    Jerusalem time today and tomorrow, a Meet link found in a location, the ones to come
    first, 1.25 KB. Not checked: a Microsoft 365 account signed in (no app registration on
    this PC), the Calendars menu by hand, or the day view on the deck itself (it's on the
    work PC).
  - Released as v0.23.0, 2026-11-17.
- Sites as data, installed web apps, and a browser extension, companion only (2026-11-17;
  step 3 of the profiles plan: "context packs and a Chrome link"):
  - A site's keys are data (`WebSite`, a "pack"): the built-in ones in
    `Integrations/Browser/sites.json`, embedded in the exe (Gmail, YouTube and Google
    Calendar as before, by the same ids and keys so saved bars still fit; new: Jira and
    GitHub), and the profile's `"sites"`, first, replacing a built-in one by id. A site is
    told by its address (host and path, `*` globs), then by an installed web app's name,
    then by the tab's title. `ContextBar.Item` reads a key for both `contextBar` and sites.
  - Installed web apps (Install as app): their windows carry an AppUserModelID with
    `_crx_`; `WindowSource` names them as the Start menu does ("Gmail", its list read again
    at most once a minute for one installed since), so the deck shows the app and the bar
    takes it for it (by the site's `app` names, or the profile's `contextBar` by its name).
  - The browser extension (`chrome-ext/`, Manifest V3, Chrome 116 on, and Edge): its service
    worker keeps a WebSocket to `/browser` and sends the active tab's address and title
    (not for incognito tabs or non-web pages) when another tab or window comes to the front
    or the tab moves on, plus a ping every 20 s, which keeps it awake. `BrowserTabs` keeps
    the last 30 by title, in memory, never logged; a browser window whose title starts with
    a reported tab's title is on its address. A report refreshes the bar. The kit carries
    the extension and install.ps1 copies it beside the companion, where Load unpacked
    finds it once per browser; updates come with the companion's.
  - `/browser` takes only a `chrome-extension://` (or `extension://`) Origin. `/vscode`, which
    took any WebSocket, now refuses an `http(s)://` Origin, so a web page can't pose as a VS
    Code window (the extension's Node WebSocket sends none). Both answer a close now; a
    client waited for that answer forever.
  - Found on the way: a `contextBar` entry written as a plain list threw when the bar asked
    it for a color (it has none); YouTube's Mute and Spotify's Louder named a "volume"
    glyph the deck's font doesn't have, so they had no icon (now headphones and plus).
  - Checked: 28 checks in a throwaway harness built against the companion (its internals
    open to "FlexScreen.Tests" only): the built-in sites as before, telling a site by title,
    address, app, the address winning, a profile's site added, replacing one and left out
    without an id, the extension's reports by browser and title, Chrome's bar by a reported
    address and for an installed web app, a site's bank, keys as the settings write them;
    and, against a running dev companion, an extension connecting to `/browser`, a web page
    and an origin-less client refused there, a web page refused on `/vscode` and VS Code's
    kind of client still let in. The extension's script under Node with a mocked browser:
    10 checks (what it sends and when, what it never sends, the ping, reconnecting). This
    PC's Start menu lists its 5 installed web apps as `Chrome._crx_<id>` with their names
    (WhatsApp Web, Google Photos…), the ids the windows carry. Not checked: the extension
    loaded in a real Chrome, an installed web app's window in front, or the bar on the deck.
- Pages per profile, Panels, and the calendar's own topic, firmware 0.24.0 (2026-11-17;
  step 4 of the profiles plan, "the deck as a generic renderer", so a new kind of
  information rarely needs firmware):
  - Pages: the profile's `"pages"` (ids, in tab order) go in the `settings` message; the
    deck keeps only ids it knows, each once (a stray one would never match what it builds
    and restart it forever), saves them, and restarts into a new order as for a palette.
    `ui::Page` stays the page's id; `tab_of`/`page_at` map ids to tabs, so `showPage`, the
    companion's `page` message and the context bar's page for an app go by id, and a page
    left off is a no-op. A page left off is still built, off screen, so its update needs
    no checks. Without `"pages"`: the six as before, and Panels when there are panels.
  - Panels (`page_panels.cpp`, `Panels.cs`): up to 4 lists of up to 12 rows (a line, a
    second line, a badge, a link), one to three side by side, four two by two, rebuilt when
    they change. The companion reads each every `every` seconds (300; 30 at the least):
    a `url` (JSON rows, `{rows, note}`, or text with a row per line), or a `command` run
    through cmd (30 s at most; its error's first line when it fails). A command runs code,
    so it's only taken from this PC's machine.json (`Settings.FromMachine`), never from a
    profile, which can come from a synced folder. A row's address stays with the companion;
    the deck only knows it has one (`panel.open` opens it, http or https only).
  - The calendar goes as `calendar` to a deck whose hello says 0.24.0 or later (`google`,
    its first name, before); events carry their calendar's color (Google's backgroundColor,
    Outlook's hexColor, a link's `"color"`), down the left edge of the day view's rows.
  - Checked: 16 more checks in the harness against the companion (rows from JSON, objects,
    plain text, the 12-row and length limits; a profile's command panel left out and this
    PC's taken; a real command's output as rows, and one that fails with its exit code and
    error; pages with and without panels, the profile's own, unknown ones; colors; which
    topic for which deck); the simulator, 304 checks (new: pages in the profile's order and the others
    off the bar, the same order again without a restart, the `page` message by id, the
    Panels page with three and four panels, a failed one, one being read, a row's link and
    one without, back to the six; the day view from the `calendar` topic); the firmware
    builds for the ESP32 (flash 20.5%). End to end with the real simulator: a profile
    panel reading a text file over HTTP and a machine.json panel running a command; the
    companion sent the pages with Panels, the deck restarted once into them, and the page
    showed both. Not checked: on the deck itself.
- Pages and Panels taken out again, firmware 0.24.1 (2026-11-17): the user hadn't asked for
  either, and didn't want another tab on the deck. The tab bar is the fixed six again
  (`ui::Page` is the tab index, as before 0.24.0), the `settings` message has no `pages`,
  and there's no `panels` topic, `panel.open` or `Panels.cs`. What step 4 also brought stays:
  the `calendar` topic for a deck from 0.24.0 and the calendar colors in the day view. A
  0.24.0 deck that saved a page order keeps it until it gets 0.24.1, which ignores it; a
  0.24.1 companion sends no pages, so a 0.24.0 deck keeps the order it has.
  - Checked: the simulator, 291 checks (the pages and panels ones gone; new: a `settings`
    message with `pages`, as a 0.24.0 companion sends it, leaves the six tabs in their order,
    no Panels tab and no restart); the harness's browser and calendar checks again; the
    companion and the deck firmware build (for the ESP32 too). Not checked: on the deck.
- A torn screen after an update, a steadier update screen, and a quicker context bar,
  firmware 0.24.2 (2026-11-18, found on the deck):
  - After updating to 0.24.1 from the tray, the picture was torn and shifted past the
    screen's edges until a replug; during the update it jumped about. Not memory speed (the
    bench gave the same 15 MB/s fill, 8 MB/s copy before and after a power-on), not a
    restart as such (two palette restarts came back clean). The cause is in ESP-IDF 5.5's
    RGB driver: Arduino builds it with `CONFIG_LCD_RGB_RESTART_IN_VSYNC`, so it restarts
    the transfer every frame, and in that branch it never resets `bb_eof_count`, whose
    parity says which of the two bounce buffers the DMA's EOF interrupt refills. Writing
    flash holds off its interrupts (they aren't IRAM-safe, and PSRAM can't be read then
    anyway); EOFs that pile up meanwhile come as one, an odd number lost flips the parity,
    and from then on the ISR refills the buffer being sent: torn bands for good. The deck
    writes flash while the panel runs only in an update and when the new firmware is kept
    (`esp_ota_mark_app_valid_cancel_rollback`, once the companion answers), which is why it
    showed after the update and a power-on cleared it.
  - Keeping it: `display::resyncSoon()` after that write; the loop then deletes and creates
    the panel (the driver's state from scratch: there's no call that resets the count), sets
    LVGL's buffers to the new frame buffers and redraws everything with the backlight off,
    a blink of about a third of a second, once after an update.
  - The update keeps the image in PSRAM as it comes (it's 1.3 MB; there's 6 MB free) and
    writes it to flash only after `ota_end`, with the screen dark, then restarts: the
    progress screen holds still while the bar fills ("Installing · the screen goes dark for
    a few seconds" for 0.4 s first). Without the room it writes each part as it comes, as
    before. The first byte is checked as the parts come (0xE9), as `Update.write()` did. The
    companion waits 30 s for `ota_done`; ota_upload.py now 60. A deck shows the steady
    screen from the update after the one that brings 0.24.2, since the firmware already
    running receives it.
  - The context bar took almost a second to come back after the Numpad: showing it changed
    the bottom padding of the tab view's content, which laid out all six pages again, a frame
    after the page had been drawn without it (two full redraws, ~250 ms each, plus the
    layout). Now each page but the Numpad keeps the bar's room at its own bottom while the
    bar has keys, so the Numpad only hides and shows the bar.
  - Checked on the deck: two updates over the link, the second received by 0.24.2 (the
    bar still, the dark install, the picture clean after), and one from the companion's own
    update path started through /test (`fw.update`), watched by the user: all still.
- A three-times-faster start, and no endless asking for an icon, firmware 0.24.3
  (2026-11-18):
  - A restart looked like a 10 s hang ending in a watchdog reset. It isn't one: the USB port
    is back within 2 s (ESP-IDF's `esp_restart_noos` arms the RTC watchdog for 1 s, the
    ROM stalls on this board, and that watchdog resets it, hence `reset: watchdog`). The
    time went into setup(): the deck answers `sync` only from the loop, ~8.9 s after the
    start, so the companion's first handshake went unanswered and its retry 5 s later was
    answered 3 s after that, the same after a power-on. The hello now carries `boot`, each
    part's end in ms: the UI took 7.6 s for 1,248 objects (~5 ms each), the overlays (call
    panel, launcher, session board) more than whole pages, and the first `update()` 1.5 s.
  - The cause was the panel running during setup: its bounce-buffer interrupt copies
    ~30 MB/s out of PSRAM on the loop's core, and that traffic shares the MSPI bus with the
    flash the code runs from. `board::begin()` now creates the panel without starting it
    (`esp_lcd_panel_init` starts the transfer), and main starts it once the UI is built,
    before the first frame (the backlight is off until then anyway). The UI now builds in
    2.3 s, the loop starts at 3.7 s instead of 8.9 s, and the companion's first handshake is
    answered (0.4 s after it connected). The same load slows everything while the deck runs:
    the next lever for redraw speed, if one is needed.
  - The Today page asked every 3 s for a Recent tile's icon the companion didn't have: a
    window closed before the companion restarted (the window list only knows open windows'
    icons, and the tiles are saved). The saved tiles now keep their icons' pixels
    (`iconPixels`, put back into the window list at start); a tile whose icon this run can't
    give is sent without one, so it shows its glyph. And `pc::requestIcon()` waits twice as
    long for each unanswered ask of the same icon (3 s, 6 s… a minute), starting over when
    the icon comes or a companion says hello.
- Diagnostics and the edge glow, firmware 0.25.0 (2026-11-18, two ideas from the list:
  diagnostics "worth having before the wireless link", and the glow), on the branch
  `deck-diagnostics-and-glow`:
  - Diagnostics are an overlay like the session board, not a seventh tab. Holding the
    status bar's clock (or the date) opens them; a tap does nothing, so the clock can't open
    them by accident. They need nothing from the PC and stay open offline. Three cards
    (firmware, uptime, frame rate) over two (memory, the link). Everything counts from the
    deck's start; a restart is the reset. The last restart's reason is amber for a panic,
    a task or interrupt watchdog, or a brownout. A plain `watchdog` reset reads "Restarted
    itself (watchdog)" and isn't amber: on this board it's how every restart the firmware
    asks for ends (see 0.24.3), so flagging it cried wolf after each update.
  - Frames come from LVGL's display events, not display.cpp (which the board and the
    simulator each have): a refresh counts as a frame only when it rendered (LVGL's refresh
    timer runs every few ms and mostly finds nothing to draw), and its time leaves out the
    wait for vsync, as the benchmark does. LVGL only draws what changes, so a still screen
    reads 0-1 fps; the busiest second of the last minute says what it manages while
    something moves. Uptime is kept in 64 bits from `millis()`, which runs over at 49 days.
  - The link's counters (`pc::linkStats()`): lines taken in; lines that didn't parse; lines
    past the 8 KB the deck takes (dropped and counted now, not parsed: a cut line can't be
    whole JSON); bytes lost to a full receive buffer (before this only logged); album art
    and icon parts that didn't decode; and drops, each time `pc::connected()` goes from
    true to false. The last error is kept with when, and logged as before.
  - The glow is four strips on LVGL's system layer (over the idle clock, the firmware
    update screen and anything `lv_obj_move_foreground` puts on the top layer). Strips, not
    one bordered object: a change redraws 10 px along the edges instead of the whole screen
    (64-177 ms). Each is 4 px solid and 6 px fading in (a gradient from the color's
    opacity to none). They're boxes, which take no taps, so a tap at the edge still
    reaches what's under it. Red and blue are fixed: Ember's accent is orange and would
    pass for Claude's amber.
  - It first stayed lit for as long as the reason held; the user didn't want it to remain
    ("it should just blink and then fade away"). Now it goes on when something starts:
    on 300 ms, off 200, on 300, off 200, on 300, then fades out over 1.5 s (ease out),
    2.8 s in all. It starts on the rising edge of each reason (Claude needing you, a build
    failing, a call starting), so it doesn't blink again for what's still the same, and a
    newer one restarts it in its own color. A failed build only counts while it's under 10
    minutes old (the build topic's `secs`): a failed build stays the latest result, and
    coming back online to an old one shouldn't blink. Offline the edges aren't looked at
    (what the PC last said may no longer hold), and their last state is kept, so the link
    coming back blinks only for what started meanwhile. The fade sets the gradient's edge
    opacity rather than the object's (which LVGL draws through a layer). Each blink is
    logged ("glow: amber, Claude needs you").
  - The simulator's `deck.py` ended every simulator on the PC by name when it closed
    (`taskkill /IM`), which also ended the runs of another session working on the deck at
    the same time (both died with exit code 1, no crash). It now ends only those on its
    port. It can also read the screen's pixels (`frame()`, `pixel()`), which the glow needs
    since `tree()` doesn't list the system layer, and send raw bytes (`send_raw()`).
  - Checked: the simulator, 340 checks, rebased on 0.24.3 (new: the clock opens them on a
    hold and not on a tap; version, reset reason, uptime moving on, since when, frame rate,
    the busiest second and draw time, memory, nothing cut short; a garbled line, a line
    over 8 KB and a bad image part each counted, in amber, with the last error; a drop
    counted offline, open offline, closed by X and by a tap beside; the glow blinking blue
    when a call starts, then gone, and not again as it goes on; red when a build fails;
    amber when Claude comes to need you, then gone while Claude still waits; on all four
    edges, over the idle clock, a tap through it at the edge; none for a build failed 11
    minutes ago; back online, a blink for a call that started meanwhile and none for what
    stayed the same); the firmware builds for the ESP32 (RAM 29.6%, flash 20.6%). On the deck: the
    always-on glow version (before the blink) ran there as 0.25.0 and drew a clean screen;
    the blink build was flashed the next morning and drew a clean screen too. The blink and
    the overlay haven't been seen on the panel itself.
- The deck's own data in PSRAM, firmware 0.25.1 (2026-11-19; the user: keep Wi-Fi and
  Bluetooth in internal RAM as intended, but leave internal RAM for other work; "run tests
  to see if it compromised the performance too much"):
  - ESP-IDF (Arduino's `CONFIG_SPIRAM_MALLOC_ALWAYSINTERNAL` 4096) puts every `malloc` of
    4 KB or less in internal RAM: the PC's state, each line's JSON, the UI's strings. Lowering
    that limit for everyone would also move lwIP's buffers once Wi-Fi comes: without
    `CONFIG_SPIRAM_TRY_ALLOCATE_WIFI_LWIP` (off in Arduino's build) lwIP uses plain `malloc`
    (`mem_clib_malloc`). The Wi-Fi driver's buffers and NimBLE (`MEM_ALLOC_MODE_INTERNAL`)
    ask for internal RAM by name. So `malloc`, `calloc` and `realloc` are wrapped
    (`-Wl,--wrap=`, `src/psram_first.cpp`): on the loop task, from just before the UI is
    built, they prefer PSRAM (`heap_caps_*_prefer`, internal RAM if PSRAM runs out); on
    every other task they're ESP-IDF's as before. USB, I2C and the panel are set up before,
    and explicit `heap_caps_*` calls (DMA, the panel, FreeRTOS, LVGL's heap) never pass
    through it.
  - The 4 KB buffers for a screenshot's base64 and an update's parts were static (internal
    RAM, idle nearly always); now PSRAM, allocated the first time they're needed.
  - An update first took 35.6 s instead of 24-26 s: the install phase (after the last part)
    went from ~5 s to 14.4 s. Arduino's updater allocates its 4 KB flash-sector buffer with
    a plain `new` (internal before, as 4096 is within the limit; PSRAM now), and ESP-IDF
    writes flash from a PSRAM source a few bytes at a time. `psram::Internal` pauses the
    preference around `Update.begin()`; installing took 5.6 s again (26.8 s in all).
  - The benchmark (`bench`) now logs three lines (one was cut at the log's 160 characters,
    losing the memory figures): redraws; PSRAM's speed and memory, with the largest free
    internal block (what a radio needs in one piece); and, since the last benchmark, lines
    taken in and UI update passes, average and worst.
  - Measured on the deck, the same run on each build (a benchmark 10-20 s after connecting,
    then two more after a minute of page changes each): internal RAM free 127.4 KB before,
    139.4 KB after; its lowest point 108.0 KB, 122.9 KB; the largest free block 73.7 KB,
    81.9 KB. PSRAM free went down only 4 KB (6.30 MB): of the 12 KB gained, 8 KB is the two
    static buffers and the rest the state's strings; the bigger gain in the lowest point is
    each line's JSON, which peaks during the connect burst. Lines after a minute: 1.20-1.26
    ms avg, 5.9 ms worst before; 1.23 ms, 5.9 ms after. In the connect burst 2.0 ms avg, 36
    ms worst before; 2.3-2.5 ms, 33-41 ms after (a different number of lines each time).
    UI update passes 0.52 ms avg before, 0.53 ms after; worst 16-17 ms (100 and 85 ms
    while pages changed) both. Full redraws the same, 226-281 ms a page. No performance cost
    worth the name, and internal RAM went from ~127 to ~139 KB free.
  - What's left in internal RAM is mostly fixed: ~89 KB static (USB stack buffers, part of
    them for USB classes Arduino builds in that the deck doesn't use; the model's arrays;
    fonts' tables are in flash), the panel's 64 KB of bounce buffers, the loop's 32 KB
    stack, and other tasks' stacks.
  - Checked: the simulator, 340 checks, 339 passed (it doesn't run the wrapper: its memory is the PC's).
    The one that failed, "a tap on Stop only says Hold" on the session board, is the test's
    timing: it reads the screen 1.0-1.5 s after the tap and the hint shows for 1.5 s; on its
    own it passed 2 times of 3. On the deck, the measurements above, a screenshot through the
    PSRAM buffer, and updates received by the new build (35.6 s before the fix, 26.8 s
    after), kept each time. Not checked: a tap on the panel (the touch controller's I2C
    buffers were set up before the change and stay internal).
- The side rail, firmware 0.26.0 (2026-11-19; the user picked it from a brainstorm of
  layouts, asked for the context bar along the whole width, and had the last commit before
  it tagged `layout-0`):
  - The tab bar along the bottom became a rail down the left, 76 px wide: the clock and
    date at its top (holding the clock still opens the diagnostics), a button per page (its
    icon over its name, 47 px high), and the shortcuts' button at its foot. The rail ends at
    y 422, above the context bar. The tabview's own bar is hidden and the rail's buttons
    are its own: the tabview takes every button in its bar for a tab, so a clock couldn't
    sit above them, and its bar runs the tabview's whole height. `lv_tabview_set_active()`
    sends no event and pages change from many places (a swipe, the pills, the context
    bar's chip, the companion), so `updateRail()` lights the current page each pass.
  - The status line, 40 px, runs beside the rail. With the clock gone it has about the
    room the old bar had (the clock left, the rail took 76 px): at the old gaps the layer
    pill missed by a pixel beside a meeting, Claude and a build, so the gaps went from 8
    and 6 to 6 and 5, and its padding to 6.
  - The context bar is the same bar, the screen's whole width, 44 px lower: along the
    bottom edge, under the rail too. `kRailW`, `kStatusH`, `kCtxY`, `kRailH` and the
    banner and panel rectangles (`kCol*`, `kPane*`) are in `ui_kit.h`; `kBarH` is gone.
  - The pages lost 76 px of width and gained 48 px of height: their cards are 704×366
    instead of 780×318. Today's Recent became five rows (five tiles across cut "VS Code"
    to "VS C…"). Windows' tiles put the icon above the name again, now there's the height
    (beside it, "Obsidian" read "Obsid…"); its FancyZones panel 300 → 270 px with smaller
    layout thumbnails, so "Columns" fits. Capture's clipboard 236 → 214 px and Obsidian
    186 → 160, so the tasks keep ~310 px; the clipboard's hint is "Tap: paste · hold: pin",
    on one line. Audio's device rows lead with a speaker and a microphone instead of
    "OUTPUT" and "INPUT" (with the words the third output was cut), beside a now-playing
    card of 280 px. Dev's build card 300 → 220 px, for git's five buttons. The Numpad has
    no context bar, so its keys run to the bottom, with an empty corner under the rail.
  - Overlays: the approval banner and Claude's questions span the pages' cards; the session
    board, the call panel and the shortcuts panel sit right of the rail and stop above the
    context bar, so in a call the meeting app's keys stay in reach. The board's rows went
    from 62 to 56 px to keep five, and its usage panel from 272 to 224 px (at 256,
    "VS Code · Sonnet 4.5" was cut beside Stop). The day view and an open task are centered
    on the pages; toasts sit above the context bar. The cheat sheet, the diagnostics and the
    context bar's own menu keep the screen's whole width.
  - Checked: the simulator, 338 checks, 331 passed. The 7 that failed were the tests' idea of
    the old layout (where the call panel is, for five Zoom checks; a Jira row now cut a word
    earlier) and the session board's cut model names; after fixing them those three groups
    passed, 67 of 67. The six pages in the day set (base and violet) and the stress set, and
    the overlays, looked over. The ESP32 build: RAM 27.1 %, flash 20.6 %. On the deck:
    flashed over USB (27 s) and kept once the PC answered (built before the version went up,
    so it said 0.25.1). Not looked at from the PC: screenshots through `/deck` timed out
    with most parts missing, as they had on older builds (the companion's log has the same
    on earlier days).
- The Numpad as a pop-up, five rail buttons, swipes up and down, and the pages laid out
  again for the rail, firmware 0.26.1 (2026-11-19; the user: the tabs' icons "a bit small",
  the Numpad into the shortcuts or the Windows page "to pop up for use", "swipe up to move
  between tabs", then "the layouts should be re-adapted instead of just stretched down and
  squeezed right"):
  - The Numpad isn't a page: **Numpad** in the shortcuts panel's header (reachable from
    every page, unlike the Windows page) opens it over the page on show. Its panel runs from
    the rail to the screen's right edge and down over the context bar, which hides while it's
    open (its chip showed under the rail beside it). Its X, or a tap beside it, closes it and
    lets go of any key held. `kNumpadPage` stays 5 for `page` (the companion's `/deck`, the
    simulator), which opens it; 0-4 close it.
  - The rail's five buttons are 56 px high with 28 px icons (16 px read as small).
  - Swipes: up is the next page, down the one before; sideways still works. LVGL sends no
    gesture for a drag that scrolls a list or moves a fader or a slider (those already had
    gesture bubbling off), so they stay what they are; checked on Audio's faders.
  - The pages laid out again for 704×366 rather than narrowed. Today: Now playing's art 48 →
    96 px. Audio: Now playing a band across the top (the keys on the left, what's playing in
    the middle with the times either side of its progress bar, the 104 px album on the
    right: with the art first and the keys at the far end, the user found it stretched), outputs and inputs as lists in a 200 px
    column (as narrow as "Microphone Array" allows, for the mixer; more devices scroll), and
    the companion ranks Teams right after Spotify among the faders, then Chrome. The call
    apps (Teams, Discord, Zoom) have a fader whenever they run: Windows gives an app a
    session only once it plays, so the user couldn't set Teams before a call. Their level
    is held in `_levels` until a session comes, which `Align` then sets to it (within a
    750 ms round), and kept in `mixer.json` for a restart; an app already playing when the
    companion starts keeps its own level, the mixer beside them (~125 px of fader travel instead
    of ~150). Dev: the banner, then the terminal down the left and a 264 px column of git over
    the build; git's lines stacked (the last commit on a line of its own) and its buttons in a
    3×2 grid, Commit over two cells. Windows: the FancyZones column 234 px with the layouts as
    a list (whole names), tiles ~140 px wide, monitor names on up to two lines. Capture: two
    columns, the tasks 400 px, then the clipboard (its clips in a list that scrolls, three at
    a time) over Obsidian (today's note, and four recent notes in a grid of two even columns:
    chips as wide as their names looked heaped one after the other, the user said).
  - Checked: the simulator, 354 checks, 351 passed. Of the 3 that failed, two were the tests
    tapping a clip and a note the new Capture scrolls or wraps away (now a visible clip, a
    scroll to the rest, and a visible note), and the layer pill's cheat sheet passed on its
    own: those groups again, 15 of 15. New checks: swipes up, down and sideways, the rail, a
    fader drag staying on Audio, the Numpad from the shortcuts, its X, a tap beside it, and
    the context bar hiding under it.
    After the user's changes on the deck (Audio's keys left and album right, Obsidian's notes
    in a grid, the narrower devices column, the call apps' faders): Audio's and Capture's
    checks, 7 of 7 and 14 of 14. The call apps' faders seen with the PC's real Teams and
    Discord, open and silent, through a test companion on the simulator; a first session
    taking the level set beforehand not seen yet (neither played a sound). The ESP32 build,
    and on the deck flashed and kept each time; not looked at from the PC (screenshots over
    USB still time out).
- Twice as fast to draw, touch on its own core, and a picture that rights itself, firmware
  0.27.0 (2026-11-19/20; the user asked whether both cores were used, then for the touch
  task, "smoothness over bluetooth", the memory settings tuned, and the torn screen that
  needed a replug "all the time after testing" looked into):
  - Measured first (`bench` now logs `load:` lines: each core's load, average and busiest
    second, the loop's split between link, UI and LVGL with its worst pass, and every task
    with its core, priority, CPU share and least free stack): core 0 sat idle, everything
    ran on core 1, and drawing was most of it.
  - Touch: a task on core 0 polls the GT911 every 8 ms and LVGL reads what it found, so
    touch is read during long redraws and a tap that comes and goes inside one still counts.
    I2C goes through one lock (the expander's writes from the loop too).
  - Drawing on core 0 was tried: two LVGL draw threads were ~15% slower (most of a redraw is
    the loop walking the objects), one on core 0 was 7-10% faster but the picture flickered
    (its PSRAM traffic starved the panel's refills). Dropped then; to try again on 0.27.0.
  - ESP-IDF is rebuilt with the deck's own settings (`custom_sdkconfig`, pioarduino's
    hybrid compile): a 64 KB data cache with 64-byte lines and a 32 KB instruction cache
    (48 KB of internal RAM), and code and constants copied into PSRAM at start and run from
    there (`SPIRAM_XIP_FROM_PSRAM`, 1.3 MB of PSRAM). Against 0.26.1 with the touch task:
    full redraws of the five pages 280/274/229/269/284 ms became 128/125/100/125/125,
    PSRAM fill and copy 15/8 MB/s became 28/17, core 1 while changing pages 46% busy became
    18%, the first frame 4.4 s after power-on became 3.5 s. Internal RAM free went from 133
    to 83 KB (largest block 61 KB, 33 KB after the panel is recreated), PSRAM free from 6.1
    to 4.9 MB, lowest 4.0.
  - The rebuild starts from Arduino's generic ESP32-S3 settings, not its octal-PSRAM
    build's, and missed two things that one has. TinyUSB (the keyboard, media keys and serial
    link compiled out): `firmware/components/arduino_tinyusb` declares its settings and links
    Arduino's own prebuilt library from the package, unchanged. PSRAM set up at startup
    (`SPIRAM_BOOT_*`): without it Arduino added PSRAM to the heap in `initArduino()`, and
    ESP-IDF sets the lowest free of a heap added after startup to 0, so PSRAM's lowest read 0.
  - The torn screen: Arduino builds ESP-IDF with `LCD_RGB_RESTART_IN_VSYNC`, and in that
    branch ESP-IDF 5.5's RGB driver never resets `bb_eof_count`, whose parity says which
    bounce buffer the EOF interrupt refills (0.24.2 found it after updates and recreated the
    panel then). Anything that holds the interrupt off for two refills, a flash write or
    PSRAM busy, can flip it for good; testing does both often. With it off the driver does
    what ESP-IDF does by default: it counts each frame's refills, restarts after a short
    frame and resets the count, so a late refill costs one frame. Only a rebuild of ESP-IDF
    can change it. The redraw after an update stays.
  - Building: `core_dir = ~/.platformio-deck`, or any build without these settings in the
    same folder (another project, an older branch) reinstalls the framework and the next
    deck build rebuilds it. `firmware/scripts/idf_rebuild.py` patches the platform's
    linker-script step, whose list of fragments (~10,700 characters) is over cmd.exe's 8191,
    to pass it through a file, and keeps pioarduino's `sdkconfig.defaults` (where it keeps
    the settings' hash) beside the rebuilt libraries for a checkout that has none, which
    would otherwise reinstall the framework and rebuild it. `platform_packages` pins SCons to
    the one PlatformIO runs on; the platform's own and PlatformIO's replaced each other
    halfway through a rebuild.
  - Checked on the deck: each build flashed over USB and kept, 0.27.0 built from scratch in
    `~/.platformio-deck` (18 min); the benchmark three times with page changes; PSRAM's
    lowest 4.0 MB; the screen clean after the update (the user). The simulator: 355 checks,
    all passed. Not seen: a missed refill healing (none forced). The deck still sometimes
    reports a watchdog restart after an update, as before.

## Decisions

- Touch version of the board (confirmed).
- Terminal = the last few commands run, each tap-to-replay (saved command buttons were
  dropped on request: the build command changes all the time), run status with
  finish/fail alerts for long builds.
- Calendar = Google Calendar at first; since 2026-11-17 any calendar sources the profile
  names (Google accounts, Microsoft 365 or Outlook.com, iCalendar links), merged, with
  filters. Tasks = Google Tasks, all of it (the mail-only "action items" card was dropped
  on 2026-11-15: "not good enough"), plus Microsoft To Do, the latest Obsidian daily note,
  and Jira and GitHub issues to look at and open (2026-11-15).
- What differs per user or per PC is settings, not code (2026-11-17): profiles that can be
  synced, `machine.json` per PC, sign-ins per PC. The deck keeps its six pages in a fixed
  order: per-profile pages and a Panels page were built in 0.24.0 and taken out in 0.24.1
  (not asked for; no extra tab wanted).
- App integrations to become modules of their own (detection, context, actions, state
  per app: VS Code, Chrome, Edge, Explorer, Teams, Zoom, Discord, OBS…), as the task
  sources are; agreed 2026-11-15, to do once the Zoom work is in.
- Keyboard layer = the layers of the Corne (QMK, programmed with Vial). The G915 TKL has
  no layers to show.
- Changes are confirmed on the real board. Since 2026-11-08 there is also a simulator
  (`simulator/`, on request; it was turned down on 2026-11-06), for reaching every state
  with made-up data and checking layouts in all themes before flashing.
- Terminals = VS Code's integrated terminal only (no PowerShell profile hook for Windows
  Terminal).
- Setup = the VS Code extension plus Claude Code hooks in `~/.claude/settings.json`,
  installed by the companion and the install kit.

## Open questions

- **Corne details**: a wireless **W-CORNE** (USB ID 55D4:0461, serial `VIAL:F64C2B3C`);
  its receiver exposes Vial's raw-HID interface (usage page FF60) and answers read-only
  VIA queries, which is how phase 6 gets the layer without a firmware change. Layer
  names are QMK's usual BASE/LOWER/RAISE/ADJUST; Vial doesn't store names.
