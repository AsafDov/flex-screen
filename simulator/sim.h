// The simulator's own plumbing, shared by its runtime, board, display and main.
#pragma once

#include <stdint.h>

namespace sim {

// The "USB" link: a TCP server on 127.0.0.1:port. Whatever connects plays the companion.
void startLink(int port);
void pollLink();       // accepts, reads, and handles the simulator's own "sim.*" lines
void closeLink();
int linkPort();

// One line to the connected client (no newline needed), for the simulator's own reports.
void sendLine(const char *json);

// The touch panel: the window's mouse, or a "sim.touch" line from a test.
void setTouch(bool down, int x, int y);
bool touch(int16_t &x, int16_t &y);

void setBacklight(bool on);
bool backlight();

// Counts frames LVGL finished, so the window repaints and tests can wait for a still screen.
void framePresented();
uint32_t frames();

bool quitRequested();
void requestQuit();

}  // namespace sim
