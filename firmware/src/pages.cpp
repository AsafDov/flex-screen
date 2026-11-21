#include "pages.h"

#include "tiles.h"
#include "ui.h"

namespace ui::pages {
namespace {

// Each page's tiles, in the order the grid takes them: each at the first place from the
// top left with room for it. The grid is 14 columns by 12 rows (tiles.h); the drawings show
// where each tile lands.

// Today: the clock and Up next across the top, Now Playing and Recent under them.
//   ┌──────────┬───────────────┐
//   │ clock    │ up-next       │
//   ├──────────┼───────────────┤
//   │ now-     │ recent        │
//   │ playing  │               │
//   └──────────┴───────────────┘
const char *const kToday[] = {"clock", "up-next", "now-playing", "recent"};

// Audio: what's playing across the top; the output and input devices, and the mixer.
//   ┌───────────────────────────┐
//   │ player                    │
//   ├───────┬───────────────────┤
//   │devices│ mixer             │
//   │       │                   │
//   └───────┴───────────────────┘
const char *const kAudio[] = {"player", "devices", "mixer"};

// Dev: Claude's banner across the top; the terminal, and git over the build.
//   ┌───────────────────────────┐
//   │ claude                    │
//   ├─────────────────┬─────────┤
//   │ terminal        │ git     │
//   │                 ├─────────┤
//   │                 │ build   │
//   └─────────────────┴─────────┘
const char *const kDev[] = {"claude", "terminal", "git", "build"};

// Windows: the open windows (and the desktop map), and FancyZones.
//   ┌─────────────────┬─────────┐
//   │ windows         │ fancy-  │
//   │                 │ zones   │
//   └─────────────────┴─────────┘
const char *const kWindows[] = {"windows", "fancyzones"};

// Capture: the tasks, and the clipboard over Obsidian.
//   ┌───────────────┬───────────┐
//   │ tasks         │ clipboard │
//   │               ├───────────┤
//   │               │ obsidian  │
//   └───────────────┴───────────┘
const char *const kCapture[] = {"tasks", "clipboard", "obsidian"};

struct Page {
  const char *const *tiles;
  int count;
};

#define PAGE(list) {list, sizeof(list) / sizeof(list[0])}
const Page kPages[] = {PAGE(kToday), PAGE(kAudio), PAGE(kDev), PAGE(kWindows), PAGE(kCapture)};
#undef PAGE
static_assert(sizeof(kPages) / sizeof(kPages[0]) == kPageCount, "a list of tiles per page");

}  // namespace

void build(int index, lv_obj_t *tab) {
  if (index < 0 || index >= kPageCount) return;
  tiles::layout(tab, kPages[index].tiles, kPages[index].count);
}

}  // namespace ui::pages
