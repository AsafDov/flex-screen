#include "model.h"

namespace model {

Media media;
Volume volume;
Mixer mixer;
Devices devices;
Mic mic;
Art art;
Claude claude;
Term term;
Build build;
Recent recent;
Actions actions;
Shortcuts shortcuts;
Context context;
Git git;
Toast toast;
Settings settings;
Pc pc;
Call call;
Windows windows;
Keyboard keyboard;
Keymap keymap;
Clips clips;
Obsidian obsidian;
Google google;
uint32_t bench_requests = 0;
Zones zones;
Clock clock;

int32_t agedSecs(int32_t secs, uint32_t at_ms) {
  return secs < 0 ? -1 : secs + int32_t((millis() - at_ms) / 1000);
}

float Media::positionNow() const {
  float p = pos;
  if (playing) p += (millis() - pos_at_ms) / 1000.0f;
  return len > 0 && p > len ? len : p;
}

const Device *Devices::selectedOutput() const {
  for (int i = 0; i < out_count; i++) {
    if (out[i].selected) return &out[i];
  }
  return nullptr;
}

const Event *Google::next(int64_t now) const {
  for (int i = 0; i < event_count; i++) {
    if (!events[i].all_day && events[i].end > now) return &events[i];
  }
  return nullptr;
}

int64_t utcNow() {
  if (clock.epoch == 0) return 0;
  return clock.epoch + (millis() - clock.synced_at_ms) / 1000;
}

int64_t localNow() {
  if (clock.epoch == 0) return 0;
  const int64_t elapsed = (millis() - clock.synced_at_ms) / 1000;
  return clock.epoch + elapsed + int64_t(clock.tz_offset_min) * 60;
}

}  // namespace model
