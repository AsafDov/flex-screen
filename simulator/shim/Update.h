// Firmware updates over the link: not in the simulator (every call fails politely).
#pragma once

#include <Arduino.h>

#define U_FLASH 0

class UpdateClass {
 public:
  bool begin(size_t, int = U_FLASH) { return false; }
  size_t write(uint8_t *, size_t) { return 0; }
  bool end(bool = false) { return false; }
  void abort() {}
  bool isRunning() const { return false; }
  const char *errorString() const { return "not in the simulator"; }
};
extern UpdateClass Update;
