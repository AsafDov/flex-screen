// LVGL on the board's panel and touch.
#pragma once

#include <stdint.h>

namespace display {

// Creates the LVGL display (drawing straight into the panel's two frame buffers) and
// the touch input. Call after board::begin().
void begin();

// Microseconds spent waiting for vsync since boot (the benchmark subtracts it).
uint32_t vsyncWaitUs();

// millis() at the last touch, for the idle screen.
uint32_t lastTouchMs();

// The frame buffer the panel is showing (RGB565, kWidth x kHeight), or null before the
// first frame. LVGL draws into the other one, so it holds still between loop passes.
const void *frontBuffer();

}  // namespace display
