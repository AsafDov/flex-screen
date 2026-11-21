// The Build tile (Dev): the last build's result, when and how long, and the last 10 runs as
// a row of boxes, newest on the right. (Building again is the terminal's job: the build
// command changes all the time.) One of a kind (the manager places each kind once), so its
// state lives here.
#include "model.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr int kHistory = 10;
constexpr uint32_t kTickMs = 1000;  // "n min ago" and the running timer
constexpr uint32_t kFailText = 0xFF9A70;
constexpr uint32_t kFailBox = 0xFF7A45;

struct {
  lv_obj_t *icon_bg, *icon, *state, *when, *cmd;
  lv_obj_t *history[kHistory];
} w;

uint32_t build_seen = UINT32_MAX;
uint32_t tick_ms = 0;

void buildBuild(lv_obj_t *c) {
  lv_obj_set_style_pad_all(c, 10, 0);
  flexRow(c, 10);
  w.icon_bg = box(c);
  lv_obj_set_size(w.icon_bg, 36, 36);
  lv_obj_set_style_radius(w.icon_bg, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_bg_opa(w.icon_bg, LV_OPA_COVER, 0);
  w.icon = label(w.icon_bg, "", &lv_font_montserrat_16, kMuted);
  lv_obj_center(w.icon);

  lv_obj_t *text = box(c);
  lv_obj_set_flex_grow(text, 1);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  flexCol(text, 2);
  w.state = label(text, "", &body_font, kText);
  w.when = label(text, "", &small_font, kMuted);
  ellipsize(w.when);
  w.cmd = label(text, "", &small_font, kMuted);  // only in the companion's toast now
  lv_obj_set_hidden(w.cmd, true);
  lv_obj_t *hist = box(text);
  lv_obj_set_size(hist, LV_PCT(100), 12);
  flexRow(hist, 3);
  for (lv_obj_t *&box_ : w.history) {
    box_ = card(hist, kRaised, kRaised);
    lv_obj_set_size(box_, 11, 12);
    lv_obj_set_style_pad_all(box_, 0, 0);
    lv_obj_set_style_radius(box_, 3, 0);
    lv_obj_center(label(box_, "", &small_font, 0x2A1206));
  }
}

void updateBuild() {
  const model::Build &b = model::build;
  const char *icon = LV_SYMBOL_MINUS;
  const char *state = "No builds yet";
  uint32_t bg = kRaised, fg = kMuted, text = kMuted;
  if (b.state == "running") {
    icon = LV_SYMBOL_REFRESH, state = "Building", bg = kHighlight, fg = kAccent, text = kAccent;
  } else if (b.state == "passed") {
    icon = LV_SYMBOL_OK, state = "Passed", bg = 0x143A2E, fg = kGreen, text = kGreen;
  } else if (b.state == "failed") {
    icon = LV_SYMBOL_CLOSE, state = "Failed", bg = 0x3A1D12, fg = kFailText, text = kFailText;
  }
  setBgColor(w.icon_bg, bg);
  setText(w.icon, icon);
  setTextColor(w.icon, fg);
  setText(w.state, state);
  setTextColor(w.state, text);

  char ago[24], took[24];
  const int32_t secs = model::agedSecs(b.secs, b.at_ms);
  if (b.state == "running") {
    formatTime(took, sizeof(took), float(secs < 0 ? 0 : secs));
    setTextf(w.when, "running · %s", took);
  } else if (b.state != "none") {
    formatAgo(ago, sizeof(ago), secs);
    formatDuration(took, sizeof(took), b.ms);
    setTextf(w.when, "%s · %s", ago, took);
  } else {
    setText(w.when, "Builds in VS Code show here");
  }
  setText(w.cmd, b.cmd.length() ? b.cmd.c_str() : "—");

  // Newest on the right, empty slots on the left.
  const int n = b.history.length();
  for (int i = 0; i < kHistory; i++) {
    const int h = i - (kHistory - n);
    const char r = h >= 0 ? b.history[h] : ' ';
    setBgColor(w.history[i], r == 'P' ? kGreen : r == 'F' ? kFailBox : kRaised);
    setText(lv_obj_get_child(w.history[i], 0), r == 'F' ? LV_SYMBOL_CLOSE : "");
  }
}

class Build : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildBuild(c); }
  void update() override {
    const bool tick = millis() - tick_ms >= kTickMs;
    if (tick) tick_ms = millis();
    if (!tick && model::build.version == build_seen) return;
    build_seen = model::build.version;
    updateBuild();
  }
};

}  // namespace

extern const Kind kBuild = {"build", {5, 3}, {}, []() -> Tile * { return new Build(); }};

}  // namespace ui::tiles
