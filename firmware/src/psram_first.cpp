#include "psram_first.h"

#include <stddef.h>

#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

namespace {

constexpr uint32_t kPsram = MALLOC_CAP_DEFAULT | MALLOC_CAP_SPIRAM;
constexpr uint32_t kInternal = MALLOC_CAP_DEFAULT | MALLOC_CAP_INTERNAL;

TaskHandle_t psram_task = nullptr;
int paused = 0;  // only ever changed on psram_task itself

// Before the scheduler runs there's no current task; nothing of ours allocates then anyway.
inline bool ours() { return psram_task && !paused && xTaskGetCurrentTaskHandle() == psram_task; }

}  // namespace

extern "C" {

void *__real_malloc(size_t size);
void *__real_calloc(size_t n, size_t size);
void *__real_realloc(void *p, size_t size);

void *__wrap_malloc(size_t size) {
  return ours() ? heap_caps_malloc_prefer(size, 2, kPsram, kInternal) : __real_malloc(size);
}

void *__wrap_calloc(size_t n, size_t size) {
  return ours() ? heap_caps_calloc_prefer(n, size, 2, kPsram, kInternal) : __real_calloc(n, size);
}

// A block from either memory may be resized from either kind of task: heap_caps_realloc
// moves it between memories when it has to, and frees it for a size of 0.
void *__wrap_realloc(void *p, size_t size) {
  return ours() ? heap_caps_realloc_prefer(p, size, 2, kPsram, kInternal) : __real_realloc(p, size);
}

}  // extern "C"

namespace psram {

void preferForThisTask() { psram_task = xTaskGetCurrentTaskHandle(); }
void pause() { paused++; }
void resume() { paused--; }

}  // namespace psram
