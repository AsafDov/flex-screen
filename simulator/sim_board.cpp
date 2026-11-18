// The board in the simulator: no panel or expander; the touch is the window's mouse (or a
// test's "sim.touch"), the backlight just blanks the window.
#include "board.h"
#include "sim.h"

namespace board {

namespace {
bool lit = false;
}

bool begin() { return true; }
esp_lcd_panel_handle_t panel() { return nullptr; }
bool recreatePanel() { return true; }
void backlight(bool on) {
  lit = on;
  sim::setBacklight(on);
}
bool backlightOn() { return lit; }
bool readTouch(int16_t &x, int16_t &y) { return sim::touch(x, y); }
TouchInfo touchInfo() { return {}; }
bool keepFirmware() { return false; }  // no app slots here: nothing is ever on trial

}  // namespace board
