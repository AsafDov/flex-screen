// The USB device descriptor setup: nothing to do in the simulator.
#pragma once

#include <Arduino.h>

class ESPUSB {
 public:
  void VID(uint16_t) {}
  void PID(uint16_t) {}
  void manufacturerName(const char *) {}
  void productName(const char *) {}
  bool begin() { return true; }
};
extern ESPUSB USB;
