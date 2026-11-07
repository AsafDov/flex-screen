// Today: clock, now playing with master volume, and (from phase 5) calendar and mail.
#pragma once

#include <lvgl.h>

namespace ui::today {

void build(lv_obj_t *page);
void update();   // call every loop
void showArt();  // call when the installed album art changes

}  // namespace ui::today
