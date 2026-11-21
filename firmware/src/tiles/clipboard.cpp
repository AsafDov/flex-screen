// The Clipboard tile (Capture): your newest text copies and pins, a list that scrolls. Tap
// a clip to paste it into the window in front, hold it to pin it; Clear empties the rest.
// One of a kind (the manager places each kind once), so its state lives here.
#include <stdlib.h>
#include <string.h>

#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kTickMs = 30000;  // ages ("4m")

struct ClipRow {
  lv_obj_t *root, *icon, *swatch, *text, *age;
};

struct {
  ClipRow clips[model::Clips::kMax];
  lv_obj_t *note, *hint;
} w;

uint32_t clips_seen = UINT32_MAX;
int connected_seen = -1;
uint32_t tick_ms = 0;

int indexOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

void onClipTap(lv_event_t *e) {
  const int i = indexOf(e);
  if (i < model::clips.count) pc::pasteClip(model::clips.list[i].id.c_str());
}

void onClipHold(lv_event_t *e) {
  const int i = indexOf(e);
  if (i < model::clips.count) pc::pinClip(model::clips.list[i].id.c_str());
}

void onClear(lv_event_t *) { pc::clearClips(); }

void buildClipboard(lv_obj_t *c) {
  flexCol(c, 6);
  // The hint sits at the bottom, away from Clear: tapping "TAP TO PASTE" up there hit Clear.
  lv_obj_t *h = box(c);
  lv_obj_set_size(h, LV_PCT(100), 26);
  flexRow(h, 6);
  lv_obj_set_flex_align(h, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  label(h, "CLIPBOARD", &small_font, kMuted);
  lv_obj_t *clear = button(h, LV_SIZE_CONTENT, 26, kRaised, onClear);
  lv_obj_set_style_radius(clear, 8, 0);
  lv_obj_set_style_pad_hor(clear, 12, 0);
  lv_obj_center(label(clear, "Clear", &small_font, kMuted));
  w.note = label(c, "", &small_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);

  // The clips in a list of their own that scrolls: about four show at once above Obsidian.
  lv_obj_t *list = box(c);
  lv_obj_set_width(list, LV_PCT(100));
  lv_obj_set_flex_grow(list, 1);
  flexCol(list, 6);
  lv_obj_add_flag(list, LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_set_scroll_dir(list, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(list, LV_SCROLLBAR_MODE_AUTO);
  lv_obj_set_clickable(list, true);  // a drag between clips still scrolls
  for (int i = 0; i < model::Clips::kMax; i++) {
    ClipRow &r = w.clips[i];
    r.root = button(list, LV_PCT(100), 38, kRaised, nullptr);
    lv_obj_set_style_radius(r.root, 10, 0);
    lv_obj_set_style_pad_hor(r.root, 10, 0);
    lv_obj_add_event_cb(r.root, onClipTap, LV_EVENT_SHORT_CLICKED, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_add_event_cb(r.root, onClipHold, LV_EVENT_LONG_PRESSED, reinterpret_cast<void *>(intptr_t(i)));
    holdRing(r.root);
    flexRow(r.root, 8);
    r.icon = label(r.root, "", &small_font, kSoftText);
    r.swatch = box(r.root);
    lv_obj_set_size(r.swatch, 16, 16);
    lv_obj_set_style_radius(r.swatch, 4, 0);
    lv_obj_set_style_bg_opa(r.swatch, LV_OPA_COVER, 0);
    r.text = label(r.root, "", &small_font, kText);
    fillRow(r.text);
    r.age = label(r.root, "", &small_font, kMuted);
  }
  w.hint = label(c, "Tap: paste  ·  hold: pin", &small_font, kMuted);
  lv_obj_set_width(w.hint, LV_PCT(100));
  lv_obj_set_style_text_align(w.hint, LV_TEXT_ALIGN_CENTER, 0);
}

const char *clipIcon(const String &kind) {
  if (kind == "link") return LV_SYMBOL_UPLOAD;
  if (kind == "code") return LV_SYMBOL_FILE;
  if (kind == "command") return LV_SYMBOL_KEYBOARD;
  return LV_SYMBOL_LIST;
}

void updateClips() {
  const model::Clips &c = model::clips;
  const bool online = pc::connected();
  setText(w.note, !online ? "PC offline" : c.count == 0 ? "Copy some text and it shows up here." : "");
  setHidden(w.note, online && c.count > 0);
  setHidden(w.hint, !online || c.count == 0);
  for (int i = 0; i < model::Clips::kMax; i++) {
    ClipRow &r = w.clips[i];
    const bool used = online && i < c.count;
    setHidden(r.root, !used);
    if (!used) continue;
    const model::Clip &clip = c.list[i];
    const bool color = clip.kind == "color";
    setHidden(r.swatch, !color);
    setHidden(r.icon, color);
    if (color) {
      // "#5AB4FF" (or #RGB): show the color itself.
      const char *digits = clip.text.c_str() + 1;
      uint32_t v = strtoul(digits, nullptr, 16);
      if (strlen(digits) == 3) v = ((v >> 8 & 0xF) * 17) << 16 | ((v >> 4 & 0xF) * 17) << 8 | (v & 0xF) * 17;
      if (strlen(digits) == 8) v >>= 8;  // #RRGGBBAA
      setBgColor(r.swatch, v);
    } else {
      setText(r.icon, clip.pinned ? LV_SYMBOL_BULLET : clipIcon(clip.kind));
      setTextColor(r.icon, clip.pinned ? kAmber : kSoftText);
    }
    setText(r.text, clip.text.c_str());
    char age[16];
    const int32_t secs = model::agedSecs(clip.secs, c.at_ms);
    if (clip.pinned) snprintf(age, sizeof(age), "pinned");
    else if (secs < 60) snprintf(age, sizeof(age), "now");
    else if (secs < 3600) snprintf(age, sizeof(age), "%dm", int(secs / 60));
    else if (secs < 86400) snprintf(age, sizeof(age), "%dh", int(secs / 3600));
    else snprintf(age, sizeof(age), "%dd", int(secs / 86400));
    setText(r.age, age);
  }
}

class Clipboard : public Tile {
 public:
  void build(lv_obj_t *c, Span) override {
    buildClipboard(c);
    updateClips();
  }
  void update() override {
    const int connected = pc::connected() ? 1 : 0;
    const bool tick = millis() - tick_ms >= kTickMs;
    if (tick) tick_ms = millis();
    if (connected == connected_seen && !tick && model::clips.version == clips_seen) return;
    connected_seen = connected;
    clips_seen = model::clips.version;
    updateClips();
  }
};

}  // namespace

extern const Kind kClipboard = {"clipboard", {6, 7}, {}, []() -> Tile * { return new Clipboard(); }};

}  // namespace ui::tiles
