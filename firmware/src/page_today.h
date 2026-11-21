// Today: clock, now playing with master volume, the calendar, recent actions and the tasks
// count (opens Tasks). Made of tiles (tiles.h): page_today.cpp only lists them.
#pragma once

#include <lvgl.h>

namespace ui::today {

void build(lv_obj_t *page);
void update();   // call every loop
void showArt();  // call when the installed album art changes

}  // namespace ui::today
