// The edge glow: a thin colored frame around the screen that blinks twice and fades away
// when something happens, to catch out of the corner of your eye on any page and over the
// idle clock. Amber when Claude comes to need you (a session waiting, an approval or a
// question), red when a build fails, blue when a call starts; if several start together,
// that's the order. It doesn't stay lit, and it doesn't blink again for what's still the
// same; nothing while the PC is offline.
#pragma once

namespace ui::glow {

void build();   // on LVGL's system layer, over everything else; it never takes a tap
void update();  // call every loop

}  // namespace ui::glow
