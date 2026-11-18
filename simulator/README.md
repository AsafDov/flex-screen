# Deck simulator

The deck's firmware UI running on the PC: the same LVGL pages, status bar, overlays and
protocol handling as on the board (`firmware/src`, compiled unchanged), drawn in a window
or headless. Anything can play the companion over a local TCP port, so every feature and
state can be reached with made-up data and checked without touching the real PC.

It doesn't replace the board: changes still get flashed and checked there. It's for
reaching what the board can't show on demand (calls, toasts, long texts, empty lists,
other themes, offline) and for catching layout mistakes before flashing.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File simulator\build.ps1
```

Needs Visual Studio's C++ build tools (MSVC) and the firmware's libraries (`pio run -d
firmware` fetches LVGL and ArduinoJson into `firmware/.pio/libdeps`). CMake and Ninja come
from PlatformIO's packages. Output: `simulator\build\flex-screen-sim.exe`.

## Run

```powershell
simulator\build\flex-screen-sim.exe            # a window; the mouse is the finger
simulator\build\flex-screen-sim.exe --headless --port 48160
```

It listens on `127.0.0.1:48160` (`--port` to change) for the companion's side of the link:
newline-delimited JSON, exactly as over USB (`protocol/v0.md`). If it says it can't listen, Windows has
reserved a port range that includes it (`netsh int ipv4 show excludedportrange
protocol=tcp`): pick another, and set `FLEX_SIM_PORT` for the Python tools. Send `{"t":"sync"}` and it
answers `hello`, like the deck. A palette change restarts it, as the deck restarts: the
new process listens again, so reconnect. So does a finished firmware update (`ota` …
`ota_end`): the image is kept in memory, and the simulator restarts as the same build.
`--scale 2` doubles the window.

The companion can play its side too, with the PC's real state: start it with
`FLEX_SCREEN_DECK=tcp:127.0.0.1:48160` in its environment and it connects here instead of
looking for the deck's USB port (handy for its firmware update, through `/test`'s
`fw.update`). It runs beside the installed companion, with its own single-instance lock
and its endpoint one port up, `http://localhost:48152` (Claude Code's hooks stay with the
real one).

On top of the protocol it understands its own messages (never passed to the firmware, and
not counted as the PC being there, so an offline screen stays offline):

| Message | Does |
|---|---|
| `{"t":"sim.touch","x":…,"y":…,"down":true}` | Puts a finger down (or lifts it, `false`) |
| `{"t":"sim.page","i":n}` | Shows page n (0 Today … 5 Numpad) |
| `{"t":"sim.shot"}` | Sends the screen back as `shot` parts (as the firmware's `shot`) |
| `{"t":"sim.ping","id":n}` | Answers `{"t":"sim.pong","id":n,"frames":…}`: wait until `frames` stops changing to know the screen is still |
| `{"t":"sim.dump","id":n}` | Answers `sim.tree`: every object on screen and the top layer (class, box, hidden, label text) |
| `{"t":"sim.quit"}` | Ends it |

What the deck types over USB comes back as `sim.key` (`k`, `down`), `sim.media` (`usage`)
and `sim.type` (`text`).

## Test

Python from PlatformIO (`%USERPROFILE%\.platformio\penv\Scripts\python.exe`) runs these;
they need nothing beyond the standard library.

- `deck.py`: the driver. `Deck.launch()` starts the simulator headless; then `sync()`,
  `load(topics)`, `tap(x, y)`, `hold`, `drag`, `page(i)`, `shot(path)`, `tree()`,
  `took('media.toggle', since=mark)`, `offline()`/`online()`; `frame()` and `pixel(frame,
  x, y)` for colors (the edge glow is on LVGL's system layer, which `tree()` doesn't list);
  `send_raw(bytes)` for lines that aren't JSON. Closing ends only the simulator on its own
  port, so two sessions can each run one (on different `FLEX_SIM_PORT`s).
- `fixtures.py`: made-up state: `day()` (a normal morning), `stress()` (long texts,
  Hebrew, full lists), `empty()` (first run), `call(app)`.
- `test_deck.py`: every feature, tapped, held and dragged, checked against what the deck
  sends; overlays (toast, call panel, cheat sheet, idle screen) and offline. Prints
  PASS/FAIL; screenshots of the interesting states in `out/tests/`.
- `run_some.py test_sessions test_dev`: only those of `test_deck.py`'s tests, for a quick
  check while working on one feature.
- `tour.py`: screenshots of all six pages for each data set and theme in `out/tour/`
  (waiting, offline, gone, day in the four themes, stress, empty).
- `sheet.py`: six screenshots on one sheet, for looking them over: `python sheet.py
  day-ember` or `python sheet.py -o out.png a.png b.png …`.

## How it differs from the board

- `board.cpp`, `display.cpp`, `main.cpp` and the PSRAM allocator are replaced
  (`sim_board.cpp`, `sim_display.cpp`, `sim_main.cpp`); `shim/` stands in for the
  Arduino core, USB, HID, Preferences, OTA (in memory: it checks the size and the
  image's first byte, not the MD5), the ESP-IDF heap and mbedTLS's base64. Preferences
  live in `build/sim-prefs.txt`. There are no app slots, so a firmware is never on trial.
- The process runs in UTC, as the deck's C library does (the companion's `time` carries
  the offset): in the PC's zone, `mktime()` shifted due dates by a day.
- LVGL's object, style and allocation checks are on (`lv_conf.h`): a misuse stops it with
  a message (exit code 70). Slower than the board's build; that's fine on a PC.
- No receive buffer to overflow and no 2.5 s pause while a screenshot goes out: link
  timing problems still need the board.
