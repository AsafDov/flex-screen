#include "display.h"

#include <Arduino.h>
#include <lvgl.h>

#include "board.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "freertos/semphr.h"
#include "lv_mem_psram.h"

namespace display {
namespace {

SemaphoreHandle_t vsync;
const void *front = nullptr;
uint32_t vsync_wait_us = 0;  // total spent waiting for vsync, for the benchmark

bool IRAM_ATTR onVsync(esp_lcd_panel_handle_t, const esp_lcd_rgb_panel_event_data_t *, void *) {
  BaseType_t woken = pdFALSE;
  xSemaphoreGiveFromISR(vsync, &woken);
  return woken == pdTRUE;
}

// Direct mode: LVGL renders into whichever frame buffer the panel isn't showing. After
// the last area of a frame, hand that buffer to the panel and wait for the vsync where
// it takes over, so LVGL never draws into the buffer being scanned out.
//
// (Rendering 32-line strips in internal RAM and copying them over was tried: slower,
// 125-240 ms a full redraw against 64-177 ms, because both buffers then have to be
// brought up to date by copying, and PSRAM bandwidth is what runs out.)
void flush(lv_display_t *disp, const lv_area_t *, uint8_t *px) {
  if (lv_display_flush_is_last(disp)) {
    xSemaphoreTake(vsync, 0);  // drop a stale vsync from before the swap
    esp_lcd_panel_draw_bitmap(board::panel(), 0, 0, board::kWidth, board::kHeight, px);
    front = px;
    const uint32_t t0 = micros();
    xSemaphoreTake(vsync, pdMS_TO_TICKS(50));
    vsync_wait_us += micros() - t0;
  }
  lv_display_flush_ready(disp);
}

uint32_t last_touch_ms = 0;

void readTouch(lv_indev_t *, lv_indev_data_t *data) {
  int16_t x, y;
  const bool down = board::readTouch(x, y);
  if (down) last_touch_ms = millis();
  data->point.x = x;
  data->point.y = y;
  data->state = down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED;
}

uint32_t tick() { return millis(); }

void onRefresh(lv_event_t *e) { lv_heap_set_rendering(lv_event_get_code(e) == LV_EVENT_REFR_START); }

}  // namespace

uint32_t vsyncWaitUs() { return vsync_wait_us; }
uint32_t lastTouchMs() { return last_touch_ms; }
const void *frontBuffer() { return front; }

void begin() {
  lv_init();
  lv_tick_set_cb(tick);

  vsync = xSemaphoreCreateBinary();
  esp_lcd_rgb_panel_event_callbacks_t cbs = {};
  cbs.on_vsync = onVsync;
  esp_lcd_rgb_panel_register_event_callbacks(board::panel(), &cbs, nullptr);

  void *fb0 = nullptr;
  void *fb1 = nullptr;
  esp_lcd_rgb_panel_get_frame_buffer(board::panel(), 2, &fb0, &fb1);

  lv_display_t *disp = lv_display_create(board::kWidth, board::kHeight);
  lv_display_set_color_format(disp, LV_COLOR_FORMAT_RGB565);
  lv_display_set_buffers(disp, fb0, fb1, board::kWidth * board::kHeight * 2,
                         LV_DISPLAY_RENDER_MODE_DIRECT);
  lv_display_set_flush_cb(disp, flush);
  lv_display_add_event_cb(disp, onRefresh, LV_EVENT_REFR_START, nullptr);
  lv_display_add_event_cb(disp, onRefresh, LV_EVENT_REFR_READY, nullptr);

  lv_indev_t *touch = lv_indev_create();
  lv_indev_set_type(touch, LV_INDEV_TYPE_POINTER);
  lv_indev_set_read_cb(touch, readTouch);
}

}  // namespace display
