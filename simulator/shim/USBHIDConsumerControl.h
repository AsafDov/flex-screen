// Media keys; reported like the keyboard's, as {"t":"sim.media","usage":code}.
#pragma once

#include <Arduino.h>

#define CONSUMER_CONTROL_SCAN_NEXT 0x00B5
#define CONSUMER_CONTROL_SCAN_PREVIOUS 0x00B6
#define CONSUMER_CONTROL_PLAY_PAUSE 0x00CD
#define CONSUMER_CONTROL_MUTE 0x00E2

class USBHIDConsumerControl {
 public:
  void begin() {}
  size_t press(uint16_t usage);
  size_t release() { return 1; }
};
