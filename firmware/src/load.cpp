#include "load.h"

#include <Arduino.h>
#include <stdlib.h>
#include <string.h>

#include "display.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "pc.h"

namespace load {
namespace {

constexpr int kCores = 2;
constexpr int kParts = int(Part::Count);
constexpr uint32_t kSlowPassUs = 50 * 1000;  // a pass this long holds the link and touch up visibly
constexpr int64_t kSecondUs = 1000 * 1000;
constexpr int kMaxTasks = 48;

// The pass under way.
uint32_t pass_us[kParts];
uint32_t pass_touch_us = 0, pass_touch_reads = 0, pass_worst_touch_us = 0;
uint32_t pass_start_us = 0;  // micros() when it began
uint32_t vsync_seen_us = 0;  // display::vsyncWaitUs() then
bool started = false;
bool skip_pass = false;

// The loop since the last report.
struct {
  uint32_t passes;
  uint64_t loop_us;
  uint64_t part_us[kParts];
  uint64_t touch_us;
  uint32_t touch_reads;
  uint32_t worst_touch_us;
  uint64_t vsync_us;
  uint32_t slow_passes;
  uint32_t worst_us;
  uint32_t worst_part_us[kParts];
  uint32_t worst_vsync_us;
} win;

// Each core's load, a second at a time: what its idle task didn't get is what the core did.
struct {
  int64_t at_us;  // esp_timer at the last sample
  uint32_t idle[kCores];
  uint64_t elapsed_us;  // the samples since the report
  uint64_t idle_us[kCores];
  uint32_t busiest[kCores];  // the busiest whole second, in tenths of a percent
} cores;

// Each task's run time at the last report, so the next one counts only what came since.
struct Seen {
  TaskHandle_t handle;
  uint32_t run;
};
Seen seen[kMaxTasks];
int seen_count = 0;
int64_t report_at_us = 0;  // esp_timer at the last report; 0 is the start

uint32_t permille(uint64_t part, uint64_t whole) {
  if (!whole) return 0;
  const uint64_t p = part * 1000 / whole;
  return p > 1000 ? 1000 : uint32_t(p);
}

void baselineCores(int64_t now) {
  cores.at_us = now;
  for (int c = 0; c < kCores; c++) cores.idle[c] = ulTaskGetIdleRunTimeCounterForCore(c);
}

// A sample once a second has gone by; `partial` takes the part-second up to now too (for a
// report), without letting it count as the busiest second.
void sampleCores(bool partial) {
  const int64_t now = esp_timer_get_time();
  if (!cores.at_us) {
    baselineCores(now);
    return;
  }
  const int64_t elapsed = now - cores.at_us;
  if (elapsed <= 0 || (!partial && elapsed < kSecondUs)) return;
  cores.elapsed_us += uint64_t(elapsed);
  for (int c = 0; c < kCores; c++) {
    const uint32_t idle_now = ulTaskGetIdleRunTimeCounterForCore(c);
    const uint32_t idle = idle_now - cores.idle[c];  // the counter is 32-bit microseconds
    cores.idle[c] = idle_now;
    cores.idle_us[c] += idle;
    const uint32_t busy = 1000 - permille(idle, uint64_t(elapsed));
    if (elapsed >= kSecondUs && busy > cores.busiest[c]) cores.busiest[c] = busy;
  }
  cores.at_us = now;
}

void commitPass(uint32_t us, uint32_t vsync_us) {
  win.passes++;
  win.loop_us += us;
  for (int i = 0; i < kParts; i++) win.part_us[i] += pass_us[i];
  win.touch_us += pass_touch_us;
  win.touch_reads += pass_touch_reads;
  if (pass_worst_touch_us > win.worst_touch_us) win.worst_touch_us = pass_worst_touch_us;
  win.vsync_us += vsync_us;
  if (us > kSlowPassUs) win.slow_passes++;
  if (us > win.worst_us) {
    win.worst_us = us;
    memcpy(win.worst_part_us, pass_us, sizeof(pass_us));
    win.worst_vsync_us = vsync_us;
  }
}

// "12.3%" from tenths of a percent.
struct Pct {
  char s[8];
  explicit Pct(uint32_t pm) { snprintf(s, sizeof(s), "%u.%u%%", unsigned(pm / 10), unsigned(pm % 10)); }
};

unsigned ms(uint64_t us) { return unsigned((us + 500) / 1000); }

void logCores(uint64_t window_us) {
  const Pct c0(1000 - permille(cores.idle_us[0], cores.elapsed_us)), c1(1000 - permille(cores.idle_us[1], cores.elapsed_us));
  const Pct b0(cores.busiest[0]), b1(cores.busiest[1]);
  pc::log("load: over %u s, core 0 busy %s (busiest second %s), core 1 busy %s (busiest second %s)",
          unsigned(window_us / kSecondUs), c0.s, b0.s, c1.s, b1.s);
}

void logLoop() {
  if (!win.passes) {
    pc::log("load: the loop hasn't run a whole pass since the last report");
    return;
  }
  const uint64_t busy = win.part_us[0] + win.part_us[1] + win.part_us[2];
  const uint64_t rest = win.loop_us > busy ? win.loop_us - busy : 0;
  const Pct link(permille(win.part_us[int(Part::Link)], win.loop_us)), ui(permille(win.part_us[int(Part::Ui)], win.loop_us)),
      lvgl(permille(win.part_us[int(Part::Lvgl)], win.loop_us)), touch(permille(win.touch_us, win.loop_us)),
      vsync(permille(win.vsync_us, win.loop_us)), other(permille(rest, win.loop_us));
  pc::log("load: loop %u passes, %u us avg; link %s, ui %s, lvgl %s (touch %s, vsync wait %s), delay and the rest %s",
          unsigned(win.passes), unsigned(win.loop_us / win.passes), link.s, ui.s, lvgl.s, touch.s, vsync.s, other.s);
  pc::log("load: worst pass %u ms (link %u, ui %u, lvgl %u incl. vsync %u); %u over %u ms; touch %u reads, %u us avg, "
          "%u us worst",
          ms(win.worst_us), ms(win.worst_part_us[int(Part::Link)]), ms(win.worst_part_us[int(Part::Ui)]),
          ms(win.worst_part_us[int(Part::Lvgl)]), ms(win.worst_vsync_us), unsigned(win.slow_passes),
          unsigned(kSlowPassUs / 1000), unsigned(win.touch_reads),
          unsigned(win.touch_reads ? win.touch_us / win.touch_reads : 0), unsigned(win.worst_touch_us));
}

// Every task: its share of one core since the last report, the core it's pinned to ("*" for
// either) and its priority, and the least stack it has had free.
void logTasks(uint64_t window_us) {
  const UBaseType_t room = uxTaskGetNumberOfTasks() + 8;  // in case some start meanwhile
  auto *tasks = static_cast<TaskStatus_t *>(malloc(room * sizeof(TaskStatus_t)));
  if (!tasks) {
    pc::log("load: no memory for the task list");
    return;
  }
  // Walks every task (and its stack, for the free space) with the scheduler held: timed, so
  // the cost of asking is known before it's asked with a radio running.
  const uint32_t t0 = micros();
  const UBaseType_t n = uxTaskGetSystemState(tasks, room, nullptr);
  const uint32_t walk_us = micros() - t0;

  uint32_t delta[kMaxTasks + 8];
  const int count = int(n) < kMaxTasks ? int(n) : kMaxTasks;
  for (int i = 0; i < count; i++) {
    uint32_t before = 0;  // a task first seen counts from its start
    for (int k = 0; k < seen_count; k++) {
      if (seen[k].handle == tasks[i].xHandle) {
        before = seen[k].run;
        break;
      }
    }
    delta[i] = tasks[i].ulRunTimeCounter - before;
  }
  // The busiest first.
  for (int i = 1; i < count; i++) {
    for (int k = i; k > 0 && delta[k] > delta[k - 1]; k--) {
      const TaskStatus_t t = tasks[k];
      tasks[k] = tasks[k - 1];
      tasks[k - 1] = t;
      const uint32_t d = delta[k];
      delta[k] = delta[k - 1];
      delta[k - 1] = d;
    }
  }

  char line[160];
  int len = snprintf(line, sizeof(line), "load: %u tasks (core/priority, cpu, stack free), listed in %u us:", unsigned(n),
                     unsigned(walk_us));
  for (int i = 0; i < count; i++) {
    const TaskStatus_t &t = tasks[i];
    char core[4];
    if (t.xCoreID == tskNO_AFFINITY) strcpy(core, "*");
    else snprintf(core, sizeof(core), "%d", int(t.xCoreID));
    const Pct cpu(permille(delta[i], window_us));
    char entry[64];
    const int e = snprintf(entry, sizeof(entry), " %s %s/%u %s %.1fK;", t.pcTaskName, core, unsigned(t.uxCurrentPriority),
                           cpu.s, t.usStackHighWaterMark / 1024.0f);  // bytes on ESP-IDF
    if (len + e >= int(sizeof(line)) - 1) {
      pc::log("%s", line);
      len = snprintf(line, sizeof(line), "load: tasks cont.:");
    }
    strcpy(line + len, entry);
    len += e;
  }
  pc::log("%s", line);
  // A task's counter is 32-bit microseconds: one busy for over 71 minutes of a window turns over.
  if (window_us > 60ull * 60 * kSecondUs) pc::log("load: the window is over an hour; the busiest tasks' shares may be off");
  free(tasks);
}

// Every task's run time now, where the next report starts counting: after the benchmark's
// redraws, so they count nowhere.
void rebaseTasks() {
  const UBaseType_t room = uxTaskGetNumberOfTasks() + 8;
  auto *tasks = static_cast<TaskStatus_t *>(malloc(room * sizeof(TaskStatus_t)));
  if (!tasks) return;
  const UBaseType_t n = uxTaskGetSystemState(tasks, room, nullptr);
  seen_count = int(n) < kMaxTasks ? int(n) : kMaxTasks;
  for (int i = 0; i < seen_count; i++) seen[i] = {tasks[i].xHandle, tasks[i].ulRunTimeCounter};
  free(tasks);
  report_at_us = esp_timer_get_time();
}

}  // namespace

void add(Part part, uint32_t us) { pass_us[int(part)] += us; }

void touch(uint32_t us) {
  pass_touch_us += us;
  pass_touch_reads++;
  if (us > pass_worst_touch_us) pass_worst_touch_us = us;
}

void endPass() {
  const uint32_t now = micros();
  const uint32_t vsync = display::vsyncWaitUs();
  if (started && !skip_pass) commitPass(now - pass_start_us, vsync - vsync_seen_us);
  if (skip_pass) {  // the benchmark's redraws count nowhere
    baselineCores(esp_timer_get_time());
    rebaseTasks();
  } else {
    sampleCores(false);
  }
  started = true;
  skip_pass = false;
  pass_start_us = now;
  vsync_seen_us = vsync;
  memset(pass_us, 0, sizeof(pass_us));
  pass_touch_us = pass_touch_reads = pass_worst_touch_us = 0;
}

void report() {
  sampleCores(true);
  const int64_t now = esp_timer_get_time();
  const uint64_t window_us = uint64_t(now - report_at_us);
  logCores(cores.elapsed_us);
  logLoop();
  logTasks(window_us);

  win = {};
  cores.elapsed_us = 0;
  memset(cores.idle_us, 0, sizeof(cores.idle_us));
  memset(cores.busiest, 0, sizeof(cores.busiest));
  skip_pass = true;
}

}  // namespace load
