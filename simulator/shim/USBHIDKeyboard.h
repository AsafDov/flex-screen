// The deck as a USB keyboard. The simulator reports each key to whatever is connected as
// {"t":"sim.key","k":code,"down":bool}, so tests can check the numpad.
#pragma once

#include "USBCDC.h"

#define KEY_LEFT_GUI 0x83
#define KEY_UP_ARROW 0xDA
#define KEY_DOWN_ARROW 0xD9
#define KEY_LEFT_ARROW 0xD8
#define KEY_RIGHT_ARROW 0xD7
#define KEY_BACKSPACE 0xB2
#define KEY_TAB 0xB3
#define KEY_ESC 0xB1
#define KEY_HOME 0xD2
#define KEY_END 0xD5
#define KEY_NUM_LOCK 0xDB
#define KEY_KP_SLASH 0xDC
#define KEY_KP_ASTERISK 0xDD
#define KEY_KP_MINUS 0xDE
#define KEY_KP_PLUS 0xDF
#define KEY_KP_ENTER 0xE0
#define KEY_KP_1 0xE1
#define KEY_KP_2 0xE2
#define KEY_KP_3 0xE3
#define KEY_KP_4 0xE4
#define KEY_KP_5 0xE5
#define KEY_KP_6 0xE6
#define KEY_KP_7 0xE7
#define KEY_KP_8 0xE8
#define KEY_KP_9 0xE9
#define KEY_KP_0 0xEA
#define KEY_KP_DOT 0xEB

typedef enum {
  ARDUINO_USB_HID_KEYBOARD_ANY_EVENT = -1,
  ARDUINO_USB_HID_KEYBOARD_LED_EVENT = 0,
} arduino_usb_hid_keyboard_event_t;

typedef union {
  struct {
    uint8_t numlock : 1;
    uint8_t capslock : 1;
    uint8_t scrolllock : 1;
    uint8_t compose : 1;
    uint8_t kana : 1;
    uint8_t reserved : 3;
  };
  uint8_t leds;
} arduino_usb_hid_keyboard_event_data_t;

class USBHIDKeyboard {
 public:
  void begin() {}
  void onEvent(arduino_usb_hid_keyboard_event_t, esp_event_handler_t) {}
  size_t press(uint8_t k);
  size_t release(uint8_t k);
  void releaseAll();
  size_t print(const char *text);
};
