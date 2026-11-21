// The five pages beside the rail: Today, Audio, Dev, Windows and Capture. Each is made of
// tiles (tiles.h); pages.cpp names which, and the tile manager lays them out, sizes them
// and keeps them up to date.
#pragma once

#include <lvgl.h>

namespace ui::pages {

// Lays out page `index` (0 Today … 4 Capture, as kPageNames in ui.cpp) on `tab`.
void build(int index, lv_obj_t *tab);

}  // namespace ui::pages
