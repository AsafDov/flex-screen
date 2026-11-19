#include "sessions.h"

#include <Arduino.h>

#include "fonts/font_icons.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::sessions {
namespace {

constexpr int kX = kPaneX, kY = kPaneY, kW = kPaneW, kH = kPaneH;  // right of the rail, above the context bar
constexpr int kUsageW = 224;  // beside the rail: at 256 the rows cut "VS Code · Sonnet 4.5" beside Stop
constexpr int kRowH = 56;  // five in the list's height
constexpr int kRows = model::Claude::kMax;
constexpr int kSlots = 60;  // the 5-hour window in 5-minute steps
constexpr int kSlotSecs = 5 * 3600 / kSlots;
constexpr uint32_t kHintMs = 1500;

struct Row {
  lv_obj_t *root, *dot, *project, *meta, *detail, *time, *stop, *stop_label, *bar;
};

struct {
  lv_obj_t *scrim, *panel, *summary, *list, *empty;
  Row rows[kRows];
  lv_obj_t *pct, *week, *chart, *resets, *pace, *note;
  lv_chart_series_t *used, *ahead;
} w;

uint32_t claude_seen = UINT32_MAX, usage_seen = UINT32_MAX;
int link_seen = -1;
uint32_t tick_ms = 0;
int hint_row = -1;
uint32_t hint_ms = 0;

bool isOpen() { return w.panel && !lv_obj_has_flag(w.panel, LV_OBJ_FLAG_HIDDEN); }

void close() {
  lv_obj_set_hidden(w.scrim, true);
  lv_obj_set_hidden(w.panel, true);
}

void onClose(lv_event_t *) { close(); }

int rowOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

void onRow(lv_event_t *e) {
  const int i = rowOf(e);
  if (i < model::claude.count) pc::openClaude(model::claude.sessions[i].id.c_str());
}

// A tap only says how: stopping a session is too easy to do by mistake.
void onStopTap(lv_event_t *e) {
  const int i = rowOf(e);
  if (i >= model::claude.count || model::claude.sessions[i].stopping) return;
  hint_row = i;
  hint_ms = millis();
  setText(w.rows[i].stop_label, "Hold");
}

void onStopHold(lv_event_t *e) {
  const int i = rowOf(e);
  if (i >= model::claude.count) return;
  pc::stopClaude(model::claude.sessions[i].id.c_str());
  setText(w.rows[i].stop_label, "Stopping");
  hint_row = -1;
}

// "12:40" for a UTC time, in the PC's time zone.
void clockText(char *buf, size_t n, int64_t utc) {
  const int64_t local = utc + int64_t(model::clock.tz_offset_min) * 60;
  const int minutes = int(((local % 86400) + 86400) % 86400 / 60);
  snprintf(buf, n, "%d:%02d", minutes / 60, minutes % 60);
}

void stateLook(const String &state, uint32_t &dot, uint32_t &detail) {
  if (state == "input") dot = kAmber, detail = kAmber;
  else if (state == "working") dot = kAccent, detail = kSoftText;
  else if (state == "done") dot = kGreen, detail = kMuted;
  else dot = kMuted, detail = kMuted;
}

void buildRow(int i) {
  Row &r = w.rows[i];
  r.root = button(w.list, LV_PCT(100), kRowH, kRaised, onRow, (void *)intptr_t(i));
  lv_obj_set_style_radius(r.root, 12, 0);
  lv_obj_set_style_pad_hor(r.root, 12, 0);
  flexRow(r.root, 10);

  r.dot = box(r.root);
  lv_obj_set_size(r.dot, 10, 10);
  lv_obj_set_style_radius(r.dot, 5, 0);
  lv_obj_set_style_bg_opa(r.dot, LV_OPA_COVER, 0);

  lv_obj_t *text = box(r.root);
  lv_obj_set_size(text, 1, LV_SIZE_CONTENT);
  lv_obj_set_flex_grow(text, 1);
  flexCol(text, 2);
  lv_obj_t *line = box(text);
  lv_obj_set_size(line, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(line, 8);
  r.project = label(line, "", &body_font, kText);
  r.meta = label(line, "", &small_font, kMuted);
  fillRow(r.meta);
  r.detail = label(text, "", &small_font, kSoftText);
  ellipsize(r.detail);

  r.time = label(r.root, "", &small_font, kMuted);

  r.stop = button(r.root, 84, 40, kBg, nullptr);
  lv_obj_set_style_radius(r.stop, 10, 0);
  lv_obj_add_event_cb(r.stop, onStopTap, LV_EVENT_SHORT_CLICKED, (void *)intptr_t(i));
  lv_obj_add_event_cb(r.stop, onStopHold, LV_EVENT_LONG_PRESSED, (void *)intptr_t(i));
  r.stop_label = label(r.stop, "Stop", &small_font, kText);
  lv_obj_center(r.stop_label);

  r.bar = lv_bar_create(r.root);
  lv_obj_add_flag(r.bar, LV_OBJ_FLAG_FLOATING);  // along the row's foot, out of the row
  lv_obj_remove_flag(r.bar, LV_OBJ_FLAG_CLICKABLE);
  lv_obj_set_size(r.bar, LV_PCT(100), 3);
  lv_obj_align(r.bar, LV_ALIGN_BOTTOM_MID, 0, -3);
  lv_obj_set_style_bg_color(r.bar, hex(kTrack), 0);
  lv_obj_set_style_bg_color(r.bar, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_hidden(r.bar, true);
}

void buildUsage(lv_obj_t *parent) {
  lv_obj_t *c = card(parent, kBg, kBorder);
  lv_obj_set_size(c, kUsageW, LV_PCT(100));
  lv_obj_set_style_pad_all(c, 12, 0);
  flexCol(c, 6);
  label(c, "5-HOUR USAGE", &small_font, kMuted);

  lv_obj_t *figures = box(c);
  lv_obj_set_size(figures, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(figures, 8);
  lv_obj_set_flex_align(figures, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_END);
  w.pct = label(figures, "", &lv_font_montserrat_28, kText);
  w.week = label(figures, "", &small_font, kMuted);

  w.chart = lv_chart_create(c);
  lv_obj_set_size(w.chart, LV_PCT(100), 120);
  lv_chart_set_type(w.chart, LV_CHART_TYPE_LINE);
  lv_chart_set_point_count(w.chart, kSlots);
  lv_chart_set_axis_range(w.chart, LV_CHART_AXIS_PRIMARY_Y, 0, 100);
  lv_chart_set_div_line_count(w.chart, 5, 0);  // 0, 25, 50, 75, 100%
  lv_obj_set_style_bg_opa(w.chart, LV_OPA_TRANSP, 0);
  lv_obj_set_style_border_width(w.chart, 0, 0);
  lv_obj_set_style_pad_all(w.chart, 2, 0);
  lv_obj_set_style_line_color(w.chart, hex(kTrack), 0);  // the grid
  lv_obj_set_style_line_width(w.chart, 3, LV_PART_ITEMS);
  lv_obj_set_style_width(w.chart, 0, LV_PART_INDICATOR);  // no dots on the line
  lv_obj_set_style_height(w.chart, 0, LV_PART_INDICATOR);
  lv_obj_remove_flag(w.chart, LV_OBJ_FLAG_CLICKABLE);
  w.ahead = lv_chart_add_series(w.chart, hex(kMuted), LV_CHART_AXIS_PRIMARY_Y);
  w.used = lv_chart_add_series(w.chart, hex(kAccent), LV_CHART_AXIS_PRIMARY_Y);

  w.resets = label(c, "", &small_font, kSoftText);
  w.pace = label(c, "", &small_font, kMuted);
  lv_obj_set_width(w.pace, LV_PCT(100));
  lv_label_set_long_mode(w.pace, LV_LABEL_LONG_MODE_WRAP);
  w.note = label(c, "", &small_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);
}

void showSessions() {
  const model::Claude &c = model::claude;
  int needs = 0, working = 0;
  for (int i = 0; i < kRows; i++) {
    Row &r = w.rows[i];
    const bool used = i < c.count;
    setHidden(r.root, !used);
    if (!used) continue;
    const model::ClaudeSession &s = c.sessions[i];
    needs += s.state == "input";
    working += s.state == "working";
    uint32_t dot, detail;
    stateLook(s.state, dot, detail);
    setBgColor(r.dot, dot);
    setTextUpTo(r.project, s.project.length() ? s.project.c_str() : "Claude", 240);
    String meta = s.app;
    if (s.model.length()) meta += (meta.length() ? " · " : "") + s.model;
    setText(r.meta, meta.c_str());

    String text;
    if (s.stopping) text = "Stopping before its next step";
    else if (s.total > 0) text = (s.progress.length() ? s.progress + " · " : String()) + String(s.done) + " of " + String(s.total);
    else if (s.detail.length()) text = s.detail;
    else text = s.state == "idle" ? "Ready" : s.state == "done" ? "Finished" : "Working";
    if (s.state == "input" && !s.stopping && !text.startsWith("Asks")) text = "Needs you · " + text;
    setText(r.detail, text.c_str());
    setTextColor(r.detail, s.stopping ? kAmber : detail);

    char ago[24];
    formatAgo(ago, sizeof(ago), model::agedSecs(s.secs, c.at_ms));
    // "3 min ago" reads as when it last did something; the board wants how long it's been at it.
    String time = ago;
    if (time.endsWith(" ago")) time.remove(time.length() - 4);
    if (time == "just now") time = "now";
    setText(r.time, time.c_str());

    const bool stoppable = s.state == "working" || s.state == "input";
    setHidden(r.stop, !stoppable);
    if (stoppable && hint_row != i) setText(r.stop_label, s.stopping ? "Stopping" : "Stop");
    setTextColor(r.stop_label, s.stopping ? kMuted : kText);

    const bool progress = s.total > 0;
    setHidden(r.bar, !progress);
    if (progress) {
      lv_bar_set_range(r.bar, 0, s.total);
      lv_bar_set_value(r.bar, s.done, LV_ANIM_OFF);
    }
  }
  setHidden(w.empty, c.count > 0);
  if (!c.count) setText(w.summary, "");
  else if (needs) setTextf(w.summary, "%d session%s · %d need%s you", c.count, c.count == 1 ? "" : "s", needs, needs == 1 ? "s" : "");
  else setTextf(w.summary, "%d session%s · %d working", c.count, c.count == 1 ? "" : "s", working);
}

void showUsage() {
  const model::Usage &u = model::usage;
  const bool known = u.five >= 0;
  setHidden(w.chart, !known);
  setHidden(w.resets, !known || !u.resets);
  setHidden(w.pace, !known);
  setHidden(w.note, known);
  if (!known) {
    setText(w.pct, "–");
    setText(w.week, "");
    setText(w.note, "Shows once Claude Code is signed in on this PC (or a server's hooks report it).");
    return;
  }
  setTextf(w.pct, "%d%%", u.five);
  setTextColor(w.pct, u.five >= 90 ? 0xFF9A70 : u.five >= 75 ? kAmber : kText);
  if (u.week >= 0) setTextf(w.week, "week %d%%", u.week);
  else setText(w.week, "");

  const int64_t now = model::utcNow();
  char at[16];
  if (u.resets) {
    clockText(at, sizeof(at), u.resets);
    char left[24];
    formatAgo(left, sizeof(left), int32_t(u.resets - now));
    String until = left;
    if (until.endsWith(" ago")) until.remove(until.length() - 4);
    setTextf(w.resets, "Resets at %s · in %s", at, until.c_str());
  }
  if (u.eta) {
    clockText(at, sizeof(at), u.eta);
    setTextf(w.pace, "At this pace you hit the limit around %s", at);
    setTextColor(w.pace, kAmber);
  } else if (u.rate >= 0.5f) {
    setText(w.pace, "At this pace it lasts until the reset");
    setTextColor(w.pace, kGreen);
  } else {
    setText(w.pace, u.count >= 2 ? "Barely used in the last hour" : "The pace shows after a few readings");
    setTextColor(w.pace, kMuted);
  }

  // The chart: readings by 5-minute step, carried forward to now; then the pace, ahead.
  lv_chart_set_all_values(w.chart, w.used, LV_CHART_POINT_NONE);
  lv_chart_set_all_values(w.chart, w.ahead, LV_CHART_POINT_NONE);
  if (!u.start || !now) return;
  const int now_slot = constrain(int((now - u.start) / kSlotSecs), 0, kSlots - 1);
  int value = -1, p = 0;
  for (int slot = 0; slot <= now_slot; slot++) {
    while (p < u.count && u.points[p].secs < (slot + 1) * kSlotSecs) value = u.points[p++].pct;
    if (value >= 0) lv_chart_set_series_value_by_id(w.chart, w.used, slot, value);
  }
  if (value < 0) value = u.five;
  lv_chart_set_series_value_by_id(w.chart, w.used, now_slot, u.five);
  if (u.rate >= 0.5f) {
    for (int slot = now_slot; slot < kSlots; slot++) {
      const float ahead = u.five + u.rate * float(slot - now_slot) * kSlotSecs / 3600.0f;
      lv_chart_set_series_value_by_id(w.chart, w.ahead, slot, int32_t(ahead > 100 ? 100 : ahead));
      if (ahead >= 100) break;
    }
  }
  lv_chart_refresh(w.chart);
}

}  // namespace

void build() {
  // Taps beside the panel close it rather than reaching the page underneath.
  w.scrim = box(lv_layer_top());
  lv_obj_set_size(w.scrim, 800, 480);
  lv_obj_set_style_bg_color(w.scrim, hex(0x000000), 0);
  lv_obj_set_style_bg_opa(w.scrim, LV_OPA_50, 0);
  lv_obj_set_clickable(w.scrim, true);
  lv_obj_add_event_cb(w.scrim, onClose, LV_EVENT_CLICKED, nullptr);

  w.panel = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_pos(w.panel, kX, kY);
  lv_obj_set_size(w.panel, kW, kH);
  lv_obj_set_style_radius(w.panel, 18, 0);
  lv_obj_set_style_pad_all(w.panel, 12, 0);
  lv_obj_set_style_shadow_width(w.panel, 30, 0);
  lv_obj_set_style_shadow_color(w.panel, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.panel, LV_OPA_60, 0);
  lv_obj_set_clickable(w.panel, true);  // a tap between rows stays on the panel
  flexCol(w.panel, 10);

  lv_obj_t *head = box(w.panel);
  lv_obj_set_size(head, LV_PCT(100), 34);
  flexRow(head, 12);
  label(head, "Claude sessions", &title_font, kText);
  w.summary = label(head, "", &small_font, kMuted);
  fillRow(w.summary);
  lv_obj_t *x = lv_obj_get_parent(iconButton(head, 48, 34, kSurface, LV_SYMBOL_CLOSE, kMuted, onClose));
  lv_obj_set_ext_click_area(x, 6);

  lv_obj_t *body = box(w.panel);
  lv_obj_set_width(body, LV_PCT(100));
  lv_obj_set_flex_grow(body, 1);
  flexRow(body, 10);
  lv_obj_set_flex_align(body, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);

  w.list = box(body);
  lv_obj_set_size(w.list, 1, LV_PCT(100));
  lv_obj_set_flex_grow(w.list, 1);
  flexCol(w.list, 6);
  lv_obj_add_flag(w.list, LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_set_scroll_dir(w.list, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(w.list, LV_SCROLLBAR_MODE_AUTO);
  lv_obj_set_clickable(w.list, true);  // a drag between rows still scrolls
  for (int i = 0; i < kRows; i++) buildRow(i);
  w.empty = label(w.list, "No Claude sessions. They show here from the Claude app, VS Code, terminals and Remote-SSH.",
                  &body_font, kMuted);
  lv_obj_set_width(w.empty, LV_PCT(100));
  lv_label_set_long_mode(w.empty, LV_LABEL_LONG_MODE_WRAP);

  buildUsage(body);
  close();
}

void open() {
  claude_seen = usage_seen = UINT32_MAX;  // fresh on opening
  hint_row = -1;
  lv_obj_set_hidden(w.scrim, false);
  lv_obj_set_hidden(w.panel, false);
  lv_obj_scroll_to_y(w.list, 0, LV_ANIM_OFF);
  update();
}

void update() {
  if (!isOpen()) return;
  const int online = pc::connected();
  if (!online) {
    close();  // nothing to show or do offline
    return;
  }
  if (hint_row >= 0 && millis() - hint_ms > kHintMs) {
    hint_row = -1;
    claude_seen = UINT32_MAX;
  }
  // Times count on: once a second while open.
  const bool tick = millis() - tick_ms >= 1000;
  if (tick) tick_ms = millis();
  if (tick || model::claude.version != claude_seen || online != link_seen) {
    claude_seen = model::claude.version;
    showSessions();
  }
  if (model::usage.version != usage_seen || (tick && millis() / 1000 % 30 == 0)) {
    usage_seen = model::usage.version;
    showUsage();
  }
  link_seen = online;
}

}  // namespace ui::sessions
