// The Capture page's Tasks card: every open task from the companion's task sources (Google
// Tasks for now), by list or all together, grouped by when they're due; tap one to tick it
// off, + to add one with the on-screen keyboard.
#pragma once

#include <lvgl.h>

namespace ui::tasks {

void build(lv_obj_t *row);  // the card, in Capture's row of cards
void update();  // call every loop

}  // namespace ui::tasks
