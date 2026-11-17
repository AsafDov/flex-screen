// The day at a glance, over everything: today's events in order (the ones that have ended
// dimmed, a "now" line, the one going on picked out, Join on those with a meeting link),
// then tomorrow's. Opened by tapping Up next on Today.
#pragma once

#include <lvgl.h>

namespace ui::day {

void build();
void open();
void update();  // call every loop

}  // namespace ui::day
