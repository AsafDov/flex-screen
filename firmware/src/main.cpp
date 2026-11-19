// flex-screen deck firmware.
#include <Arduino.h>
#include <lvgl.h>

#include "board.h"
#include "display.h"
#include "load.h"
#include "pc.h"
#include "psram_first.h"
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
  pc::bootMark("usb");

  screen_ok = board::begin();
  pc::bootMark("board");
  if (!screen_ok) return;
  board::startTouch();  // the touch task, on core 0
  display::begin();
  pc::bootMark("display");
  // The hardware is set up (USB, I2C, the panel keep their buffers in internal RAM); what
  // the loop allocates from here on (the PC's state, parsing, the UI's texts) goes to PSRAM.
  psram::preferForThisTask();
  ui::begin();
  pc::bootMark("ui");
  board::startPanel();
  pc::bootMark("panel");
  lv_refr_now(nullptr);  // first frame before the backlight comes on
  pc::bootMark("frame");
  board::backlight(true);
  Serial.println("flex-screen deck ready");
}

void loop() {
  static bool first = true;
  if (first) {
    first = false;
    pc::bootMark("loop");  // from here the deck answers the companion
  }
  uint32_t t0 = micros();
  pc::poll();
  load::add(load::Part::Link, micros() - t0);
  if (!screen_ok) {
    delay(5);
    load::endPass();
    return;
  }
  display::resyncIfNeeded();
  t0 = micros();
  ui::update();
  load::add(load::Part::Ui, micros() - t0);
  t0 = micros();
  const uint32_t idle = lv_timer_handler();
  load::add(load::Part::Lvgl, micros() - t0);
  delay(idle < 5 ? idle : 5);
  load::endPass();
}
