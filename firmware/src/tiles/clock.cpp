// The clock tile: the PC's time, large, with seconds and the date.
#include <time.h>

#include "model.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

class Clock : public Tile {
 public:
  void build(lv_obj_t *card, Span) override {
    flexCol(card, 2);
    lv_obj_set_flex_align(card, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
    lv_obj_t *hm = box(card);
    lv_obj_set_size(hm, LV_PCT(100), LV_SIZE_CONTENT);
    flexRow(hm, 4);
    lv_obj_set_flex_align(hm, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_END);
    clock_ = label(hm, "--:--", &lv_font_montserrat_48, kText);
    secs_ = label(hm, "", &lv_font_montserrat_20, kMuted);
    lv_obj_set_style_pad_bottom(secs_, 6, 0);
    date_ = label(card, "Waiting for the PC", &lv_font_montserrat_16, kMuted);
  }

  void update() override {
    const int64_t now = model::localNow();
    if (now == 0 || now == seen_) return;
    seen_ = now;
    const time_t t = time_t(now);
    struct tm tm;
    gmtime_r(&t, &tm);  // already shifted to local time
    char buf[32];
    strftime(buf, sizeof(buf), "%H:%M", &tm);
    lv_label_set_text(clock_, buf);
    strftime(buf, sizeof(buf), ":%S", &tm);
    lv_label_set_text(secs_, buf);
    strftime(buf, sizeof(buf), "%A, %e %B", &tm);
    lv_label_set_text(date_, buf);
  }

 private:
  lv_obj_t *clock_ = nullptr, *secs_ = nullptr, *date_ = nullptr;
  int64_t seen_ = -1;
};

}  // namespace

// extern: tiles.cpp's list of kinds refers to it.
extern const Kind kClock = {"clock", {3, 1}, {}, []() -> Tile * { return new Clock(); }};

}  // namespace ui::tiles
