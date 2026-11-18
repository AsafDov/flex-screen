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

// After a flash write while the panel runs (keeping new firmware): the panel is created
// again on the next resyncIfNeeded(). Writing flash holds off the panel driver's interrupts,
// and its bounce buffers can come out of step for good: with the restart it does every
// frame, it never resets the count that says which buffer to refill, so a missed one
// leaves the picture torn and shifted until the next start.
void resyncSoon();
// From the loop, between LVGL's passes: creates the panel again if asked, and draws the
// whole screen into it with the backlight off, so it comes back as it was.
void resyncIfNeeded();

}  // namespace display
