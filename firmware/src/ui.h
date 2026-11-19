// The deck's screens: five swipeable pages (Today, Audio, Dev, Windows, Capture) beside a
// rail of page buttons down the left, under a shared status line, with the context bar
// along the bottom, and the Numpad popping up over them from the shortcuts panel. The pages follow the "ESP32 Desk Companion – 7" UI" canvas
// scaled from 1024x600 to 800x480; the rail took the place of its tab bar along the bottom.
#pragma once

namespace ui {

void begin();   // builds every screen; call after display::begin()
void update();  // redraws whatever the model changed; call every loop

// Shows a master volume the user is dragging to in the status bar right away, ahead of
// the PC's confirmation (which then takes over).
void previewVolume(int level);

// The pages, in the rail's order. The Numpad isn't one: it pops up over them. Its old
// index stays for "page" (the companion's /deck, the simulator), which opens it.
enum Page { kTodayPage, kAudioPage, kDevPage, kWindowsPage, kCapturePage, kPageCount, kNumpadPage = kPageCount };

// Switches to a page (a Page), closing the Numpad; kNumpadPage opens it instead.
void showPage(int index);
int currentPage();

}  // namespace ui
