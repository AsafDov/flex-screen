#include "idle.h"

#include <Arduino.h>
#include <lvgl.h>
#include <time.h>

#include "board.h"
#include "display.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::idle {
namespace {

enum class State { Awake, Clock, Off };

struct {
  lv_obj_t *root, *clock, *date, *meeting;
} w;

State state = State::Awake;
uint32_t woke_ms = 0;   // a tap that woke the deck counts as activity from here
int64_t clock_seen = -1;
uint32_t meeting_tick_ms = 0;

void onWake(lv_event_t *) {
  woke_ms = millis();
  // The press already went to the clock screen, so the page below never sees it.
}

// Seconds since the last sign of someone at the desk: a touch on the deck, or input on
// the PC (the companion's report, aged since it arrived).
uint32_t idleSecs() {
  const uint32_t touch_ms = display::lastTouchMs();
  const uint32_t since_touch = (millis() - (touch_ms > woke_ms ? touch_ms : woke_ms)) / 1000;
  if (!pc::connected()) return since_touch;
  const model::Pc &p = model::pc;
  const uint32_t since_pc = p.idle_s + (millis() - p.at_ms) / 1000;
  return since_touch < since_pc ? since_touch : since_pc;
}

void showClock() {
  const int64_t now = model::localNow();
  if (now == 0) {
    setText(w.clock, "--:--");
    setText(w.date, "");
  } else if (now / 60 != clock_seen) {
    clock_seen = now / 60;
    const time_t t = time_t(now);
    struct tm tm;
    gmtime_r(&t, &tm);
    char buf[32];
    strftime(buf, sizeof(buf), "%H:%M", &tm);
    setText(w.clock, buf);
    strftime(buf, sizeof(buf), "%A, %e %B", &tm);
    setText(w.date, buf);
  }
  if (millis() - meeting_tick_ms < 15000) return;
  meeting_tick_ms = millis();
  const int64_t utc = model::utcNow();
  const model::Event *e = pc::connected() && model::google.status == "ok" && utc ? model::google.next(utc) : nullptr;
  if (!e) {
    setText(w.meeting, "");
    return;
  }
  const int64_t mins = (e->start - utc + 59) / 60;
  if (e->start <= utc) setTextf(w.meeting, LV_SYMBOL_BELL "  %s · now", e->title.c_str());
  else if (mins < 60) setTextf(w.meeting, LV_SYMBOL_BELL "  %s · in %d min", e->title.c_str(), int(mins));
  else {
    const time_t t = time_t(e->start + int64_t(model::clock.tz_offset_min) * 60);
    struct tm tm;
    gmtime_r(&t, &tm);
    char at[8];
    strftime(at, sizeof(at), "%H:%M", &tm);
    setTextf(w.meeting, LV_SYMBOL_BELL "  %s · %s", e->title.c_str(), at);
  }
}

void enter(State next) {
  if (next == state) return;
  const State was = state;
  state = next;
  lv_obj_set_hidden(w.root, next == State::Awake);
  if (next == State::Off) board::backlight(false);
  if (was == State::Off) board::backlight(true);
  if (next != State::Awake) {
    clock_seen = -1;
    meeting_tick_ms = 0;
    showClock();
  }
  pc::log("idle: %s", next == State::Awake ? "awake" : next == State::Clock ? "clock" : "screen off");
}

}  // namespace

void build() {
  w.root = box(lv_layer_top());
  lv_obj_set_size(w.root, LV_PCT(100), LV_PCT(100));
  lv_obj_set_style_bg_color(w.root, hex(0x000000), 0);
  lv_obj_set_style_bg_opa(w.root, LV_OPA_COVER, 0);
  lv_obj_set_clickable(w.root, true);
  lv_obj_add_event_cb(w.root, onWake, LV_EVENT_PRESSED, nullptr);
  flexCol(w.root, 6);
  lv_obj_set_flex_align(w.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  // Dim grey on black: the backlight can't be dimmed on this board, only switched off.
  w.clock = label(w.root, "", &lv_font_montserrat_48, 0x8A929C);
  w.date = label(w.root, "", &body_font, 0x5A626C);
  w.meeting = label(w.root, "", &body_font, 0x5A7FA8);
  lv_obj_set_style_pad_top(w.meeting, 18, 0);
  lv_obj_set_hidden(w.root, true);
}

void wake() { woke_ms = millis(); }

bool asleep() { return state != State::Awake; }

void update() {
  const model::Settings &s = model::settings;
  const uint32_t idle = idleSecs();
  const bool locked = pc::connected() && model::pc.locked;
  // A touch while dark or on the clock wakes everything.
  if (state != State::Awake && millis() - woke_ms < 500) {
    enter(State::Awake);
    return;
  }
  State next = State::Awake;
  if (s.off_after_s && idle >= s.off_after_s) next = State::Off;
  else if (locked || (s.clock_after_s && idle >= s.clock_after_s)) next = State::Clock;
  // Once awake by touch, stay awake until idle again, even while the PC stays locked.
  if (locked && next == State::Clock && millis() - woke_ms < 1000UL * (s.clock_after_s ? s.clock_after_s : 60)) {
    next = State::Awake;
  }
  enter(next);
  if (state != State::Awake) showClock();
}

}  // namespace ui::idle
