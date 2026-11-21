#include "page_today.h"

#include "tiles.h"

namespace ui::today {
namespace {

// Today's tiles, in the order the grid takes them: the clock and Up next across the top,
// Now Playing and Recent under them.
//   ┌──────────┬───────────────┐
//   │ clock    │ up-next       │
//   ├──────────┼───────────────┤
//   │ now-     │ recent        │
//   │ playing  │               │
//   └──────────┴───────────────┘
const char *const kTiles[] = {"clock", "up-next", "now-playing", "recent"};

}  // namespace

void build(lv_obj_t *page) { tiles::layout(page, kTiles, sizeof(kTiles) / sizeof(kTiles[0])); }

void update() { tiles::update(); }

void showArt() { tiles::showArt(); }

}  // namespace ui::today
