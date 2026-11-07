// The keyboard layer cheat sheet: once a layer key has been held for ten seconds, the deck
// shows what that layer has on every key, laid out like the keyboard (from the companion,
// which reads the layout and keymap from a Vial keyboard). Letting go hides it. Tapping
// the status bar's layer pill pins it open, following the layer, until it's tapped.
#pragma once

#include <lvgl.h>

namespace ui::cheat {

void build();   // on LVGL's top layer, above the call panel
void toggle();  // the layer pill: pin it open, or close it
void update();  // call every loop

}  // namespace ui::cheat
