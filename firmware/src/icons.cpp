#include "icons.h"

#include "esp_heap_caps.h"
#include "model.h"

namespace icons {
namespace {

// Room for everything that can be on screen at once (8 windows, 5 Recent tiles, 12
// shortcuts) and as many again: a slot is only reused for an icon nothing listed shows, so
// no page draws one that's been replaced.
constexpr int kSlots = 50;

struct Slot {
  String id;
  uint8_t *pixels = nullptr;
  lv_image_dsc_t dsc = {};
  uint32_t stored = 0;  // arrival order, for evicting the oldest
};

Slot slots[kSlots];
uint32_t arrivals = 0;
uint32_t changes = 0;

bool listed(const String &id) {
  for (int i = 0; i < model::windows.count; i++) {
    if (model::windows.list[i].icon == id) return true;
  }
  for (int i = 0; i < model::actions.count; i++) {
    if (model::actions.list[i].icon == id) return true;
  }
  for (int i = 0; i < model::shortcuts.count; i++) {
    if (model::shortcuts.list[i].icon == id) return true;
  }
  return false;
}

Slot *freeSlot() {
  Slot *oldest = nullptr;
  for (Slot &s : slots) {
    if (!s.pixels) return &s;
    if (listed(s.id)) continue;
    if (!oldest || s.stored < oldest->stored) oldest = &s;
  }
  return oldest;
}

}  // namespace

void put(const char *id, uint8_t *pixels, int w, int h) {
  for (Slot &s : slots) {
    if (s.pixels && s.id == id) {  // already here
      heap_caps_free(pixels);
      return;
    }
  }
  Slot *s = freeSlot();
  if (!s) {  // every slot shows something listed; can't happen with 25 listed and 50 slots
    heap_caps_free(pixels);
    return;
  }
  if (s->pixels) heap_caps_free(s->pixels);
  s->id = id;
  s->pixels = pixels;
  s->stored = arrivals++;
  s->dsc = {};
  s->dsc.header.magic = LV_IMAGE_HEADER_MAGIC;
  s->dsc.header.cf = LV_COLOR_FORMAT_RGB565A8;
  s->dsc.header.w = w;
  s->dsc.header.h = h;
  s->dsc.header.stride = w * 2;  // of the RGB565 plane; the alpha plane follows it
  s->dsc.data_size = uint32_t(w) * h * 3;
  s->dsc.data = pixels;
  changes++;
}

const lv_image_dsc_t *get(const String &id) {
  if (id.length() == 0) return nullptr;
  for (Slot &s : slots) {
    if (s.pixels && s.id == id) return &s.dsc;
  }
  return nullptr;
}

uint32_t version() { return changes; }

}  // namespace icons
