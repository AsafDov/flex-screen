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
Approval approval;
Question question;
Usage usage;
Picker picker;
Git git;
Toast toast;
FirmwareUpdate firmware_update;
Settings settings;
Pc pc;
Call call;
Windows windows;
Keyboard keyboard;
Keymap keymap;
Clips clips;
Obsidian obsidian;
Google google;
Tasks tasks;
TaskDetail task_detail;
Panels panels;
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
  const Event *next = nullptr;
  for (int i = 0; i < event_count; i++) {
    const Event &e = events[i];
    if (!e.all_day && e.end > now && (!next || e.start < next->start)) next = &e;
  }
  return next;
}

const char *Tasks::listName(const String &src, const String &id) const {
  const TaskList *l = listOf(src, id);
  return l ? l->name.c_str() : "";
}

const TaskList *Tasks::listOf(const String &src, const String &id) const {
  for (int i = 0; i < list_count; i++) {
    if (lists[i].src == src && lists[i].id == id) return &lists[i];
  }
  return nullptr;
}

bool Tasks::canTick(const Task &t) const {
  const TaskList *l = listOf(t.src, t.list);
  return !l || l->tick;
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
