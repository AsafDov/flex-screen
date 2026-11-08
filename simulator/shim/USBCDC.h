// The deck's USB serial link to the companion. In the simulator it's a TCP server on
// localhost (sim_runtime.cpp): whatever connects plays the companion, line for line the
// same JSON. Lines whose "t" starts with "sim." are the simulator's own (touches, quit)
// and never reach the firmware.
#pragma once

#include <Arduino.h>

typedef const char *esp_event_base_t;
typedef void (*esp_event_handler_t)(void *arg, esp_event_base_t base, int32_t id, void *data);

typedef enum {
  ARDUINO_USB_CDC_ANY_EVENT = -1,
  ARDUINO_USB_CDC_CONNECTED_EVENT = 0,
  ARDUINO_USB_CDC_DISCONNECTED_EVENT,
  ARDUINO_USB_CDC_LINE_STATE_EVENT,
  ARDUINO_USB_CDC_LINE_CODING_EVENT,
  ARDUINO_USB_CDC_RX_EVENT,
  ARDUINO_USB_CDC_TX_EVENT,
  ARDUINO_USB_CDC_RX_OVERFLOW_EVENT,
} arduino_usb_cdc_event_t;

typedef union {
  struct {
    uint32_t bit_rate;
    uint8_t stop_bits;
    uint8_t parity;
    uint8_t data_bits;
  } line_coding;
  struct {
    size_t dropped_bytes;
  } rx_overflow;
} arduino_usb_cdc_event_data_t;

class USBCDC {
 public:
  void begin(unsigned long = 0) {}
  void setRxBufferSize(size_t) {}
  void enableReboot(bool) {}
  void onEvent(arduino_usb_cdc_event_t, esp_event_handler_t) {}
  int available();
  size_t read(uint8_t *buffer, size_t size);
  size_t write(uint8_t c) { return write(&c, 1); }
  size_t write(const uint8_t *buffer, size_t size);
  void flush() {}
  operator bool() const;  // a "companion" is connected (not explicit: `bool open = cdc;`, as in Arduino)
};
