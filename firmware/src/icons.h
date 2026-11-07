// App icons for the Windows page: 32×32 RGB565A8 images the companion sends once each,
// kept in PSRAM by content id. pc.cpp stores them; the page draws them.
#pragma once

#include <Arduino.h>
#include <lvgl.h>

namespace icons {

constexpr int kSize = 32;

// Takes ownership of `pixels` (heap_caps_malloc'd, w*h*3 bytes: RGB565 then alpha).
void put(const char *id, uint8_t *pixels, int w, int h);

// The icon with this id, or null if it isn't on the deck (yet, or any more).
const lv_image_dsc_t *get(const String &id);

// Bumps every time an icon arrives, so pages know to look again.
uint32_t version();

}  // namespace icons
