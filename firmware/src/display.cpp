#include "display.h"

#include <Arduino.h>
#include <lvgl.h>

#include "board.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "freertos/semphr.h"
#include "load.h"
#include "lv_mem_psram.h"

// LVGL's one draw thread runs on core 0; the loop, LVGL's object walk and the panel's refills
// stay on core 1 (0.27.1). Its PSRAM traffic slows the refills (a refill-sized read took
// about twice as long), which flickered with 0.26.0's memory settings; with 0.27.0's caches
// and code in PSRAM it doesn't show, and draw time is 12-17% less. Two draw threads, one on
// each core, were ~15% slower: most of a redraw is the loop walking the objects, which can't
// be split. Touch has its own task on core 0 too (board::startTouch()).

// LVGL starts its draw thread ("swdraw", lv_draw_sw.c) with plain xTaskCreate, on no core in
// particular, and ESP-IDF pins an unpinned task to the core it's on the first time it uses
// the FPU. The linker routes every xTaskCreatePinnedToCore here (platformio.ini: --wrap): the
// draw thread goes to core 0, so it draws while the loop on core 1 walks the screen's objects
// and queues the next draw tasks. Every other task is created exactly as asked.
extern "C" BaseType_t __real_xTaskCreatePinnedToCore(TaskFunction_t code, const char *const name, const uint32_t stack,
                                                     void *const arg, UBaseType_t priority, TaskHandle_t *const created,
                                                     const BaseType_t core);

extern "C" BaseType_t __wrap_xTaskCreatePinnedToCore(TaskFunction_t code, const char *const name, const uint32_t stack,
                                                     void *const arg, UBaseType_t priority, TaskHandle_t *const created,
                                                     const BaseType_t core) {
  BaseType_t pin = core;
  if (core == tskNO_AFFINITY && name && strcmp(name, "swdraw") == 0) pin = 0;
  return __real_xTaskCreatePinnedToCore(code, name, stack, arg, priority, created, pin);
}

namespace display {
namespace {

SemaphoreHandle_t vsync;
lv_display_t *disp = nullptr;
const void *front = nullptr;
uint32_t vsync_wait_us = 0;  // total spent waiting for vsync, for the benchmark
volatile bool resync_wanted = false;
volatile uint32_t vsyncs = 0;
volatile uint32_t frames_refilled = 0;

bool IRAM_ATTR onFrameRefilled(esp_lcd_panel_handle_t, const esp_lcd_rgb_panel_event_data_t *, void *) {
  frames_refilled = frames_refilled + 1;
  return false;
}

bool IRAM_ATTR onVsync(esp_lcd_panel_handle_t, const esp_lcd_rgb_panel_event_data_t *, void *) {
  vsyncs = vsyncs + 1;
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
  const uint32_t t0 = micros();
  const bool down = board::readTouch(x, y);
  load::touch(micros() - t0);
  if (down) last_touch_ms = millis();
  data->point.x = x;
  data->point.y = y;
  data->state = down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED;
}

uint32_t tick() { return millis(); }

void onRefresh(lv_event_t *e) { lv_heap_set_rendering(lv_event_get_code(e) == LV_EVENT_REFR_START); }

// The panel's vsync callback and its two frame buffers, as LVGL's draw buffers.
void attachPanel() {
  esp_lcd_rgb_panel_event_callbacks_t cbs = {};
  cbs.on_vsync = onVsync;
  cbs.on_frame_buf_complete = onFrameRefilled;
  esp_lcd_rgb_panel_register_event_callbacks(board::panel(), &cbs, nullptr);
  void *fb0 = nullptr;
  void *fb1 = nullptr;
  esp_lcd_rgb_panel_get_frame_buffer(board::panel(), 2, &fb0, &fb1);
  lv_display_set_buffers(disp, fb0, fb1, board::kWidth * board::kHeight * 2, LV_DISPLAY_RENDER_MODE_DIRECT);
  front = nullptr;
}

}  // namespace

void resyncSoon() { resync_wanted = true; }

void resyncIfNeeded() {
  if (!resync_wanted || !disp) return;
  resync_wanted = false;
  const bool lit = board::backlightOn();
  if (lit) board::backlight(false);  // the new frame buffers start black
  if (!board::recreatePanel()) return;  // nothing to show on: as after a failed start
  attachPanel();
  lv_obj_invalidate(lv_display_get_screen_active(disp));  // with the layers over it
  lv_refr_now(disp);
  if (lit) board::backlight(true);
}

uint32_t vsyncWaitUs() { return vsync_wait_us; }
void panelCounts(uint32_t &v, uint32_t &f) {
  v = vsyncs;
  f = frames_refilled;
}
uint32_t lastTouchMs() { return last_touch_ms; }
const void *frontBuffer() { return front; }

void begin() {
  lv_init();
  lv_tick_set_cb(tick);

  vsync = xSemaphoreCreateBinary();
  disp = lv_display_create(board::kWidth, board::kHeight);
  lv_display_set_color_format(disp, LV_COLOR_FORMAT_RGB565);
  attachPanel();
  lv_display_set_flush_cb(disp, flush);
  lv_display_add_event_cb(disp, onRefresh, LV_EVENT_REFR_START, nullptr);
  lv_display_add_event_cb(disp, onRefresh, LV_EVENT_REFR_READY, nullptr);

  lv_indev_t *touch = lv_indev_create();
  lv_indev_set_type(touch, LV_INDEV_TYPE_POINTER);
  lv_indev_set_read_cb(touch, readTouch);
}

}  // namespace display
