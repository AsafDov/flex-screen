// flex-screen deck firmware.
#include <Arduino.h>
#include <lvgl.h>

#include "board.h"
#include "display.h"
#include "pc.h"
#include "ui.h"

// LVGL renders recursively, one level per nested object plus glyph drawing at the
// bottom; on busy redraws that overflowed Arduino's default 8 KB loop stack.
SET_LOOP_TASK_STACK_SIZE(32 * 1024);

// A firmware that came as an update over the link stays on trial until the companion has
// talked to it (pc.cpp's keepFirmware): if it crashes, or can't reach the PC and gets
// unplugged, the next start goes back to the firmware the deck had (the bootloader's app
// rollback). Without this, Arduino keeps any new firmware the moment it starts.
extern "C" bool verifyRollbackLater() { return true; }

namespace {
bool screen_ok = false;
}

void setup() {
  Serial.begin(115200);  // UART port: logs only
  pc::begin();           // USB first, so the link and media keys work even if the panel doesn't

  screen_ok = board::begin();
  if (!screen_ok) return;
  display::begin();
  ui::begin();
  lv_refr_now(nullptr);  // first frame before the backlight comes on
  board::backlight(true);
  Serial.println("flex-screen deck ready");
}

void loop() {
  pc::poll();
  if (!screen_ok) {
    delay(5);
    return;
  }
  ui::update();
  const uint32_t idle = lv_timer_handler();
  delay(idle < 5 ? idle : 5);
}
