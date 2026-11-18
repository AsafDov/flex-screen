// The edge glow: a thin colored frame around the screen, to catch out of the corner of
// your eye on any page and over the idle clock. Amber while Claude needs you (a session
// waiting, an approval or a question), red for a build that failed in the last 10 minutes,
// blue during a call; when several hold, that's the order. Nothing while the PC is offline.
#pragma once

namespace ui::glow {

void build();   // on LVGL's system layer, over everything else; it never takes a tap
void update();  // call every loop

}  // namespace ui::glow
