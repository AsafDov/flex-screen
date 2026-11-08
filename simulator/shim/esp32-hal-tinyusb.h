#pragma once

typedef enum {
  RESTART_NO_PERSIST,
  RESTART_PERSIST,
  RESTART_BOOTLOADER,
  RESTART_BOOTLOADER_DFU,
} restart_type_t;

inline void usb_persist_restart(restart_type_t) {}  // no bootloader to reboot into
