#include "diag.h"

#include <Arduino.h>
#include <lvgl.h>
#include <time.h>

#include "display.h"
#include "esp_heap_caps.h"
#include "idle.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::diag {
namespace {

// Where the sessions board sits: under the status bar, over the pages and the tab bar.
constexpr int kX = 8, kY = kStatusH + 4, kW = 800 - 2 * kX, kH = 480 - kY - 8;  // the whole width: it's a tool
constexpr int kPad = 12, kCardGap = 10;
constexpr int kCardW = (kW - 2 * kPad - 2 * kCardGap) / 3;
constexpr int kTopH = 120;
constexpr int kCounters = 6;
constexpr int kSecs = 60;  // the frame figures cover the last minute, a second at a time
constexpr uint32_t kLowRam = 40 * 1024;  // internal RAM this low is worth a second look

// The frames drawn in one second.
struct Second {
  uint32_t frames;
  uint32_t draw_us;  // all of them together, without the waits for vsync
  uint32_t slowest_us;
};
Second seconds[kSecs];
int sec_at = 0;  // the second being counted now
uint32_t sec_start_ms = 0;

// One LVGL refresh, from its start: whether it drew anything, and the vsync wait so far.
uint32_t refr_t0 = 0, refr_wait0 = 0;
bool rendered = false;

uint64_t up_ms = 0;  // millis() runs over after 49 days; this doesn't
uint32_t up_seen_ms = 0;

struct Counter {
  lv_obj_t *value, *name;
};

struct {
  lv_obj_t *scrim, *panel;
  lv_obj_t *fw, *fw_detail;
  lv_obj_t *uptime, *uptime_detail;
  lv_obj_t *fps, *fps_detail, *draw_detail;
  lv_obj_t *ram, *ram_low, *ram_block, *psram;
  lv_obj_t *link_dot, *link_state, *last_error;
  Counter counters[kCounters];
} w;

uint32_t tick_ms = 0;

bool isOpen() { return w.panel && !lv_obj_is_hidden(w.panel); }

void close() {
  lv_obj_set_hidden(w.scrim, true);
  lv_obj_set_hidden(w.panel, true);
}

void onClose(lv_event_t *) { close(); }

// LVGL's refresh timer runs every few ms and mostly finds nothing to draw: only refreshes
// that drew count as frames. Their time includes the layout and copying the other buffer
// up to date, but not the wait for vsync (display.cpp's flush), which isn't work.
void onRefreshStart(lv_event_t *) {
  refr_t0 = micros();
  refr_wait0 = display::vsyncWaitUs();
  rendered = false;
}

void onRenderStart(lv_event_t *) { rendered = true; }

void onRefreshReady(lv_event_t *) {
  if (!rendered) return;
  const uint32_t us = micros() - refr_t0 - (display::vsyncWaitUs() - refr_wait0);
  Second &s = seconds[sec_at];
  s.frames++;
  s.draw_us += us;
  if (us > s.slowest_us) s.slowest_us = us;
}

// Moves on to the next second when one is over (several, after a stall).
void countTime() {
  const uint32_t now = millis();
  up_ms += now - up_seen_ms;
  up_seen_ms = now;
  if (now - sec_start_ms >= uint32_t(kSecs) * 1000) {  // stalled for a minute or more
    memset(seconds, 0, sizeof(seconds));
    sec_start_ms = now;
    return;
  }
  while (now - sec_start_ms >= 1000) {
    sec_start_ms += 1000;
    sec_at = (sec_at + 1) % kSecs;
    seconds[sec_at] = {};
  }
}

// "45 s", "12 min 5 s", "3 h 12 min", "4 d 2 h".
void formatSpan(char *buf, size_t n, uint64_t ms) {
  const uint32_t s = uint32_t(ms / 1000);
  if (s < 60) snprintf(buf, n, "%u s", unsigned(s));
  else if (s < 3600) snprintf(buf, n, "%u min %u s", unsigned(s / 60), unsigned(s % 60));
  else if (s < 86400) snprintf(buf, n, "%u h %u min", unsigned(s / 3600), unsigned(s / 60 % 60));
  else snprintf(buf, n, "%u d %u h", unsigned(s / 86400), unsigned(s / 3600 % 24));
}

// "980", "12.3k", "4.1M": a counter in a narrow cell.
void formatCount(char *buf, size_t n, uint32_t v) {
  if (v < 10000) snprintf(buf, n, "%u", unsigned(v));
  else if (v < 1000000) snprintf(buf, n, "%.1fk", v / 1000.0f);
  else snprintf(buf, n, "%.1fM", v / 1000000.0f);
}

// "142 KB", "6.2 MB".
void formatBytes(char *buf, size_t n, size_t bytes) {
  if (bytes < 1024 * 1024) snprintf(buf, n, "%u KB", unsigned(bytes / 1024));
  else snprintf(buf, n, "%.1f MB", bytes / (1024.0f * 1024.0f));
}

// A card with a caption, a big figure and room for detail lines under it.
lv_obj_t *figureCard(lv_obj_t *parent, int width, int height, const char *caption, lv_obj_t **figure) {
  lv_obj_t *c = card(parent, kBg, kBorder);
  lv_obj_set_size(c, width, height);
  flexCol(c, 4);
  label(c, caption, &small_font, kMuted);
  *figure = label(c, "", &lv_font_montserrat_28, kText);
  return c;
}

lv_obj_t *detailLine(lv_obj_t *parent, uint32_t color = kSoftText) {
  lv_obj_t *l = label(parent, "", &small_font, color);
  ellipsize(l);
  return l;
}

void buildLink(lv_obj_t *parent, int width, int height) {
  lv_obj_t *c = card(parent, kBg, kBorder);
  lv_obj_set_size(c, width, height);
  flexCol(c, 10);
  label(c, "LINK TO THE PC", &small_font, kMuted);

  lv_obj_t *state = box(c);
  lv_obj_set_size(state, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(state, 8);
  w.link_dot = box(state);
  lv_obj_set_size(w.link_dot, 10, 10);
  lv_obj_set_style_radius(w.link_dot, 5, 0);
  lv_obj_set_style_bg_opa(w.link_dot, LV_OPA_COVER, 0);
  w.link_state = label(state, "", &title_font, kText);
  fillRow(w.link_state);

  static const char *const kNames[kCounters] = {"Lines", "Bad lines", "Cut lines", "Bytes lost", "Bad parts", "Drops"};
  lv_obj_t *row = card(c, kSurface, kSurface);
  lv_obj_set_size(row, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_pad_ver(row, 10, 0);
  lv_obj_set_style_pad_hor(row, 6, 0);
  flexRow(row, 0);
  for (int i = 0; i < kCounters; i++) {
    lv_obj_t *cell = box(row);
    lv_obj_set_size(cell, 1, LV_SIZE_CONTENT);
    lv_obj_set_flex_grow(cell, 1);
    flexCol(cell, 2);
    lv_obj_set_flex_align(cell, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    w.counters[i].value = label(cell, "0", &title_font, kText);
    w.counters[i].name = label(cell, kNames[i], &small_font, kMuted);
  }

  w.last_error = label(c, "", &small_font, kSoftText);
  lv_obj_set_width(w.last_error, LV_PCT(100));
  lv_label_set_long_mode(w.last_error, LV_LABEL_LONG_MODE_WRAP);
}

void showFirmware() {
  setText(w.fw, pc::kFirmwareVersion);
  const char *reason = pc::resetReason();
  // On this board a restart the firmware asks for (an update, a new palette) ends in a plain
  // "watchdog" reset: the ROM stalls after the restart and ESP-IDF's 1 s RTC watchdog resets
  // the chip (DESIGN.md, 0.24.3). That's the normal way back, not a hang.
  if (!strcmp(reason, "watchdog")) setText(w.fw_detail, "Restarted itself (watchdog)");
  else setTextf(w.fw_detail, "Last restart: %s", reason);
  // A crash, a hang or a power dip is what's worth noticing. A hang shows as the task or
  // interrupt watchdog, never as the plain one above.
  const bool crashed = !strcmp(reason, "panic") || !strcmp(reason, "task watchdog") ||
                       !strcmp(reason, "interrupt watchdog") || !strcmp(reason, "brownout");
  setTextColor(w.fw_detail, crashed ? kAmber : kSoftText);
}

void showUptime() {
  char span[32];
  formatSpan(span, sizeof(span), up_ms);
  setText(w.uptime, span);
  const int64_t now = model::localNow();
  if (now == 0) {
    setText(w.uptime_detail, "Started before the PC set the clock");
    return;
  }
  const time_t start = time_t(now - int64_t(up_ms / 1000));
  struct tm tm;
  gmtime_r(&start, &tm);  // already shifted to local time
  char at[24];
  strftime(at, sizeof(at), now / 86400 == start / 86400 ? "%H:%M" : "%a %e %b, %H:%M", &tm);
  setTextf(w.uptime_detail, "Since %s", at);
}

void showFrames() {
  const Second &last = seconds[(sec_at + kSecs - 1) % kSecs];
  uint32_t peak = 0, frames = 0, slowest = 0;
  uint64_t draw = 0;
  for (const Second &s : seconds) {
    if (s.frames > peak) peak = s.frames;
    frames += s.frames;
    draw += s.draw_us;
    if (s.slowest_us > slowest) slowest = s.slowest_us;
  }
  // LVGL only draws what changed, so a still screen reads 0-1 fps: the busiest second says
  // what the deck manages while something moves.
  setTextf(w.fps, "%u fps", unsigned(last.frames));
  setTextf(w.fps_detail, "Up to %u fps · last minute", unsigned(peak));
  if (frames) {
    setTextf(w.draw_detail, "Draw %u ms avg, %u ms worst", unsigned((draw / frames + 500) / 1000),
             unsigned((slowest + 500) / 1000));
  } else {
    setText(w.draw_detail, "Nothing drawn in the last minute");
  }
}

void showMemory() {
  const size_t ram = heap_caps_get_free_size(MALLOC_CAP_INTERNAL);
  const size_t lowest = heap_caps_get_minimum_free_size(MALLOC_CAP_INTERNAL);
  char a[16], b[16];
  formatBytes(a, sizeof(a), ram);
  setText(w.ram, a);
  setTextColor(w.ram, ram < kLowRam ? kAmber : kText);
  formatBytes(a, sizeof(a), lowest);
  setTextf(w.ram_low, "Internal RAM, lowest %s", a);
  formatBytes(a, sizeof(a), heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL));
  setTextf(w.ram_block, "Largest free block %s", a);
  formatBytes(a, sizeof(a), heap_caps_get_free_size(MALLOC_CAP_SPIRAM));
  formatBytes(b, sizeof(b), heap_caps_get_minimum_free_size(MALLOC_CAP_SPIRAM));
  setTextf(w.psram, "PSRAM %s, lowest %s", a, b);
}

void showLink() {
  const pc::LinkStats &s = pc::linkStats();
  const bool up = pc::connected();
  char span[32];
  if (up) {
    formatSpan(span, sizeof(span), millis() - s.up_ms);
    setTextf(w.link_state, "Connected for %s", span);
  } else if (s.down_ms) {
    formatSpan(span, sizeof(span), millis() - s.down_ms);
    setTextf(w.link_state, "Offline for %s", span);
  } else {
    setText(w.link_state, "Waiting for the companion");
  }
  setBgColor(w.link_dot, up ? kGreen : 0x5A626C);
  setTextColor(w.link_state, up ? kText : kMuted);

  const uint32_t values[kCounters] = {s.lines, s.bad_lines, s.long_lines, s.lost_bytes, s.bad_parts, s.drops};
  for (int i = 0; i < kCounters; i++) {
    char v[16];
    formatCount(v, sizeof(v), values[i]);
    setText(w.counters[i].value, v);
    setTextColor(w.counters[i].value, i > 0 && values[i] ? kAmber : kText);  // every one but Lines is trouble
  }

  if (!s.error_ms) {
    setText(w.last_error, "No errors since the deck started.");
    setTextColor(w.last_error, kMuted);
    return;
  }
  char ago[24];
  formatAgo(ago, sizeof(ago), int32_t((millis() - s.error_ms) / 1000));
  setTextf(w.last_error, "Last error, %s: %s", ago, s.error);
  setTextColor(w.last_error, kSoftText);
}

void show() {
  showFirmware();
  showUptime();
  showFrames();
  showMemory();
  showLink();
}

}  // namespace

void build() {
  lv_display_t *disp = lv_display_get_default();
  lv_display_add_event_cb(disp, onRefreshStart, LV_EVENT_REFR_START, nullptr);
  lv_display_add_event_cb(disp, onRenderStart, LV_EVENT_RENDER_START, nullptr);
  lv_display_add_event_cb(disp, onRefreshReady, LV_EVENT_REFR_READY, nullptr);
  sec_start_ms = up_seen_ms = millis();
  up_ms = millis();  // the deck has been up since millis() was 0

  // Taps beside the panel close it rather than reaching the page underneath.
  w.scrim = box(lv_layer_top());
  lv_obj_set_size(w.scrim, 800, 480);
  lv_obj_set_clickable(w.scrim, true);
  lv_obj_add_event_cb(w.scrim, onClose, LV_EVENT_CLICKED, nullptr);

  w.panel = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_pos(w.panel, kX, kY);
  lv_obj_set_size(w.panel, kW, kH);
  lv_obj_set_style_radius(w.panel, 18, 0);
  lv_obj_set_style_pad_all(w.panel, kPad, 0);
  lv_obj_set_style_shadow_width(w.panel, 30, 0);
  lv_obj_set_style_shadow_color(w.panel, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.panel, LV_OPA_60, 0);
  lv_obj_set_clickable(w.panel, true);  // a tap between the cards stays on the panel
  flexCol(w.panel, kCardGap);

  lv_obj_t *head = box(w.panel);
  lv_obj_set_size(head, LV_PCT(100), 34);
  flexRow(head, 12);
  label(head, "Deck diagnostics", &title_font, kText);
  lv_obj_t *note = label(head, "Counted since the deck started", &small_font, kMuted);
  fillRow(note);
  lv_obj_t *x = lv_obj_get_parent(iconButton(head, 48, 34, kSurface, LV_SYMBOL_CLOSE, kMuted, onClose));
  lv_obj_set_ext_click_area(x, 6);

  lv_obj_t *top = box(w.panel);
  lv_obj_set_size(top, LV_PCT(100), kTopH);
  flexRow(top, kCardGap);
  lv_obj_t *c = figureCard(top, kCardW, kTopH, "FIRMWARE", &w.fw);
  w.fw_detail = detailLine(c);
  c = figureCard(top, kCardW, kTopH, "UPTIME", &w.uptime);
  w.uptime_detail = detailLine(c);
  c = figureCard(top, kCardW, kTopH, "FRAME RATE", &w.fps);
  w.fps_detail = detailLine(c);
  w.draw_detail = detailLine(c, kMuted);

  lv_obj_t *bottom = box(w.panel);
  lv_obj_set_width(bottom, LV_PCT(100));
  lv_obj_set_flex_grow(bottom, 1);
  flexRow(bottom, kCardGap);
  lv_obj_set_flex_align(bottom, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  const int bottom_h = kH - 2 * kPad - 34 - kTopH - 2 * kCardGap;
  c = figureCard(bottom, kCardW, bottom_h, "FREE MEMORY", &w.ram);
  w.ram_low = detailLine(c);
  w.ram_block = detailLine(c);
  lv_obj_t *gap = box(c);
  lv_obj_set_size(gap, 1, 6);
  w.psram = detailLine(c);
  buildLink(bottom, kW - 2 * kPad - kCardW - kCardGap, bottom_h);

  close();
}

void open() {
  lv_obj_set_hidden(w.scrim, false);
  lv_obj_set_hidden(w.panel, false);
  tick_ms = millis();
  show();
}

void update() {
  countTime();
  if (!isOpen()) return;
  if (idle::asleep()) {  // woken up, it's the page again
    close();
    return;
  }
  // Everything here moves on its own (the uptime by the second): once a second.
  if (millis() - tick_ms < 1000) return;
  tick_ms = millis();
  show();
}

}  // namespace ui::diag
