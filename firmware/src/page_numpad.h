// Numpad: a USB keypad (with Num Lock, Enter, arrows and editing keys) and a calculator
// whose result can be typed into the PC. Keys work without the companion.
#pragma once

#include <lvgl.h>

namespace ui::numpad {

void build(lv_obj_t *page);
void update();  // call every loop

}  // namespace ui::numpad
