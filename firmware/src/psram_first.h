// The deck's own data in PSRAM, so internal RAM stays free for what needs it.
//
// ESP-IDF puts every malloc under 4 KB in internal RAM (Arduino's CONFIG_SPIRAM_MALLOC_
// ALWAYSINTERNAL): the PC's state (titles, tasks, events, sessions) and each message's
// parse all landed there. Lowering that limit for everyone would also move the network
// stack's buffers once Wi-Fi comes (lwIP allocates with plain malloc), which they're not
// to be. So the limit changes for one task only: malloc, calloc and realloc on the task
// that calls preferForThisTask() take PSRAM first (internal RAM if PSRAM runs out), and
// every other task (USB, and Wi-Fi, its network stack and Bluetooth when they come) keeps
// ESP-IDF's rule. Explicit heap_caps_* calls (DMA buffers, the panel, FreeRTOS objects,
// LVGL's own heap) aren't affected. The linker routes malloc, calloc and realloc here
// (platformio.ini: -Wl,--wrap=...).
#pragma once

namespace psram {

// From now on, allocations made on the calling task (the deck's loop) prefer PSRAM.
void preferForThisTask();

// Pause and resume it (they nest). Use Internal below rather than these.
void pause();
void resume();

// While one is in scope, the task's allocations follow ESP-IDF's rule again. For buffers
// that flash is written from: ESP-IDF copies a PSRAM source over a few bytes at a time, and
// the updater's sector buffer in PSRAM made installing an update 10 s slower (14 s, not 4).
struct Internal {
  Internal() { pause(); }
  ~Internal() { resume(); }
  Internal(const Internal &) = delete;
  Internal &operator=(const Internal &) = delete;
};

}  // namespace psram
