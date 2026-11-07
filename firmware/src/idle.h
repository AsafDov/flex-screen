// Idle: after a while with no touch and no input on the PC (or as soon as the PC locks),
// a dark clock screen with the next meeting covers the pages; a while later the
// backlight goes off. Any tap wakes it, and that tap doesn't reach the page below.
// The timings come from the companion's settings.json (model::settings).
#pragma once

namespace ui::idle {

void build();   // after the pages: the clock screen sits on LVGL's top layer
void update();  // call every loop
void wake();    // as if the screen were touched

}  // namespace ui::idle
