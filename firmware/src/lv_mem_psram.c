/* LVGL's heap, split between PSRAM and internal RAM (lv_conf.h: LV_USE_STDLIB_MALLOC is
 * LV_STDLIB_CUSTOM).
 *
 * Widgets are small blocks that plain malloc keeps in internal RAM, which the USB stack
 * and the task stacks need: the Dev page alone took ~38 KB of it. So widgets, styles and
 * texts go to PSRAM (8 MB). While a frame renders, though, LVGL allocates and frees draw
 * tasks and scratch buffers by the hundred; in PSRAM those made page changes crawl, so
 * they come from internal RAM, where they live only until the frame is done. Either way an
 * allocation falls back to the other kind of memory if its own runs out. */
#include "lvgl.h"

#if LV_USE_STDLIB_MALLOC == LV_STDLIB_CUSTOM

#include "esp_heap_caps.h"
#include "lv_mem_psram.h"

#define PSRAM_CAPS (MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT)
#define INTERNAL_CAPS (MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT)

static volatile bool rendering = false;

void lv_heap_set_rendering(bool on) { rendering = on; }

static uint32_t first_caps(void) { return rendering ? INTERNAL_CAPS : PSRAM_CAPS; }
static uint32_t other_caps(void) { return rendering ? PSRAM_CAPS : INTERNAL_CAPS; }

void lv_mem_init(void) {}

void lv_mem_deinit(void) {}

lv_mem_pool_t lv_mem_add_pool(void *mem, size_t bytes) {
  LV_UNUSED(mem);
  LV_UNUSED(bytes);
  return NULL;
}

void lv_mem_remove_pool(lv_mem_pool_t pool) { LV_UNUSED(pool); }

void *lv_malloc_core(size_t size) {
  void *p = heap_caps_malloc(size, first_caps());
  return p ? p : heap_caps_malloc(size, other_caps());
}

void *lv_realloc_core(void *p, size_t new_size) {
  void *q = heap_caps_realloc(p, new_size, first_caps());
  return q ? q : heap_caps_realloc(p, new_size, other_caps());
}

void lv_free_core(void *p) { heap_caps_free(p); }

void lv_mem_monitor_core(lv_mem_monitor_t *mon_p) { LV_UNUSED(mon_p); }

lv_result_t lv_mem_test_core(void) { return LV_RESULT_OK; }

#endif /* LV_USE_STDLIB_MALLOC == LV_STDLIB_CUSTOM */
