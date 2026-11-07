// The call controller: when Teams, Zoom, Discord, Webex or a browser is in a call, a
// panel over the pages with what that app allows (mic, camera, hand, deafen, leave,
// reactions, its volume). Hidden, it's a pill in the status bar.
#pragma once

#include <lvgl.h>

namespace ui::call {

void build();             // on LVGL's top layer, before the toast and the idle screen
void buildPill(lv_obj_t *bar);  // the status bar's call pill
void update();            // call every loop

}  // namespace ui::call
