// LVGL as on the deck (display.cpp): direct mode on two full-screen RGB565 buffers, the
// one last finished being "on the panel". Here the panel is the simulator's window, and
// the same buffer answers a "shot".
#include "display.h"

#include <Arduino.h>
#include <lvgl.h>

#include "board.h"
#include "sim.h"

namespace display {
namespace {

const void *front = nullptr;
uint32_t last_touch_ms = 0;

void flush(lv_display_t *disp, const lv_area_t *, uint8_t *px) {
  if (lv_display_flush_is_last(disp)) {
    front = px;
    sim::framePresented();
  }
  lv_display_flush_ready(disp);
}

void readTouch(lv_indev_t *, lv_indev_data_t *data) {
  int16_t x, y;
  const bool down = board::readTouch(x, y);
  if (down) last_touch_ms = millis();
  data->point.x = x;
  data->point.y = y;
  data->state = down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED;
}

uint32_t tick() { return millis(); }

}  // namespace

uint32_t vsyncWaitUs() { return 0; }
uint32_t lastTouchMs() { return last_touch_ms; }
const void *frontBuffer() { return front; }

// No RGB panel driver here to come out of step.
void resyncSoon() {}
void resyncIfNeeded() {}

void begin() {
  lv_init();
  lv_tick_set_cb(tick);

  constexpr size_t kBytes = size_t(board::kWidth) * board::kHeight * 2;
  void *fb0 = calloc(1, kBytes);
  void *fb1 = calloc(1, kBytes);

  lv_display_t *disp = lv_display_create(board::kWidth, board::kHeight);
  lv_display_set_color_format(disp, LV_COLOR_FORMAT_RGB565);
  lv_display_set_buffers(disp, fb0, fb1, kBytes, LV_DISPLAY_RENDER_MODE_DIRECT);
  lv_display_set_flush_cb(disp, flush);

  lv_indev_t *touch = lv_indev_create();
  lv_indev_set_type(touch, LV_INDEV_TYPE_POINTER);
  lv_indev_set_read_cb(touch, readTouch);
}

}  // namespace display
