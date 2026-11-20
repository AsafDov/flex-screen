#include "dayview.h"

#include <time.h>

#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::day {
namespace {

constexpr int kPanelW = 640, kPanelH = 420, kPad = 18, kHeadH = 40;
constexpr uint32_t kTickMs = 30000;  // "in 25 min", and the now line, move on

struct {
  lv_obj_t *scrim, *panel, *date, *allday, *body;
} w;

bool shown = false;
uint32_t google_seen = UINT32_MAX, tick_ms = 0;
int connected_seen = -1;

int64_t tzSeconds() { return int64_t(model::clock.tz_offset_min) * 60; }

// The local day an instant falls on (days since 1970-01-01), and that day's span in UTC.
int64_t localDay(int64_t utc) {
  const int64_t local = utc + tzSeconds();
  return local >= 0 ? local / 86400 : (local - 86399) / 86400;
}
int64_t dayStart(int64_t day) { return day * 86400 - tzSeconds(); }

// "14:05" in the PC's time zone.
void clockText(int64_t utc, char *out, size_t n) {
  const time_t t = time_t(utc + tzSeconds());
  struct tm tm;
  gmtime_r(&t, &tm);
  strftime(out, n, "%H:%M", &tm);
}

// "Tue 17 Nov" for a local day.
void dayText(int64_t day, char *out, size_t n) {
  const time_t t = time_t(day * 86400);
  struct tm tm;
  gmtime_r(&t, &tm);
  char weekday[8], month[8];
  strftime(weekday, sizeof(weekday), "%a", &tm);
  strftime(month, sizeof(month), "%b", &tm);
  snprintf(out, n, "%s %d %s", weekday, tm.tm_mday, month);
}

void onClose(lv_event_t *) { close(); }

void onJoin(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::google.event_count) pc::joinMeeting(model::google.events[i].id.c_str());
  close();
}

lv_obj_t *heading(const char *text, uint32_t color) {
  lv_obj_t *l = label(w.body, text, &small_font, color);
  lv_obj_set_style_pad_top(l, 10, 0);
  lv_obj_set_style_pad_bottom(l, 2, 0);
  return l;
}

lv_obj_t *note(const char *text) {
  lv_obj_t *l = label(w.body, text, &body_font, kMuted);
  lv_obj_set_width(l, LV_PCT(100));
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_WRAP);
  lv_obj_set_style_pad_ver(l, 6, 0);
  return l;
}

// The line between what's over and what's to come, with the time.
lv_obj_t *nowLine(int64_t now) {
  lv_obj_t *r = box(w.body);
  lv_obj_set_size(r, LV_PCT(100), 20);
  flexRow(r, 8);
  char at[8];
  clockText(now, at, sizeof(at));
  char text[16];
  snprintf(text, sizeof(text), "Now %s", at);
  label(r, text, &small_font, kAccent);
  lv_obj_t *line = box(r);
  lv_obj_set_height(line, 2);
  lv_obj_set_flex_grow(line, 1);
  lv_obj_set_style_bg_color(line, hex(kAccent), 0);
  lv_obj_set_style_bg_opa(line, LV_OPA_COVER, 0);
  return r;
}

// One event: its times, its title and where it stands (or its calendar), and Join while it
// has a meeting link and isn't over.
void row(int i, int64_t now, bool all_day) {
  const model::Event &e = model::google.events[i];
  const bool over = !all_day && e.end <= now;
  const bool going = !all_day && e.start <= now && now < e.end;
  lv_obj_t *r = card(w.body, going ? kHighlight : kSurface, going ? kHighlightLine : kSurface);
  lv_obj_set_size(r, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_min_height(r, 52, 0);
  lv_obj_set_style_pad_ver(r, 4, 0);
  lv_obj_set_style_pad_hor(r, 10, 0);
  lv_obj_set_style_radius(r, 12, 0);
  if (e.color) {
    // Its calendar's color, down the left edge (Google's and Outlook's own).
    lv_obj_set_style_border_side(r, LV_BORDER_SIDE_LEFT, 0);
    lv_obj_set_style_border_width(r, 4, 0);
    lv_obj_set_style_border_color(r, hex(e.color), 0);
    lv_obj_set_style_border_opa(r, over ? LV_OPA_40 : LV_OPA_COVER, 0);
  }
  flexRow(r, 12);

  lv_obj_t *times = box(r);
  lv_obj_set_size(times, 56, LV_SIZE_CONTENT);
  flexCol(times, 0);
  if (all_day) {
    label(times, "All day", &small_font, over ? kMuted : kSoftText);
  } else {
    char from[8], to[8];
    clockText(e.start, from, sizeof(from));
    clockText(e.end, to, sizeof(to));
    label(times, from, &body_font, over ? kMuted : kText);
    label(times, to, &small_font, kMuted);
  }

  lv_obj_t *text = box(r);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  lv_obj_set_flex_grow(text, 1);
  flexCol(text, 0);
  lv_obj_t *title = label(text, e.title.c_str(), &body_font, over ? kMuted : kText);
  ellipsize(title);
  char sub[96];
  size_t n = 0;
  sub[0] = '\0';
  auto add = [&](const char *part) {
    if (!part || !part[0] || n >= sizeof(sub) - 1) return;
    n += snprintf(sub + n, sizeof(sub) - n, "%s%s", n ? "  ·  " : "", part);
  };
  char when[32] = "";
  if (going) {
    char to[8];
    clockText(e.end, to, sizeof(to));
    snprintf(when, sizeof(when), "Now, until %s", to);
  } else if (!all_day && !over && e.start - now < 3600) {
    snprintf(when, sizeof(when), "In %d min", int((e.start - now + 59) / 60));
  }
  add(when);
  add(e.cal.c_str());
  if (n) {
    lv_obj_t *s = label(text, sub, &small_font, going ? kHighlightText : kMuted);
    ellipsize(s);
  }

  if (e.join && !over) {
    lv_obj_t *join = button(r, 84, 40, kAccent, onJoin, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(join, 12, 0);
    lv_obj_center(label(join, LV_SYMBOL_CALL "  Join", &small_font, kOnAccent));
  }
}

// The events of a local day, in order: all-day ones first, then by start. Indexes into
// model::google.events (at most kMaxEvents, so a plain insertion sort).
int eventsOf(int64_t day, int *out) {
  const model::Google &g = model::google;
  const int64_t from = dayStart(day), to = dayStart(day + 1);
  int n = 0;
  for (int i = 0; i < g.event_count; i++) {
    const model::Event &e = g.events[i];
    if (!(e.start < to && e.end > from)) continue;  // not on that day
    int j = n++;
    while (j > 0) {
      const model::Event &p = g.events[out[j - 1]];
      const bool before = e.all_day != p.all_day ? e.all_day : e.start < p.start;
      if (!before) break;
      out[j] = out[j - 1];
      j--;
    }
    out[j] = i;
  }
  return n;
}

// Today's all-day events, as pills under the date: they stay in sight while the timed ones
// scroll to now.
void allDay(const int *order, int n) {
  lv_obj_clean(w.allday);
  int shown_count = 0;
  for (int k = 0; k < n; k++) {
    const model::Event &e = model::google.events[order[k]];
    if (!e.all_day) continue;
    if (shown_count++ == 0) label(w.allday, "ALL DAY", &small_font, kMuted);
    lv_obj_t *p = card(w.allday, kRaised, kRaised);
    lv_obj_set_size(p, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
    lv_obj_set_style_pad_hor(p, 10, 0);
    lv_obj_set_style_pad_ver(p, 3, 0);
    lv_obj_set_style_radius(p, 12, 0);
    char text[120];
    snprintf(text, sizeof(text), "%s%s%s", e.title.c_str(), e.cal.length() ? "  ·  " : "", e.cal.c_str());
    lv_obj_t *l = label(p, text, &small_font, kText);
    lv_obj_set_style_max_width(l, 260, 0);
    lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
  }
  setHidden(w.allday, shown_count == 0);
}

void fill(bool keep_scroll) {
  const model::Google &g = model::google;
  const int32_t scroll = lv_obj_get_scroll_y(w.body);
  lv_obj_clean(w.body);
  setHidden(w.allday, true);
  const int64_t now = model::utcNow();
  const int64_t today = now ? localDay(now) : 0;
  char date[24] = "";
  if (now) dayText(today, date, sizeof(date));
  setText(w.date, date);

  if (!pc::connected()) {
    note("PC offline: the calendar comes from the companion.");
    return;
  }
  if (g.status == "setup" || g.status == "signin") {
    note("Connect a calendar from the companion's tray menu: \"Calendars\".");
    return;
  }
  if (g.status != "ok" && g.event_count == 0) {
    note(g.status.length() ? "The calendar can't be reached right now." : "Checking the calendar…");
    return;
  }
  if (!now) {
    note("Waiting for the time from the PC…");
    return;
  }

  int order[model::Google::kMaxEvents];
  lv_obj_t *mark = nullptr;  // what to show first: the now line, or the event going on
  int n = eventsOf(today, order);
  allDay(order, n);
  int timed = 0;
  for (int k = 0; k < n; k++) timed += !g.events[order[k]].all_day;
  if (timed == 0) note(n ? "Nothing else on the calendar today." : "Nothing on the calendar today.");
  bool now_drawn = false;
  for (int k = 0; k < n; k++) {
    const model::Event &e = g.events[order[k]];
    if (e.all_day) continue;  // in the strip above
    // The now line goes before the first event that hasn't started (one going on is picked
    // out instead).
    if (!now_drawn && e.end > now) {
      if (e.start > now) mark = nowLine(now);
      now_drawn = true;
    }
    row(order[k], now, false);
    if (!mark && e.start <= now && now < e.end) mark = lv_obj_get_child(w.body, -1);
  }
  if (!now_drawn && timed > 0) mark = nowLine(now);  // all of today is over

  char tomorrow[24], text[40];
  dayText(today + 1, tomorrow, sizeof(tomorrow));
  snprintf(text, sizeof(text), "TOMORROW  ·  %s", tomorrow);
  heading(text, kMuted);
  n = eventsOf(today + 1, order);
  if (n == 0) note("Nothing yet.");
  for (int k = 0; k < n; k++) row(order[k], now, g.events[order[k]].all_day);

  lv_obj_update_layout(w.body);
  if (keep_scroll) {
    lv_obj_scroll_to_y(w.body, scroll, LV_ANIM_OFF);
  } else if (mark) {
    // The past stays above, a drag away; what's next is at the top, under a past event.
    const int32_t y = lv_obj_get_y(mark) - 62;
    lv_obj_scroll_to_y(w.body, y > 0 ? y : 0, LV_ANIM_OFF);
  }
}

}  // namespace

void build() {
  w.scrim = box(lv_layer_top());
  lv_obj_set_size(w.scrim, 800, 480);
  lv_obj_set_style_bg_color(w.scrim, hex(0x000000), 0);
  lv_obj_set_style_bg_opa(w.scrim, LV_OPA_50, 0);
  lv_obj_set_clickable(w.scrim, true);
  lv_obj_add_event_cb(w.scrim, onClose, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_hidden(w.scrim, true);

  lv_obj_t *p = w.panel = card(lv_layer_top(), kBg, kBorder);
  lv_obj_set_size(p, kPanelW, kPanelH);
  lv_obj_align(p, LV_ALIGN_CENTER, kRailW / 2, 0);  // in the middle of the pages, not the screen
  lv_obj_set_style_radius(p, 18, 0);
  lv_obj_set_style_pad_all(p, kPad, 0);
  lv_obj_set_style_shadow_width(p, 30, 0);
  lv_obj_set_style_shadow_color(p, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(p, LV_OPA_60, 0);
  lv_obj_set_clickable(p, true);  // a tap on it stays on it
  lv_obj_set_scrollable(p, false);
  flexCol(p, 8);

  lv_obj_t *head = box(p);
  lv_obj_set_size(head, LV_PCT(100), kHeadH);
  flexRow(head, 10);
  label(head, "TODAY", &lv_font_montserrat_16, kAccent);
  w.date = label(head, "", &title_font, kText);
  fillRow(w.date);
  lv_obj_t *close_button = button(head, 96, kHeadH, kRaised, onClose);
  lv_obj_set_style_radius(close_button, 12, 0);
  lv_obj_center(label(close_button, "Close", &body_font, kText));

  w.allday = box(p);
  lv_obj_set_size(w.allday, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(w.allday, 6);
  lv_obj_set_flex_flow(w.allday, LV_FLEX_FLOW_ROW_WRAP);  // several all-day events take a second line
  lv_obj_set_style_pad_row(w.allday, 6, 0);
  lv_obj_set_hidden(w.allday, true);

  w.body = box(p);
  lv_obj_set_width(w.body, LV_PCT(100));
  lv_obj_set_flex_grow(w.body, 1);
  lv_obj_set_clickable(w.body, true);  // so a drag anywhere on it scrolls it
  lv_obj_set_scrollable(w.body, true);
  lv_obj_set_scroll_dir(w.body, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(w.body, LV_SCROLLBAR_MODE_ACTIVE);
  flexCol(w.body, 6);
  lv_obj_set_hidden(p, true);
}

void close() {
  shown = false;
  lv_obj_set_hidden(w.scrim, true);
  lv_obj_set_hidden(w.panel, true);
}

void open() {
  shown = true;
  google_seen = model::google.version;
  connected_seen = pc::connected() ? 1 : 0;
  tick_ms = millis();
  lv_obj_set_hidden(w.scrim, false);
  lv_obj_set_hidden(w.panel, false);
  lv_obj_move_foreground(w.scrim);
  lv_obj_move_foreground(w.panel);
  fill(false);
}

void update() {
  if (!shown) return;
  const int connected = pc::connected() ? 1 : 0;
  if (model::google.version == google_seen && connected == connected_seen && millis() - tick_ms < kTickMs) return;
  google_seen = model::google.version;
  connected_seen = connected;
  tick_ms = millis();
  fill(true);
}

}  // namespace ui::day
