// The deck's screens: six swipeable pages (Today, Audio, Dev, Windows, Capture, Numpad)
// beside a rail of page buttons down the left, under a shared status line, with the
// context bar along the bottom. The pages follow the "ESP32 Desk Companion – 7" UI" canvas
// scaled from 1024x600 to 800x480; the rail took the place of its tab bar along the bottom.
#pragma once

namespace ui {

void begin();   // builds every screen; call after display::begin()
void update();  // redraws whatever the model changed; call every loop

// Shows a master volume the user is dragging to in the status bar right away, ahead of
// the PC's confirmation (which then takes over).
void previewVolume(int level);

// The pages, in tab order.
enum Page { kTodayPage, kAudioPage, kDevPage, kWindowsPage, kCapturePage, kNumpadPage };

// Switches to a page (a Page).
void showPage(int index);
int currentPage();

}  // namespace ui
