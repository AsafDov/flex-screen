// The Up next tile: the next meeting on the calendars, how soon, and its Join button. A tap
// on it (but Join) opens the whole day.
#include <time.h>

#include "dayview.h"
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kTickMs = 15000;  // "in 18 min"

// "14:05" in the PC's time zone.
void clockText(int64_t utc, char *out, size_t n) {
  const time_t t = time_t(utc + int64_t(model::clock.tz_offset_min) * 60);
  struct tm tm;
  gmtime_r(&t, &tm);
  strftime(out, n, "%H:%M", &tm);
}

int64_t localDay(int64_t utc) { return (utc + int64_t(model::clock.tz_offset_min) * 60) / 86400; }

class UpNext : public Tile {
 public:
  void build(lv_obj_t *card, Span) override {
    setBgColor(card, kHighlight);
    setBorderColor(card, kHighlightLine);
    flexRow(card, 10);
    lv_obj_set_clickable(card, true);
    lv_obj_add_event_cb(card, [](lv_event_t *) { day::open(); }, LV_EVENT_CLICKED, nullptr);
    lv_obj_t *text = box(card);
    lv_obj_set_flex_grow(text, 1);
    lv_obj_set_height(text, LV_SIZE_CONTENT);
    flexCol(text, 4);
    label(text, "UP NEXT", &lv_font_montserrat_16, kAccent);
    title_ = label(text, "", &title_font, kText);
    ellipsize(title_);
    time_ = label(text, "", &body_font, kHighlightText);
    ellipsize(time_);
    join_ = button(card, 88, 44, kAccent, onJoin);
    lv_obj_set_style_radius(join_, 12, 0);
    lv_obj_center(label(join_, LV_SYMBOL_CALL "  Join", &body_font, kOnAccent));
    lv_obj_set_hidden(join_, true);
    label(card, LV_SYMBOL_RIGHT, &lv_font_montserrat_16, kAccent);  // there's more: the day
  }

  void update() override {
    const int link = pc::connected() ? 1 : 0;
    if (model::google.version == seen_ && link == link_seen_ && millis() - tick_ms_ < kTickMs) return;
    seen_ = model::google.version;
    link_seen_ = link;
    tick_ms_ = millis();
    redraw();
  }

 private:
  static void onJoin(lv_event_t *) {
    if (const model::Event *e = model::google.next(model::utcNow())) pc::joinMeeting(e->id.c_str());
  }

  void redraw() {
    const model::Google &g = model::google;
    const bool online = pc::connected();
    const int64_t now = model::utcNow();
    const model::Event *next = online && g.status == "ok" && now ? g.next(now) : nullptr;
    setHidden(join_, next == nullptr || !next->join);

    if (!online) {
      setText(title_, "PC offline");
      setText(time_, "");
    } else if (g.status == "setup" || g.status == "signin") {
      setText(title_, "Connect a calendar");
      setText(time_, "Tray icon menu: Calendars");
    } else if (!next) {
      // No status yet: the companion hasn't reported (it said "Calendar unavailable" then).
      setText(title_, g.status == "ok"   ? "Nothing else today"
                      : g.status.length() ? "Calendar unavailable"
                                          : "Checking the calendar…");
      setText(time_, "");
    } else {
      setText(title_, next->title.c_str());
      char from[8], to[8];
      clockText(next->start, from, sizeof(from));
      clockText(next->end, to, sizeof(to));
      const int64_t mins = (next->start - now + 59) / 60;
      const char *day = localDay(next->start) > localDay(now) ? "Tomorrow " : "";
      if (next->start <= now) setTextf(time_, "Now · until %s", to);
      else if (mins < 60) setTextf(time_, "%s-%s · in %d min", from, to, int(mins));
      else setTextf(time_, "%s%s-%s", day, from, to);
    }
  }

  lv_obj_t *title_ = nullptr, *time_ = nullptr, *join_ = nullptr;
  uint32_t seen_ = UINT32_MAX, tick_ms_ = 0;
  int link_seen_ = -1;
};

}  // namespace

// extern: tiles.cpp's list of kinds refers to it.
extern const Kind kUpNext = {"up-next", {4, 1}, {}, []() -> Tile * { return new UpNext(); }};

}  // namespace ui::tiles
