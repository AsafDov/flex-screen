// Where the deck's time goes, on both cores, for the benchmark (`bench`): how busy each core
// is, which tasks keep it busy, and how the loop's passes split between the link, the UI and
// LVGL. Measuring only, before any work moves to the second core (DESIGN.md, 0.26.1).
//
// It starts no task, pins nothing, changes no priority and no build setting: what runs on
// core 0 (USB today; the Bluetooth controller and NimBLE, which Arduino's ESP-IDF build pins
// there, once they come) is left exactly as it was. The figures come from FreeRTOS's run-time
// stats, which that build already keeps (an esp_timer count per task).
#pragma once

#include <stdint.h>

namespace load {

// The parts of a loop() pass. Touch and the wait for vsync happen inside Lvgl (its input
// and refresh timers) and are counted there as well as on their own.
enum class Part : uint8_t { Link, Ui, Lvgl, Count };

void add(Part part, uint32_t us);  // time spent in a part during this pass
void touch(uint32_t us);           // one touch read (display.cpp)
void endPass();                    // at the end of every loop() pass, its delay included

// Logs everything since the last report (or the start) as `load:` lines, then starts over.
// The pass it's called from (the benchmark's own) isn't counted.
void report();

}  // namespace load
