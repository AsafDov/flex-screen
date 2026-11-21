#include "tiles.h"

#include <string.h>

#include "pc.h"
#include "ui_kit.h"

namespace ui::tiles {

// Every kind of tile there is, each in its own file under tiles/. A new one adds its line
// to both lists.
extern const Kind kClock, kNowPlaying, kUpNext, kRecent;  // Today
extern const Kind kPlayer, kDevices, kMixer;              // Audio
extern const Kind kClaude, kTerminal, kGit, kBuild;       // Dev
extern const Kind kWindows, kFancyZones;                  // Windows
extern const Kind kTasks, kClipboard, kObsidian;          // Capture
const Kind *const kKinds[] = {
    &kClock,   &kNowPlaying, &kUpNext, &kRecent,  //
    &kPlayer,  &kDevices,    &kMixer,             //
    &kClaude,  &kTerminal,   &kGit,    &kBuild,   //
    &kWindows, &kFancyZones,                      //
    &kTasks,   &kClipboard,  &kObsidian,
};

namespace {

// Columns and rows of equal shares; the page's padding keeps kGap round them (and the
// context bar's room under them while it shows). LVGL keeps pointers to these.
#define FR1 LV_GRID_FR(1)
const int32_t kColumns[] = {FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, LV_GRID_TEMPLATE_LAST};
const int32_t kRowsDsc[] = {FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, FR1, LV_GRID_TEMPLATE_LAST};
#undef FR1
static_assert(sizeof(kColumns) / sizeof(kColumns[0]) == kCols + 1, "a track per column");
static_assert(sizeof(kRowsDsc) / sizeof(kRowsDsc[0]) == kRows + 1, "a track per row");

struct Placed {
  const Kind *kind;
  Tile *tile;
};

constexpr int kMaxPlaced = 32;  // every page's tiles together (16 kinds today), and room to grow
Placed placed[kMaxPlaced];
int placed_count = 0;

const Kind *find(const char *id) {
  for (const Kind *k : kKinds) {
    if (!strcmp(k->id, id)) return k;
  }
  return nullptr;
}

bool isPlaced(const Kind *kind) {
  for (int i = 0; i < placed_count; i++) {
    if (placed[i].kind == kind) return true;
  }
  return false;
}

// Which cells of the page being laid out are taken.
struct Grid {
  bool used[kRows][kCols] = {};

  bool fits(int row, int col, Span s) const {
    for (int r = row; r < row + s.rows; r++) {
      for (int c = col; c < col + s.cols; c++) {
        if (used[r][c]) return false;
      }
    }
    return true;
  }

  // The first place from the top left that has room for `s`.
  bool find(Span s, int *row, int *col) const {
    for (int r = 0; r + s.rows <= kRows; r++) {
      for (int c = 0; c + s.cols <= kCols; c++) {
        if (fits(r, c, s)) {
          *row = r;
          *col = c;
          return true;
        }
      }
    }
    return false;
  }

  void take(int row, int col, Span s) {
    for (int r = row; r < row + s.rows; r++) {
      for (int c = col; c < col + s.cols; c++) used[r][c] = true;
    }
  }
};

// Where a kind goes: at its own size if there's room, else as large as fits down to its
// min (fewer rows first: a tile keeps its width before its height).
bool place(const Grid &grid, const Kind &kind, Span *span, int *row, int *col) {
  const Span max = kind.size;
  const Span min = kind.min.cols > 0 && kind.min.rows > 0 ? kind.min : kind.size;
  for (int cols = max.cols; cols >= min.cols; cols--) {
    for (int rows = max.rows; rows >= min.rows; rows--) {
      const Span s = {cols, rows};
      if (grid.find(s, row, col)) {
        *span = s;
        return true;
      }
    }
  }
  return false;
}

}  // namespace

void layout(lv_obj_t *page, const char *const ids[], int count) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_style_pad_row(page, kGap, 0);
  lv_obj_set_style_pad_column(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  lv_obj_set_grid_dsc_array(page, kColumns, kRowsDsc);

  Grid grid;
  for (int i = 0; i < count; i++) {
    const Kind *kind = find(ids[i]);
    if (!kind) {
      pc::log("tiles: no tile \"%s\"", ids[i]);
      continue;
    }
    if (isPlaced(kind)) {
      pc::log("tiles: \"%s\" is on a page already", ids[i]);
      continue;
    }
    if (placed_count == kMaxPlaced) {
      pc::log("tiles: no room for \"%s\": %d tiles already", ids[i], kMaxPlaced);
      continue;
    }
    Span span;
    int row, col;
    if (!place(grid, *kind, &span, &row, &col)) {
      pc::log("tiles: no room for \"%s\" on its page", ids[i]);
      continue;
    }
    grid.take(row, col, span);
    lv_obj_t *c = card(page);
    lv_obj_set_grid_cell(c, LV_GRID_ALIGN_STRETCH, col, span.cols, LV_GRID_ALIGN_STRETCH, row, span.rows);
    Tile *tile = kind->make();
    tile->build(c, span);
    placed[placed_count++] = {kind, tile};
  }
}

void update() {
  for (int i = 0; i < placed_count; i++) placed[i].tile->update();
}

void showArt() {
  for (int i = 0; i < placed_count; i++) placed[i].tile->showArt();
}

}  // namespace ui::tiles
