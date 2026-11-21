// The Claude tile (Dev): a banner with what the newest Claude session is doing, in its
// state's colors, and a long job's progress along its foot. Tap it for the session board;
// Open brings up that session's window. One of a kind (the manager places each kind once),
// so its state lives here.
#include "model.h"
#include "pc.h"
#include "sessions.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kTickMs = 1000;  // "n min ago"

// The banner's look per Claude state.
struct BannerStyle {
  uint32_t bg, border, icon_bg, icon_fg, title, detail;
  const char *icon;
  const char *text;
};

// Made when used: the palette is chosen at boot, after statics are initialized.
BannerStyle bannerStyle(const model::ClaudeSession *s) {
  if (!s) return {kSurface, kBorder, kRaised, kMuted, kMuted, kMuted, LV_SYMBOL_EDIT, "No Claude sessions"};
  if (s->state == "input") return {0x2A2112, 0x4A3A1C, kAmber, 0x2A2112, 0xF5C36B, 0xD8C9A8, LV_SYMBOL_WARNING, "Claude needs your approval"};
  if (s->state == "working") return {kHighlight, kHighlightLine, kAccent, kOnAccent, kText, kHighlightText, LV_SYMBOL_REFRESH, "Claude is working"};
  if (s->state == "done") return {0x10241D, 0x1F4A3A, kGreen, 0x0B1F18, kText, 0xA9C4B8, LV_SYMBOL_OK, "Claude finished"};
  return {kSurface, kBorder, kRaised, kMuted, kText, kMuted, LV_SYMBOL_EDIT, "Claude is ready"};
}

struct {
  lv_obj_t *banner, *icon_bg, *icon, *title, *detail, *more, *open;
  lv_obj_t *progress;  // a long job's, along the banner's foot
} w;

uint32_t claude_seen = UINT32_MAX;
uint32_t tick_ms = 0;

void onBoard(lv_event_t *) { sessions::open(); }

void onOpenClaude(lv_event_t *) {
  if (model::claude.count > 0) pc::openClaude(model::claude.sessions[0].id.c_str());
}

void buildBanner(lv_obj_t *c) {
  w.banner = c;
  lv_obj_set_style_pad_hor(c, 12, 0);
  lv_obj_set_style_pad_ver(c, 0, 0);
  flexRow(c, 12);
  // The banner opens the session board; its Open button brings up the top session's window.
  lv_obj_set_clickable(c, true);
  lv_obj_add_event_cb(c, onBoard, LV_EVENT_CLICKED, nullptr);
  w.icon_bg = box(c);
  lv_obj_set_size(w.icon_bg, 34, 34);
  lv_obj_set_style_radius(w.icon_bg, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_bg_opa(w.icon_bg, LV_OPA_COVER, 0);
  w.icon = label(w.icon_bg, "", &lv_font_montserrat_20, kMuted);
  lv_obj_center(w.icon);

  lv_obj_t *text = box(c);
  lv_obj_set_flex_grow(text, 1);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  flexCol(text, 2);
  w.title = label(text, "", &lv_font_montserrat_16, kText);
  w.detail = label(text, "", &small_font, kMuted);
  ellipsize(w.title);
  ellipsize(w.detail);
  w.more = label(c, "", &small_font, kMuted);

  w.open = textButton(c, LV_SIZE_CONTENT, 38, kAccent, "Open  " LV_SYMBOL_RIGHT, kOnAccent, onOpenClaude);
  lv_obj_set_style_pad_hor(w.open, 14, 0);

  w.progress = lv_bar_create(c);
  lv_obj_add_flag(w.progress, LV_OBJ_FLAG_FLOATING);  // out of the row, along the foot
  lv_obj_remove_flag(w.progress, LV_OBJ_FLAG_CLICKABLE);
  lv_obj_set_size(w.progress, LV_PCT(100), 4);
  lv_obj_align(w.progress, LV_ALIGN_BOTTOM_MID, 0, 2);
  lv_obj_set_style_radius(w.progress, 2, 0);
  lv_obj_set_style_radius(w.progress, 2, LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(w.progress, hex(kTrack), 0);
  lv_obj_set_style_bg_color(w.progress, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_hidden(w.progress, true);
}

const model::ClaudeSession *topSession() { return model::claude.count > 0 ? &model::claude.sessions[0] : nullptr; }

void updateClaude() {
  const model::ClaudeSession *s = pc::connected() ? topSession() : nullptr;
  const BannerStyle st = bannerStyle(s);
  setBgColor(w.banner, st.bg);
  setBorderColor(w.banner, st.border);
  setBgColor(w.icon_bg, st.icon_bg);
  setText(w.icon, st.icon);
  setTextColor(w.icon, st.icon_fg);
  setText(w.title, st.text);
  setTextColor(w.title, st.title);
  setTextColor(w.detail, st.detail);
  setHidden(w.open, s == nullptr);
  const bool progress = s && s->total > 0;
  setHidden(w.progress, !progress);
  if (progress) {
    lv_bar_set_range(w.progress, 0, s->total);
    lv_bar_set_value(w.progress, s->done, LV_ANIM_OFF);
  }

  if (!s) {
    setText(w.detail, pc::connected() ? "Sessions in VS Code and the Claude app show here" : "PC offline");
    setText(w.more, "");
    return;
  }
  char ago[24];
  formatAgo(ago, sizeof(ago), model::agedSecs(s->secs, model::claude.at_ms));
  String detail = s->project;
  if (s->total > 0) {
    // A long job says how far it's got rather than its latest step.
    if (s->progress.length()) detail += " · " + s->progress;
    detail += " · " + String(s->done) + " of " + String(s->total);
  } else if (s->detail.length()) {
    detail += " · " + s->detail;
  }
  if (ago[0]) detail += String(" · ") + ago;
  setText(w.detail, detail.c_str());
  if (model::claude.count > 1) setTextf(w.more, "+%d more", model::claude.count - 1);
  else setText(w.more, "");
}

class Claude : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildBanner(c); }
  void update() override {
    const bool tick = millis() - tick_ms >= kTickMs;
    if (tick) tick_ms = millis();
    if (!tick && model::claude.version == claude_seen) return;
    claude_seen = model::claude.version;
    updateClaude();
  }
};

}  // namespace

// The page's width, two rows: about the 52 px it was before tiles.
extern const Kind kClaude = {"claude", {14, 2}, {}, []() -> Tile * { return new Claude(); }};

}  // namespace ui::tiles
