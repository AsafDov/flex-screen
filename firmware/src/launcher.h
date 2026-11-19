// The shortcuts launcher: a button at the foot of the page rail, on every page, that
// opens a panel of up to 12 tiles over the page: apps, links, deck actions and key
// combinations from the companion's settings. A tap on a tile runs it and closes the
// panel; a tap anywhere else (the button too) just closes it.
#pragma once

#include <lvgl.h>

namespace ui::launcher {

void build(lv_obj_t *rail);  // after the rail's page buttons: the button is its last child
void update();               // call every loop
void close();

}  // namespace ui::launcher
