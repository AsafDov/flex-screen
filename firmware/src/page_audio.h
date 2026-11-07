// Audio: now playing with progress, output and input devices, and a per-app mixer.
#pragma once

#include <lvgl.h>

namespace ui::audio {

void build(lv_obj_t *page);
void update();   // call every loop
void showArt();  // call when the installed album art changes

}  // namespace ui::audio
