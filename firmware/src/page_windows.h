// Windows: the open windows (tap to focus, hold to close, Show desktop) and FancyZones
// (each monitor's layout, and a map of its zones to snap the active window into).
#pragma once

#include <lvgl.h>

namespace ui::windows {

void build(lv_obj_t *page);
void update();  // call every loop

}  // namespace ui::windows
