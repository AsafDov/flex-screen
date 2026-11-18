// Waveshare ESP32-S3-Touch-LCD-7 hardware: RGB panel, GT911 touch, CH422G expander.
// Pins and timings come from Espressif's ESP32_Display_Panel board file for this board.
#pragma once

#include <stdint.h>
#include "esp_lcd_types.h"

namespace board {

constexpr int kWidth = 800;
constexpr int kHeight = 480;

// Brings up I2C, the expander, the panel (two frame buffers in PSRAM) and touch.
// Leaves the backlight off; turn it on once the first frame is drawn.
bool begin();

esp_lcd_panel_handle_t panel();
// Deletes the panel and creates it again, with new (black) frame buffers: the RGB driver's
// state from scratch. See display::resyncSoon() for why. False if it couldn't.
bool recreatePanel();
void backlight(bool on);
bool backlightOn();

// Polls the touch controller. Returns true while a finger is down; x/y keep the last
// position so a release still reports where it happened.
bool readTouch(int16_t &x, int16_t &y);

// The GT911's power and scan settings, read once at begin().
struct TouchInfo {
  bool ok = false;
  uint8_t config_version = 0;  // 0x8047
  uint8_t low_power = 0;       // 0x8055: idle seconds before it slows its scanning
  uint8_t refresh = 0;         // 0x8056: report interval is 5 + this, in ms
};
TouchInfo touchInfo();

// Keeps the running firmware if it came as an update and is still on trial (see main.cpp):
// the chip's bootloader stops going back to the previous one. True if it was on trial.
bool keepFirmware();

}  // namespace board
