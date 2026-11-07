// The deck's screens: a shared status bar over six swipeable pages (Today, Audio, Dev,
// Windows, Capture, Numpad), following the "ESP32 Desk Companion – 7" UI" canvas scaled
// from 1024x600 to 800x480.
#pragma once

namespace ui {

void begin();   // builds every screen; call after display::begin()
void update();  // redraws whatever the model changed; call every loop

// Shows a master volume the user is dragging to in the status bar right away, ahead of
// the PC's confirmation (which then takes over).
void previewVolume(int level);

// Switches to a page (0 Today, 1 Audio, 2 Dev, 3 Windows, 4 Capture, 5 Numpad).
void showPage(int index);

}  // namespace ui
