// Firmware updates over the link, kept in memory: the protocol and the deck's progress
// screen run as on the board. end() checks the size and the image's first byte (0xE9, an
// ESP32 app image), not the MD5; the firmware's ESP.restart() then starts the simulator
// again, still the same build, as a deck whose new firmware didn't stay would.
#pragma once

#include <Arduino.h>

#include <vector>

#define U_FLASH 0

class UpdateClass {
 public:
  bool begin(size_t size, int = U_FLASH) {
    abort();
    if (size == 0) return fail("Bad Size Given");
    size_ = size;
    data_.reserve(size);
    running_ = true;
    error_ = "No Error";
    return true;
  }
  bool setMD5(const char *md5) { return md5 && strlen(md5) == 32; }
  size_t write(uint8_t *data, size_t n) {
    if (!running_) return fail("Not running"), 0;
    if (data_.empty() && n && data[0] != 0xE9) return abort(), fail("Wrong Magic Byte"), 0;
    if (data_.size() + n > size_) return abort(), fail("Write Failed"), 0;
    data_.insert(data_.end(), data, data + n);
    return n;
  }
  bool end(bool = false) {
    if (!running_ || data_.size() != size_) return abort(), fail("Premature End");
    running_ = false;
    return true;
  }
  void abort() {
    running_ = false;
    data_.clear();
  }
  bool isRunning() const { return running_; }
  const char *errorString() const { return error_; }

 private:
  bool fail(const char *why) {
    error_ = why;
    return false;
  }
  std::vector<uint8_t> data_;
  size_t size_ = 0;
  bool running_ = false;
  const char *error_ = "No Error";
};
extern UpdateClass Update;
