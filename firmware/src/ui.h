// The deck's screens: a shared status bar over swipeable pages (Today, Audio, Dev, Windows,
// Capture, Numpad, and Panels when the profile has some), in the order the profile gives,
// following the "ESP32 Desk Companion – 7" UI" canvas scaled from 1024x600 to 800x480.
#pragma once

#include <Arduino.h>

namespace ui {

void begin();   // builds every screen; call after display::begin()
void update();  // redraws whatever the model changed; call every loop

// Shows a master volume the user is dragging to in the status bar right away, ahead of
// the PC's confirmation (which then takes over).
void previewVolume(int level);

// The pages, by id: they keep these whatever order the profile shows them in (the
// settings' "pages"), and the companion's `page` message goes by them.
enum Page { kTodayPage, kAudioPage, kDevPage, kWindowsPage, kCapturePage, kNumpadPage, kPanelsPage, kPageCount };

// "today", "audio", "dev", "windows", "capture", "numpad", "panels".
const char *pageId(int page);

// The pages on the deck now, in order, as the settings write them: "today,audio,dev,…".
String pageOrder();

// Switches to a page (a Page); one the profile leaves off the deck: nothing.
void showPage(int page);
// The page on show (a Page).
int currentPage();

}  // namespace ui
