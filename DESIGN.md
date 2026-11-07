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
- App (session) volume is linear amplitude while master is audio-tapered; app faders map
  through `level^1.75` so 50% sounds like 50% on master.
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

## Decisions

- Touch version of the board (confirmed).
- Terminal = the last few commands run, each tap-to-replay (saved command buttons were
  dropped on request: the build command changes all the time), run status with
  finish/fail alerts for long builds.
- Calendar = Google Calendar. Action items = Google Tasks as filled by the `todo` skill.
- Keyboard layer = the layers of the Corne (QMK, programmed with Vial). The G915 TKL has
  no layers to show.
- No simulator: all UI work happens on the real board.
- Terminals = VS Code's integrated terminal only (no PowerShell profile hook for Windows
  Terminal).
- Setup = the VS Code extension plus Claude Code hooks in `~/.claude/settings.json`,
  installed by the companion and the install kit.

## Open questions

- **Corne details**: a wireless **W-CORNE** (USB ID 55D4:0461, serial `VIAL:F64C2B3C`);
  its receiver exposes Vial's raw-HID interface (usage page FF60) and answers read-only
  VIA queries, which is how phase 6 gets the layer without a firmware change. Layer
  names are QMK's usual BASE/LOWER/RAISE/ADJUST; Vial doesn't store names.
