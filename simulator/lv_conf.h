/* LVGL's configuration for the simulator: the firmware's own (firmware/include/lv_conf.h),
 * with the system heap instead of the PSRAM allocator, and LVGL's checks and warnings on,
 * so misuse (a deleted object, a NULL style, a failed allocation) stops the simulator with
 * a message instead of passing unnoticed. */
#ifndef SIM_LV_CONF_H
#define SIM_LV_CONF_H

#include "../firmware/include/lv_conf.h"

#undef LV_USE_STDLIB_MALLOC
#define LV_USE_STDLIB_MALLOC LV_STDLIB_CLIB

#undef LV_USE_LOG
#define LV_USE_LOG 1
#undef LV_LOG_LEVEL
#define LV_LOG_LEVEL LV_LOG_LEVEL_WARN
#undef LV_LOG_PRINTF
#define LV_LOG_PRINTF 1

#undef LV_USE_ASSERT_NULL
#define LV_USE_ASSERT_NULL 1
#undef LV_USE_ASSERT_MALLOC
#define LV_USE_ASSERT_MALLOC 1
#undef LV_USE_ASSERT_STYLE
#define LV_USE_ASSERT_STYLE 1
#undef LV_USE_ASSERT_OBJ
#define LV_USE_ASSERT_OBJ 1

/* LVGL 9.6 replaced LV_ASSERT_HANDLER_INCLUDE (and warns with #warning, which MSVC's C
 * doesn't know) by LV_ASSERT_CUSTOM_INCLUDE. */
#undef LV_ASSERT_HANDLER_INCLUDE
#undef LV_ASSERT_CUSTOM_INCLUDE
#define LV_ASSERT_CUSTOM_INCLUDE <stdlib.h>
#undef LV_ASSERT_HANDLER
#define LV_ASSERT_HANDLER _exit(70);

#endif
