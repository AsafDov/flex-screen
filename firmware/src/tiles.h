// Tiles: the cards the pages are made of. Each tile is a module of its own (a file under
// tiles/) that fills one card and keeps it up to date. A page only names its tiles
// (pages.cpp); the manager here does the rest: it puts them on the page's grid in that
// order, makes each card and sizes it to its cells, grows and shrinks the cells with the
// room the page has (the context bar takes some of it while it shows), and calls every
// tile's update.
//
// A new tile:
//   1. tiles/<name>.cpp: a class deriving from Tile, and a Kind for it (its id, the cells
//      it asks for, the fewest it can still draw in, and how to make one). See
//      tiles/clock.cpp for the smallest one.
//   2. A line in tiles.cpp's list of kinds.
//   3. Its id in a page's list (pages.cpp).
// The simulator builds every file under tiles/ (simulator/CMakeLists.txt), as the
// firmware does.
#pragma once

#include <lvgl.h>

namespace ui::tiles {

// The grid every page has: 14 columns across the page's 704 px and 12 rows down whatever
// height it has. A cell is 41 px across and about 21 px tall with the context bar showing, 26 without
// it; a tile takes a block of them. (Fine enough for each page to keep the proportions it
// had before tiles: Dev's Claude banner is 2 rows, Today's clock 6 x 4, a full-height
// card 12 rows.)
constexpr int kCols = 14;
constexpr int kRows = 12;

// A tile's size, in cells.
struct Span {
  int cols, rows;
};

// One card on a page.
class Tile {
 public:
  virtual ~Tile() = default;

  // Fills `card`, which the manager has made (kSurface, 12 px padding, round corners) and
  // placed over `span` cells. A tile may restyle the card (its color, a tap on it) and
  // should lay itself out to fill it: the card's height changes with the context bar.
  virtual void build(lv_obj_t *card, Span span) = 0;

  // Every loop: redraw what changed since the last call, and nothing else. Each set
  // redraws its object (ui_kit's setText and friends skip unchanged values).
  virtual void update() {}

  // The album art on the deck changed (ui::installPendingArt).
  virtual void showArt() {}
};

// A kind of tile: what a page names it by, the cells it asks for, and how to make one.
struct Kind {
  const char *id;   // "clock", "now-playing"
  Span size;        // the cells it takes when there's room
  Span min;         // the fewest it can still draw in, when the page is full; {0, 0} = size
  Tile *(*make)();  // a new one
};

// Lays out the tiles named in `ids` on `page`, in that order: each at the first place
// from the top left where it fits, at its own size, or smaller (down to its min) when
// it doesn't. A tile that has no room even at its min, an id no kind has, and an id
// already on a page are left out, and logged.
void layout(lv_obj_t *page, const char *const ids[], int count);

// Every placed tile's update(); call every loop.
void update();

// Every placed tile's showArt(); call when the installed album art changes.
void showArt();

}  // namespace ui::tiles
