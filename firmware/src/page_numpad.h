// Numpad: a USB keypad (with Num Lock, Enter, arrows and editing keys) and a calculator
// whose result can be typed into the PC. Not a page: it pops up over the pages from the
// shortcuts panel's Numpad button, and a tap beside it (or its X) closes it. Keys work
// without the companion.
#pragma once

#include <lvgl.h>

namespace ui::numpad {

void build();  // the pop-up, hidden, on the top layer
void open();
void close();
bool isOpen();
void update();  // call every loop

}  // namespace ui::numpad
