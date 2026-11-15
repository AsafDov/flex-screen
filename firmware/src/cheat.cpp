#include "cheat.h"

#include <Arduino.h>

#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::cheat {
namespace {

constexpr int kW = 788, kH = 480 - kBarH - 10;
constexpr int kPad = 14, kBorderW = 2;
constexpr int kHeadH = 30;
constexpr int kAreaW = kW - 2 * (kPad + kBorderW);
constexpr int kAreaH = kH - 2 * (kPad + kBorderW) - kHeadH - 8;
constexpr int kKeyGap = 5;

struct KeyView {
  lv_obj_t *root, *top, *main, *bottom;
};

struct {
  lv_obj_t *root, *title, *hint, *area, *legend;
  KeyView keys[model::Keymap::kMaxKeys];
  int count = 0;
} w;

uint32_t keymap_seen = UINT32_MAX;
int shown_layer = -2, shown_key = -2;
bool shown_pinned = false;
bool pinned = false;
bool dismissed = false;       // tapped away while held: stays away until that key comes up
uint32_t dismissed_hold = 0;  // that hold's start

void dismiss() {
  pinned = false;
  if (model::keyboard.held) {
    dismissed = true;
    dismissed_hold = model::keyboard.held_at_ms;
  }
}

void onTap(lv_event_t *) { dismiss(); }

lv_obj_t *line(lv_obj_t *parent, const lv_font_t *font, int width) {
  lv_obj_t *l = label(parent, "", font, kText);
  lv_obj_set_width(l, width);
  lv_obj_set_style_text_align(l, LV_TEXT_ALIGN_CENTER, 0);
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_CLIP);
  return l;
}

void setFont(lv_obj_t *l, const lv_font_t *font) {
  if (lv_obj_get_style_text_font(l, LV_PART_MAIN) != font) lv_obj_set_style_text_font(l, font, 0);
}

// The keys where the companion says they are, scaled to fill the area.
void rebuild() {
  lv_obj_clean(w.area);
  const model::Keymap &m = model::keymap;
  w.count = m.count;
  if (m.count == 0) return;
  // The base layer comes first.
  w.legend = label(w.area, "", &small_font, kMuted);
  lv_label_set_text_fmt(w.legend, "Dim keys work as on %s   ·   Blue: what holding does",
                        m.layer_count ? m.layers[0].name.c_str() : "the base layer");
  lv_obj_align(w.legend, LV_ALIGN_BOTTOM_MID, 0, 0);
  int right = 1, bottom = 1;
  for (int i = 0; i < m.count; i++) {
    right = max(right, m.keys[i].x + m.keys[i].w / 2);
    bottom = max(bottom, m.keys[i].y + m.keys[i].h / 2);
  }
  const float unit = min(kAreaW / float(right), kAreaH / float(bottom));  // pixels per hundredth of a key
  const int ox = int((kAreaW - right * unit) / 2), oy = int((kAreaH - bottom * unit) / 2);
  for (int i = 0; i < m.count; i++) {
    const model::Keymap::Key &k = m.keys[i];
    const int kw = int(k.w * unit) - kKeyGap, kh = int(k.h * unit) - kKeyGap;
    KeyView &v = w.keys[i];
    v.root = card(w.area, kRaised, kRaised);
    lv_obj_set_pos(v.root, ox + int((k.x - k.w / 2) * unit) + kKeyGap / 2, oy + int((k.y - k.h / 2) * unit) + kKeyGap / 2);
    lv_obj_set_size(v.root, kw, kh);
    lv_obj_set_style_pad_all(v.root, 0, 0);
    lv_obj_set_style_radius(v.root, 8, 0);
    flexCol(v.root, 0);
    lv_obj_set_flex_align(v.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    v.top = line(v.root, &lv_font_montserrat_12, kw - 2);
    v.main = line(v.root, &body_font, kw - 2);
    v.bottom = line(v.root, &lv_font_montserrat_12, kw - 2);
  }
}

// One layer's labels ("top\nmain\nbottom", or just "main", tab-separated) onto the keys.
void fill(const model::Keymap::Layer &layer, int held_key) {
  const char *p = layer.labels.c_str();
  for (int i = 0; i < w.count; i++) {
    const char *end = strchr(p, '\t');
    const size_t n = end ? size_t(end - p) : strlen(p);
    char token[64];
    const size_t len = min(n, sizeof(token) - 1);
    memcpy(token, p, len);
    token[len] = '\0';
    p = end ? end + 1 : p + n;

    const char *top = "", *main = token, *bottom = "";
    if (char *a = strchr(token, '\n')) {
      *a = '\0';
      top = token;
      main = a + 1;
      if (char *b = strchr(a + 1, '\n')) {
        *b = '\0';
        bottom = b + 1;
      }
    }
    KeyView &v = w.keys[i];
    // Dim: the layer leaves the key to the base layer. Empty: nothing there.
    const bool dim = i < int(layer.dim.length()) && layer.dim[i] == '1';
    const bool empty = !*top && !*main && !*bottom;
    const bool held = i == held_key;
    setBgColor(v.root, held ? kAccent : dim || empty ? kBg : kRaised);
    setBorderColor(v.root, held ? kAccent : dim || empty ? kBorder : kRaised);
    setText(v.top, top);
    setText(v.main, main);
    setText(v.bottom, bottom);
    setHidden(v.top, !*top);
    setHidden(v.bottom, !*bottom);
    // One character (or an arrow) big, so ` ' . read; a few smaller; words smallest, to fit.
    const size_t chars = strlen(main);
    const bool glyph = chars == 1 || (chars == 3 && uint8_t(main[0]) == 0xEF);
    setFont(v.main, glyph ? &title_font : chars <= 3 ? &body_font : &small_font);
    setTextColor(v.main, held ? kOnAccent : dim ? kMuted : kText);
    setTextColor(v.top, held ? kOnAccent : kMuted);
    setTextColor(v.bottom, held ? kOnAccent : dim ? kMuted : kAccent);
  }
}

}  // namespace

void build() {
  w.root = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_size(w.root, kW, kH);
  lv_obj_set_pos(w.root, (800 - kW) / 2, kBarH + 4);
  lv_obj_set_style_border_width(w.root, kBorderW, 0);
  lv_obj_set_style_radius(w.root, 18, 0);
  lv_obj_set_style_pad_all(w.root, kPad, 0);
  lv_obj_set_style_shadow_width(w.root, 30, 0);
  lv_obj_set_style_shadow_color(w.root, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.root, LV_OPA_60, 0);
  lv_obj_set_clickable(w.root, true);
  lv_obj_add_event_cb(w.root, onTap, LV_EVENT_CLICKED, nullptr);

  lv_obj_t *head = box(w.root);
  lv_obj_set_size(head, LV_PCT(100), kHeadH);
  flexRow(head, 10);
  lv_obj_set_flex_align(head, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  label(head, LV_SYMBOL_KEYBOARD, &body_font, kMuted);
  w.title = label(head, "", &title_font, kText);
  lv_obj_t *gap = box(head);
  lv_obj_set_size(gap, 1, 1);
  lv_obj_set_flex_grow(gap, 1);
  w.hint = label(head, "", &small_font, kMuted);

  w.area = box(w.root);
  lv_obj_set_pos(w.area, 0, kHeadH + 8);
  lv_obj_set_size(w.area, kAreaW, kAreaH);
  lv_obj_set_hidden(w.root, true);
}

void toggle() {
  if (pinned) dismiss();
  else pinned = true;
}

void update() {
  const model::Keymap &m = model::keymap;
  if (m.version != keymap_seen) {
    keymap_seen = m.version;
    rebuild();
    shown_layer = -2;
  }
  const model::Keyboard &k = model::keyboard;
  const bool ready = pc::connected() && k.present && w.count > 0;
  if (!ready) pinned = false;
  if (!k.held) dismissed = false;
  // Held this long from the layer key's press (settings.json's cheatSheetSeconds; 0: never).
  const uint32_t hold_ms = 1000UL * model::settings.cheat_hold_s;
  const bool held_long = ready && k.held && hold_ms && millis() - k.held_at_ms >= hold_ms && !(dismissed && dismissed_hold == k.held_at_ms);
  const model::Keymap::Layer *layer = ready && (pinned || held_long) ? m.find(k.layer) : nullptr;
  const int key = k.held ? k.key : -1;
  if (layer && (layer->index != shown_layer || key != shown_key || pinned != shown_pinned)) {
    shown_layer = layer->index;
    shown_key = key;
    shown_pinned = pinned;
    fill(*layer, key);
    setText(w.title, layer->name.c_str());
    setText(w.hint, pinned ? "Tap to close" : "Tap the layer pill to keep it open");
  }
  setHidden(w.root, layer == nullptr);
}

}  // namespace ui::cheat
