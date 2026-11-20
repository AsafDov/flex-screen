// The demo: a little over a minute of the deck showing what it does, by itself, for showing
// it to people. The diagnostics panel's Demo button starts it (the companion's "demo"
// message too, for tests). It goes through the pages and the overlays one at a time with
// made-up data, a caption saying what each one is, and puts the deck back as it was when
// it's over, or as soon as the screen is touched.
//
// It plays the companion's part: made-up lines go through pc.cpp's own handling, so every
// page draws them as it would the PC's. Meanwhile the PC's own lines are held back and
// nothing goes to it (pc::demoMode). At the end the deck's state from before comes back,
// then whatever the PC sent while it played. Offline it plays the same, drawn as if the PC
// were there; a deck that never had the time from a PC gets a made-up one for it.
#pragma once

namespace ui::demo {

void build();   // last on LVGL's top layer: its caption, and the touch that ends it, go over everything
void start();   // nothing if it's already playing
void stop();    // puts the deck back; nothing if it isn't playing
void update();  // call every loop
bool playing();

}  // namespace ui::demo
