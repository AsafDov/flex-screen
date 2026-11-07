// LVGL's heap (lv_mem_psram.c): PSRAM normally, internal RAM while a frame renders.
#pragma once

#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

void lv_heap_set_rendering(bool on);

#ifdef __cplusplus
}
#endif
