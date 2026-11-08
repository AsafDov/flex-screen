// NVS key-value storage; the simulator keeps it in a file next to the exe, so a palette
// survives the restart that applies it.
#pragma once

#include <Arduino.h>

class Preferences {
 public:
  bool begin(const char *name, bool read_only = false);
  void end() {}
  String getString(const char *key, const String &fallback = String());
  size_t putString(const char *key, const String &value);

 private:
  std::string ns_;
};
