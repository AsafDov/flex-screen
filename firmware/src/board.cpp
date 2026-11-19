#include "board.h"

#include <Arduino.h>
#include <Wire.h>
#include <string.h>

#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "esp_ota_ops.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

namespace board {
namespace {

constexpr int kSda = 8;
constexpr int kScl = 9;
constexpr int kTouchInt = 4;

// The CH422G has no registers: each function is a one-byte write to its own I2C address.
constexpr uint8_t kExpSet = 0x24;  // bit 0 enables IO0-7 as outputs
constexpr uint8_t kExpIo = 0x38;   // IO0-7 output levels
constexpr uint8_t kExpOutputsOn = 0x01;

constexpr uint8_t kTouchReset = 1 << 1;
constexpr uint8_t kBacklight = 1 << 2;
constexpr uint8_t kLcdReset = 1 << 3;
constexpr uint8_t kSdCs = 1 << 4;
// IO5 (USB_SEL) high routes GPIO19/20 to the CAN transceiver and cuts the native USB
// port. The factory demo drives every output high, which is why its USB vanished.
// It stays low here.
constexpr uint8_t kUsbSel = 1 << 5;

constexpr uint8_t kGt911 = 0x5D;
constexpr uint16_t kGtProductId = 0x8140;
constexpr uint16_t kGtConfig = 0x8047;  // config block start (version, sizes, thresholds…)
constexpr uint16_t kGtLowPower = 0x8055;        // bits 0-3: idle seconds before slow scanning
constexpr uint16_t kGtRefresh = 0x8056;         // report interval: 5 + this, in ms
constexpr uint16_t kGtConfigChecksum = 0x80FF;  // two's complement of 0x8047..0x80FE
constexpr uint16_t kGtConfigFresh = 0x8100;     // write 1 to apply a new config
constexpr uint8_t kGtMaxLowPowerTimeout = 15;
constexpr uint16_t kGtStatus = 0x814E;  // bit 7: new report ready, bits 0-3: touch count
constexpr uint16_t kGtPoint1 = 0x814F;  // track id, x lo/hi, y lo/hi, size lo/hi, reserved

uint8_t exp_out = kSdCs;  // SD card deselected, USB_SEL low, everything else held low
esp_lcd_panel_handle_t panel_handle = nullptr;
constexpr uint32_t kReleaseDebounceMs = 40;  // reports come every 8 ms while touched

// The touch task (startTouch()) on core 0 polls the GT911; readTouch() on the loop only
// reads what it found. Both kinds of I2C traffic (touch, and the expander's backlight and
// resets from the loop) take i2c_lock, a transaction or a GT911 exchange at a time.
constexpr uint32_t kTouchPollMs = 8;  // the GT911 reports every 5 + refresh (3) ms
constexpr UBaseType_t kTouchPriority = 5;  // above LVGL's draw threads (3), below USB (24)
SemaphoreHandle_t i2c_lock = nullptr;
portMUX_TYPE touch_mux = portMUX_INITIALIZER_UNLOCKED;

// The touch task's own state.
bool touch_down = false;
uint32_t touch_seen_ms = 0;

// What it found, under touch_mux. `presses` counts each finger coming down, so a tap that
// came and went between two of LVGL's reads (during a long redraw) still reaches it.
struct Touch {
  int16_t x = 0, y = 0;
  int16_t press_x = 0, press_y = 0;  // where the last press began
  bool down = false;
  uint32_t presses = 0;
} touch;
uint32_t presses_seen = 0;  // the loop's: the last press LVGL was told about
bool gt911_found = false;
bool touch_task = false;  // running; until then (or if it couldn't start) readTouch() polls itself
TouchInfo touch_info;

struct I2cLock {
  I2cLock() {
    if (i2c_lock) xSemaphoreTake(i2c_lock, portMAX_DELAY);
  }
  ~I2cLock() {
    if (i2c_lock) xSemaphoreGive(i2c_lock);
  }
};

bool i2cWrite(uint8_t addr, const uint8_t *data, size_t len) {
  I2cLock lock;
  Wire.beginTransmission(addr);
  Wire.write(data, len);
  return Wire.endTransmission() == 0;
}

bool expWrite(uint8_t addr, uint8_t value) { return i2cWrite(addr, &value, 1); }

bool expSet(uint8_t mask, bool high) {
  exp_out = high ? (exp_out | mask) : (exp_out & ~mask);
  return expWrite(kExpIo, exp_out);
}

bool gtRead(uint16_t reg, uint8_t *buf, size_t len) {
  const uint8_t addr[2] = {uint8_t(reg >> 8), uint8_t(reg & 0xFF)};
  I2cLock lock;  // the address write and the read, with no other transaction between them
  Wire.beginTransmission(kGt911);
  Wire.write(addr, 2);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(int(kGt911), int(len)) != int(len)) return false;
  for (size_t i = 0; i < len; i++) buf[i] = Wire.read();
  return true;
}

bool gtWrite(uint16_t reg, uint8_t value) {
  const uint8_t data[3] = {uint8_t(reg >> 8), uint8_t(reg & 0xFF), value};
  return i2cWrite(kGt911, data, 3);
}

// The GT911 drops to slow scanning after 0x8055's idle timeout (3 s as shipped on this
// board), so the first touch after a pause registered late and quick taps were missed.
// Raise it to the maximum. The controller keeps its config in its own flash, so this
// writes only when the value differs; Arduino's Wire buffer holds 128 bytes, so the
// 184-byte config block is read in two halves for the checksum.
void gtSetLowPowerTimeout(uint8_t seconds) {
  constexpr size_t kLen = kGtConfigChecksum - kGtConfig;  // 0x8047..0x80FE
  uint8_t cfg[kLen];
  if (!gtRead(kGtConfig, cfg, kLen / 2) || !gtRead(kGtConfig + kLen / 2, cfg + kLen / 2, kLen - kLen / 2)) return;
  uint8_t &low_power = cfg[kGtLowPower - kGtConfig];
  if ((low_power & 0x0F) == seconds) return;
  low_power = (low_power & 0xF0) | seconds;
  uint8_t sum = 0;
  for (uint8_t b : cfg) sum += b;
  gtWrite(kGtLowPower, low_power);
  gtWrite(kGtConfigChecksum, uint8_t(~sum + 1));
  gtWrite(kGtConfigFresh, 1);  // apply and store
  delay(100);
}

bool beginExpander() {
  exp_out &= ~kUsbSel;
  // Levels first, then enable the outputs, so USB_SEL never glitches high.
  return expWrite(kExpIo, exp_out) && expWrite(kExpSet, kExpOutputsOn) &&
         expWrite(kExpIo, exp_out);
}

// `start`: also start sending frames (begin() leaves that to startPanel()).
bool createPanel(bool start) {
  esp_lcd_rgb_panel_config_t cfg = {};
  cfg.clk_src = LCD_CLK_SRC_DEFAULT;
  cfg.timings.pclk_hz = 16 * 1000 * 1000;
  cfg.timings.h_res = kWidth;
  cfg.timings.v_res = kHeight;
  cfg.timings.hsync_pulse_width = 4;
  cfg.timings.hsync_back_porch = 8;
  cfg.timings.hsync_front_porch = 8;
  cfg.timings.vsync_pulse_width = 4;
  cfg.timings.vsync_back_porch = 8;
  cfg.timings.vsync_front_porch = 8;
  cfg.timings.flags.pclk_active_neg = 1;
  cfg.data_width = 16;
  cfg.bits_per_pixel = 16;
  cfg.num_fbs = 2;  // LVGL draws into one while the panel scans out the other
  // The CPU copies each frame from PSRAM into these internal-RAM lines, so PSRAM
  // traffic from Wi-Fi or drawing can't starve the panel and shift the picture.
  cfg.bounce_buffer_size_px = kWidth * 20;  // 2 x 32 KB: room for a late refill
  cfg.dma_burst_size = 64;
  cfg.hsync_gpio_num = 46;
  cfg.vsync_gpio_num = 3;
  cfg.de_gpio_num = 5;
  cfg.pclk_gpio_num = 7;
  cfg.disp_gpio_num = -1;
  // B3-B7, G2-G7, R3-R7
  const int data[16] = {14, 38, 18, 17, 10, 39, 0, 45, 48, 47, 21, 1, 2, 42, 41, 40};
  memcpy(cfg.data_gpio_nums, data, sizeof(data));
  cfg.flags.fb_in_psram = 1;

  // Created from setup(), so the panel's interrupt runs on core 1 with LVGL. On core 0
  // redraws were 25% faster, but its bounce-buffer refills then raced LVGL for PSRAM
  // and lost: the picture flickered. On the same core it interrupts LVGL and always wins.
  return esp_lcd_new_rgb_panel(&cfg, &panel_handle) == ESP_OK && esp_lcd_panel_reset(panel_handle) == ESP_OK &&
         (!start || esp_lcd_panel_init(panel_handle) == ESP_OK);
}

bool beginPanel() {
  expSet(kLcdReset, false);
  delay(10);
  expSet(kLcdReset, true);
  delay(100);
  return createPanel(false);
}

bool beginTouch() {
  // INT held low while reset releases selects the GT911's 0x5D address.
  pinMode(kTouchInt, OUTPUT);
  digitalWrite(kTouchInt, LOW);
  delay(10);
  expSet(kTouchReset, false);
  delay(100);
  expSet(kTouchReset, true);
  delay(200);
  pinMode(kTouchInt, INPUT);

  uint8_t id[4];
  if (!gtRead(kGtProductId, id, sizeof(id)) || memcmp(id, "911", 3) != 0) return false;
  gt911_found = true;

  gtSetLowPowerTimeout(kGtMaxLowPowerTimeout);
  uint8_t cfg[kGtRefresh - kGtConfig + 1];  // read back, so hello shows what stuck
  if (gtRead(kGtConfig, cfg, sizeof(cfg))) {
    touch_info.ok = true;
    touch_info.config_version = cfg[0];
    touch_info.low_power = cfg[kGtLowPower - kGtConfig] & 0x0F;
    touch_info.refresh = cfg[kGtRefresh - kGtConfig];
  }
  return true;
}

}  // namespace

bool begin() {
  Wire.begin(kSda, kScl, 400000);
  if (!beginExpander()) {
    Serial.println("board: CH422G expander not responding");
    return false;
  }
  if (!beginPanel()) {
    Serial.println("board: RGB panel init failed");
    return false;
  }
  if (!beginTouch()) {
    Serial.println("board: GT911 touch not found");  // the display still works without it
  }
  return true;
}

esp_lcd_panel_handle_t panel() { return panel_handle; }

bool startPanel() { return panel_handle && esp_lcd_panel_init(panel_handle) == ESP_OK; }

bool recreatePanel() {
  if (panel_handle) esp_lcd_panel_del(panel_handle);
  panel_handle = nullptr;
  return createPanel(true);
}

void backlight(bool on) { expSet(kBacklight, on); }
bool backlightOn() { return exp_out & kBacklight; }

TouchInfo touchInfo() { return touch_info; }

namespace {

// One poll of the GT911, on the touch task.
void pollTouch() {
  uint8_t status;
  int16_t x = touch.x, y = touch.y;
  if (gtRead(kGtStatus, &status, 1) && (status & 0x80)) {
    const uint8_t count = status & 0x0F;
    uint8_t p[8];
    if (count > 0 && count <= 5 && gtRead(kGtPoint1, p, sizeof(p))) {
      x = int16_t(p[1] | (p[2] << 8));
      y = int16_t(p[3] | (p[4] << 8));
      touch_down = true;
      touch_seen_ms = millis();
    } else {
      touch_down = false;
    }
    gtWrite(kGtStatus, 0);  // hand the buffer back to the controller
  }
  // No new report since the last poll: the finger hasn't moved or lifted. The GT911 also
  // sends the odd empty report mid-press, which read as a lift and a second tap, so a
  // lift only counts once no finger has been seen for a few reports.
  const bool down = touch_down || millis() - touch_seen_ms < kReleaseDebounceMs;
  taskENTER_CRITICAL(&touch_mux);
  if (down && !touch.down) {
    touch.presses++;
    touch.press_x = x;
    touch.press_y = y;
  }
  touch.x = x;
  touch.y = y;
  touch.down = down;
  taskEXIT_CRITICAL(&touch_mux);
}

void touchTask(void *) {
  TickType_t wake = xTaskGetTickCount();
  for (;;) {
    pollTouch();
    vTaskDelayUntil(&wake, pdMS_TO_TICKS(kTouchPollMs));
  }
}

}  // namespace

bool startTouch() {
  if (!gt911_found) return false;  // nothing to poll
  i2c_lock = xSemaphoreCreateMutex();
  if (!i2c_lock) return false;
  // Core 0: away from LVGL and the panel's refills on core 1. Its stack and the I2C
  // driver's buffers stay in internal RAM (the task isn't the loop, so psram_first.h's
  // preference doesn't apply to it).
  touch_task = xTaskCreatePinnedToCore(touchTask, "touch", 4096, nullptr, kTouchPriority, nullptr, 0) == pdPASS;
  return touch_task;
}

bool readTouch(int16_t &x, int16_t &y) {
  if (!touch_task && gt911_found) pollTouch();  // the old way, on the loop
  taskENTER_CRITICAL(&touch_mux);
  const Touch t = touch;
  taskEXIT_CRITICAL(&touch_mux);
  // A press LVGL hasn't seen yet: shown where it began, even if the finger has lifted
  // since (its next read then sees the lift, and the tap still counts as a click).
  if (t.presses != presses_seen) {
    presses_seen = t.presses;
    x = t.down ? t.x : t.press_x;
    y = t.down ? t.y : t.press_y;
    return true;
  }
  x = t.x;
  y = t.y;
  return t.down;
}

bool keepFirmware() {
  const esp_partition_t *running = esp_ota_get_running_partition();
  esp_ota_img_states_t state;
  if (!running || esp_ota_get_state_partition(running, &state) != ESP_OK || state != ESP_OTA_IMG_PENDING_VERIFY) return false;
  return esp_ota_mark_app_valid_cancel_rollback() == ESP_OK;
}

}  // namespace board
