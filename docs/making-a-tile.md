# Making your own tile

Every page is made of tiles. Each card (Today's clock, Audio's mixer, Dev's terminal…) is a
module of its own in `firmware/src/tiles/`, and a page is only a list of tile ids in
`firmware/src/pages.cpp`. The deck does the rest: it lays the tiles out on a grid, makes
each one's card, sizes it to its cells, resizes it when the context bar comes and goes, and
asks each tile to redraw what changed. A tile of your own is one new file and two one-line
changes.

## The grid

A page is 14 columns by 12 rows. A cell is 41 px across and about 21 px tall while the
context bar shows, 26 without it, so a tile never has a fixed size: it fills the card it's given.
Today's four tiles fill the whole grid:

```
 ←  6 columns  →  ←    8 columns    →
┌───────────────┬───────────────────┐
│ clock         │ up-next           │  4 rows
├───────────────┼───────────────────┤
│ now-playing   │ recent            │  8 rows
│               │                   │
└───────────────┴───────────────────┘
```

The other pages' tiles, and their sizes, are in [DESIGN.md](../DESIGN.md#tiles).

Tiles are placed in the order the page lists them, each at the first place from the top
left where it fits at its own size. If there's no room, it's tried smaller, down to the
`min` its kind allows (fewer rows before fewer columns). A tile that still doesn't fit is
left out, and so are an id no tile has and a second copy of one; the deck logs a line for
each (`tiles: no room for "<id>" on its page`). Every page is full, so a new tile takes
the place of one of its tiles, or one of them gets a smaller `min` to make room.

## 1. Write the tile

Add a file under `firmware/src/tiles/`. This one, `last_build.cpp`, shows the newest build
or test run, green when it passed and amber when it failed:

```cpp
// The Last build tile: the newest build or test run and how it went.
#include "model.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

class LastBuild : public Tile {
 public:
  void build(lv_obj_t *card, Span) override {
    flexCol(card, 4);
    label(card, "LAST BUILD", &lv_font_montserrat_16, kAccent);
    result_ = label(card, "", &title_font, kText);
    cmd_ = label(card, "", &body_font, kMuted);
    ellipsize(cmd_);
  }

  void update() override {
    if (model::build.version == seen_) return;  // nothing new since the last redraw
    seen_ = model::build.version;
    const model::Build &b = model::build;
    if (b.state == "passed") {
      setText(result_, "Passed");
      setTextColor(result_, kGreen);
    } else if (b.state == "failed") {
      setText(result_, "Failed");
      setTextColor(result_, kAmber);
    } else if (b.state == "running") {
      setText(result_, "Running…");
      setTextColor(result_, kText);
    } else {
      setText(result_, "No builds yet");
      setTextColor(result_, kMuted);
    }
    setText(cmd_, b.cmd.c_str());
  }

 private:
  lv_obj_t *result_ = nullptr, *cmd_ = nullptr;
  uint32_t seen_ = UINT32_MAX;
};

}  // namespace

extern const Kind kLastBuild = {"last-build", {6, 4}, {}, []() -> Tile * { return new LastBuild(); }};

}  // namespace ui::tiles
```

- **`build`** fills the card once. The card is already made (the theme's surface color,
  12 px of padding, rounded corners) and placed over the tile's cells. Lay the tile out to
  fill it with LVGL's flex layout, `LV_PCT` and `lv_obj_set_flex_grow`, not pixel sizes:
  the card's height changes with the context bar. A tile may restyle its card or make it
  tappable (Up next does both).
- **`update`** runs on every pass of the deck's loop. Return early unless something you
  show has changed: every model topic has a `version` that goes up when the PC sends it
  again. `setText`, `setTextColor` and the other `ui_kit` setters also skip values that
  are already there.
- **`showArt`** is only for tiles that draw album art (Now Playing); leave it out otherwise.
- **The `Kind`** names the tile: its id (what pages list), the cells it takes when there's
  room (`{6, 4}` is six columns, four rows), the fewest it can shrink to (`{}`: it doesn't
  shrink), and how to make one. It's `extern` so `tiles.cpp` can list it.

The helpers in `ui_kit.h` (`label`, `box`, `button`, `pill`, `flexRow`, `flexCol`,
`ellipsize`) and its palette (`kText`, `kMuted`, `kAccent`, `kGreen`, `kAmber`…) keep a
tile in the deck's look in all five themes. The other tiles are worked examples:
`clock.cpp` is the smallest, `up_next.cpp` has a tap and a button, `now_playing.cpp`
draws album art, and `build.cpp` is Dev's fuller take on this one.

## 2. Register it

In `firmware/src/tiles.cpp`, declare its kind and add it to the list:

```cpp
extern const Kind kLastBuild;
const Kind *const kKinds[] = {&kClock, &kNowPlaying, /* … */ &kObsidian, &kLastBuild};
```

## 3. Put it on the page

In `firmware/src/pages.cpp`, put its id in a page's list. Here it takes Today's clock's
place (the time stays at the top of the rail):

```cpp
const char *const kToday[] = {"last-build", "up-next", "now-playing", "recent"};
```

## 4. Try it, then flash it

The [simulator](../simulator/README.md) builds every file under `tiles/`, so a new tile needs
nothing more there. Build it, then look at the page with made-up data: `tour.py` takes
screenshots of every page with a normal day, long texts and an empty first run, in
`simulator/out/tour/`.

```powershell
powershell -ExecutionPolicy Bypass -File simulator\build.ps1
& "$env:USERPROFILE\.platformio\penv\Scripts\python.exe" simulator\tour.py
```

`test_today_tiles` and `test_page_tiles` in `simulator/test_deck.py` check where each
page's tiles land, so change them with the page's list (`run_some.py test_page_tiles` runs
only that test). Then
flash the deck as in [Firmware](../README.md#firmware). The companion offers its own firmware only
when the deck runs an older version, so your build stays until you accept a newer
release's firmware update, which won't have your tile unless it's in that release.

## Data a tile can show

Tiles read what the companion sends, as kept in `firmware/src/model.h` (`pc.cpp` fills
it): media and volume, the mic, the mixer and audio devices, Claude's sessions and usage,
the terminal, builds, git, a script's progress, windows, recent actions, shortcuts, the
calendar, tasks and the clock (`model::localNow()`). Taps go back through `pc.h`
(`pc::media`, `pc::toggleMic`, `pc::openClaude`, `pc::runShortcut`…).

Anything the deck isn't sent yet takes more than a tile: the companion has to send it (a
topic in [protocol/v0.md](../protocol/v0.md)), and `pc.cpp` has to keep it in the model.
Tiles filled by the companion alone, with no new firmware, aren't built yet.
[DESIGN.md](../DESIGN.md#tiles) has the rules a tile keeps.
