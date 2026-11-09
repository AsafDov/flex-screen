# flex-screen — design

A 7" touch deck that sits next to the keyboard and runs the parts of the workday that
live outside the editor: audio, meetings, Claude Code, builds, git, windows, clipboard,
Obsidian, and a numpad.

UI reference: the **ESP32 Desk Companion – 7" UI** canvas
(https://claude.ai/artifact/Dh5LJ4fLcCeHGErS4omf3o): six swipe pages (Today, Audio,
Dev, Windows, Capture, Numpad) under one shared status bar (clock, next meeting,
Claude state, build result, keyboard layer, volume, mic mute).

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
│ USB composite: CDC + HID       │ ── HID keys ──────► │  clipboard · git · Google · vault   │
└────────────────────────────────┘    (numpad, media)  │  localhost HTTP/WebSocket  ◄──┐     │
                                                       └───────────────────────────────┼─────┘
             VS Code extension (TypeScript) ── workspace, terminal runs, tasks ────────┤
             Claude Code hooks ─────────────── working / needs you / done ─────────────┤
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
| 13 | Obsidian | Capture | Companion | Vault `DoorsOfStone`: today's note (`obsidian://` URI), Clipboard → Inbox (append to the file), recent notes (from `.obsidian/workspace.json`), quick capture |
| 14 | Calendar | Status bar, Today | Companion | Google Calendar API (read-only, every calendar ticked in Google Calendar): next-meeting countdown, today's list, Join button from the Meet/Teams/Zoom link |
| 15 | Action items from mail | Today, Capture | Companion | Google Tasks, the list the `todo` skill fills from mail triage; tap = mark done |
| 16 | Keyboard layer | Status bar | Corne firmware + companion | The Corne runs QMK with Vial, which has no "current layer" query, so a small firmware addition answers a custom raw-HID request with the active layer. The companion polls it (pausing while the Vial app is open so their replies don't cross) and maps layer numbers to names (BASE, LOWER, RAISE, ADJUST). Hidden when the Corne isn't plugged in |
| 17 | Numpad | Numpad | Firmware only | USB HID keypad keys plus a calculator mode; no companion needed |
| 19 | Context bar | Every page but the Numpad | Companion | Keys for the app in front (the window list's foreground, by exe), in its color, picked on the deck from the app's bank of actions (long press) and kept per app; settings.json's `contextBar` adds actions. Keys are `SendInput` combos into that window (refocused first if it lost the front), deck actions, or closing it |
| 18 | Shortcuts launcher | Every page (tab bar) | Companion | Up to 12 tiles from settings.json: links, Start menu apps (found in `shell:AppsFolder` by name, focused if open, else started with `explorer shell:AppsFolder\<id>`), files and folders, deck actions, key combinations (`SendInput`). Icons from the shell (`IShellItemImageFactory`) and sites' `/favicon.ico` |

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
install/     install kit for another PC: build-dist.ps1, install/uninstall scripts
dist/        build-dist.ps1's output (flex-screen.zip)
docs/        user guides (google-setup.md: Calendar, Tasks and mail; also in the install kit)
simulator/   the firmware UI on the PC (MSVC), with a Python driver, feature tests and screenshot tours
qmk/         (phase 6) Corne keymap addition (layer report) for vial-qmk
```

## Installing on another PC

The deck needs nothing per PC. For the PC side, run `install\build-dist.ps1` here: it
publishes the companion self-contained (one 56 MB exe, no .NET needed there), packages the
VS Code extension and zips both with the scripts into `dist\flex-screen.zip`. On the
other PC, unzip and double-click `install.cmd`, which (without admin rights):

- copies the companion to `%LOCALAPPDATA%\Programs\flex-screen` and adds Startup and
  Start menu shortcuts,
- installs `flex-screen.vsix` with `code --install-extension`,
- runs `flex-screen-companion.exe --install-hooks`, which merges the hooks into
  `~/.claude/settings.json` (keeping a `settings.json.before-flex-screen` backup); the
  Claude extension in VS Code reads the same file as the CLI,
- starts the companion.

Running it again updates in place; `uninstall.cmd` reverses all of it. Firmware updates
still go from this PC (`pio run -d firmware -t upload`).

## Toolchain on this PC

| Need | Status |
|---|---|
| PlatformIO core | upgraded to 6.2.0 (the pioarduino build, which reports `6.2.0.post1`) |
| ESP32 platform | pioarduino **55.03.311** (Arduino-ESP32 3.3.11, ESP-IDF 5.5.5). Pinned because 55.03.312 and later read `6.2.0.post1` as older than 6.2.0 and refuse to install |
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
  Zoom has no API: Alt+A/V/Y/Q go to its meeting window (`ZPContentViewWndClass`) and
  the focus comes back; leaving still needs confirming in Zoom, since a host's dialog
  also offers "End for all". Discord's API needs approval for voice control, so mic is
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

## Decisions

- Touch version of the board (confirmed).
- Terminal = the last few commands run, each tap-to-replay (saved command buttons were
  dropped on request: the build command changes all the time), run status with
  finish/fail alerts for long builds.
- Calendar = Google Calendar. Action items = Google Tasks as filled by the `todo` skill.
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
