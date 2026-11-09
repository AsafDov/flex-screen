#include "ctxbar.h"

#include <Arduino.h>

#include "fonts/font_icons.h"
#include "icons.h"
#include "model.h"
#include "pc.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::ctxbar {
namespace {

constexpr int kNumpadPage = 5;
constexpr int kH = 46;
constexpr int kSlot = kH + 8;  // the pages' bottom padding: the bar and the gap above it
constexpr int kX = 10, kW = 800 - 2 * kX;
constexpr int kY = 480 - kBarH - 6 - kH;  // 6 px above the tab bar
constexpr int kPadL = 10, kPadR = 5;
constexpr int kChipW = 112;
constexpr int kKeyGap = 6;
constexpr int kKeyH = 36;
constexpr int kKeysW = kW - 2 - kPadL - kPadR - kChipW - kKeyGap - 1 - 2 - kKeyGap;  // border, chip, divider
constexpr int kKeyPad = 6, kIconGap = 6;  // a key's side padding; between its icon and name
constexpr int kKeyPadTight = 3, kIconGapTight = 4;  // when the keys don't all fit
constexpr uint32_t kIconRetryMs = 3000;

struct KeyView {
  lv_obj_t *root, *icon, *name;
};

struct {
  lv_obj_t *pages;
  lv_obj_t *bar, *image, *letter, *app, *divider;
  KeyView keys[model::Context::kMax];
} w;

bool shown = true;  // as built; the first update() settles it
uint32_t context_seen = UINT32_MAX, icons_seen = UINT32_MAX, asked_ms = 0;

void onKey(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::context.count) pc::runContextKey(model::context.keys[i].id.c_str());
}

// `part` 0–255 of `color` over `base`.
lv_color_t mix(uint32_t color, uint32_t base, uint8_t part) { return lv_color_mix(hex(color), hex(base), part); }

int textWidth(const char *text, const lv_font_t *font) {
  lv_point_t size;
  lv_text_get_size(&size, text, font, 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
  return size.x;
}

const char *glyph(const String &name) {
  for (int i = 0; i < font_icon_count; i++) {
    if (name == font_icon_names[i].name) return font_icon_names[i].text;
  }
  return "";
}

void fill() {
  const model::Context &c = model::context;
  const uint32_t color = c.color ? c.color : kAccent;

  // The app's color, strongest at the left where its name is, fading into the bar.
  lv_obj_set_style_bg_color(w.bar, mix(color, kSurface, 107), 0);  // 42 %
  lv_obj_set_style_bg_grad_color(w.bar, mix(color, kSurface, 26), 0);  // 10 %
  lv_obj_set_style_border_color(w.bar, mix(color, kSurface, 140), 0);
  lv_obj_set_style_bg_color(w.divider, mix(color, kSurface, 115), 0);

  setText(w.app, c.app.c_str());
  const lv_image_dsc_t *icon = icons::get(c.icon);
  if (!icon && c.icon.length() && millis() - asked_ms >= kIconRetryMs) {
    asked_ms = millis();
    pc::requestIcon(c.icon.c_str());
  }
  setHidden(w.image, icon == nullptr);
  setHidden(w.letter, icon != nullptr);
  if (icon && lv_image_get_src(w.image) != icon) lv_image_set_src(w.image, icon);
  if (!icon) {
    char letter[2] = {c.app.length() ? char(toupper(c.app[0])) : '?', 0};
    lv_label_set_text(lv_obj_get_child(w.letter, 0), letter);
    lv_obj_set_style_bg_color(w.letter, mix(color, kSurface, 153), 0);
  }

  // Each key as wide as its icon and name, plus an even share of what's left over, so a
  // short "Find" makes room for "Commands". If they can't all fit, the keys' padding
  // tightens first; then the widest give way: every key keeps its width up to a cap, the
  // cap set so they just fill the bar, and a name cut short ends in "…" (sharing evenly cut
  // "Mute" to "M…" beside a long name).
  const int n = c.count;
  const int room = kKeysW - (n - 1) * kKeyGap;
  int icon_w[model::Context::kMax], text_w[model::Context::kMax], natural[model::Context::kMax];
  int total = 0, widest = 0, pad = kKeyPad, gap = kIconGap;
  for (int pass = 0; pass < 2; pass++) {
    total = widest = 0;
    for (int i = 0; i < n; i++) {
      const char *g = glyph(c.keys[i].glyph);
      icon_w[i] = *g ? textWidth(g, &font_icons_15) + gap : 0;
      text_w[i] = textWidth(c.keys[i].name.c_str(), &small_font);
      natural[i] = 2 * pad + icon_w[i] + text_w[i];
      total += natural[i];
      widest = max(widest, natural[i]);
    }
    if (total <= room) break;
    pad = kKeyPadTight;
    gap = kIconGapTight;
  }
  const bool fits = total <= room;
  const int extra = n && fits ? (room - total) / n : 0;
  int cap = widest;
  if (!fits) {
    int lo = 0, hi = widest;
    while (lo < hi) {  // the largest cap that fits
      const int mid = (lo + hi + 1) / 2;
      int sum = 0;
      for (int i = 0; i < n; i++) sum += min(natural[i], mid);
      if (sum <= room) lo = mid;
      else hi = mid - 1;
    }
    cap = lo;
  }
  for (int i = 0; i < model::Context::kMax; i++) {
    KeyView &k = w.keys[i];
    setHidden(k.root, i >= n);
    if (i >= n) continue;
    const model::ContextKey &x = c.keys[i];
    const int key_w = fits ? natural[i] + extra : min(natural[i], cap);
    lv_obj_set_width(k.root, key_w);
    if (lv_obj_get_style_pad_left(k.root, LV_PART_MAIN) != pad) {
      lv_obj_set_style_pad_hor(k.root, pad, 0);
      lv_obj_set_style_pad_column(k.root, gap, 0);
    }
    lv_obj_set_style_bg_color(k.root, mix(color, kSurface, 61), 0);  // 24 %
    lv_obj_set_style_bg_color(k.root, mix(color, kSurface, 115), LV_STATE_PRESSED);
    const char *g = glyph(x.glyph);
    setText(k.icon, g);
    setHidden(k.icon, !*g);
    lv_obj_set_style_text_color(k.icon, lv_color_mix(hex(0xFFFFFF), hex(color), 97), 0);  // 38 % lighter
    setTextUpTo(k.name, x.name.c_str(), key_w >= natural[i] ? text_w[i] + 2 : key_w - 2 * pad - icon_w[i]);
  }
}

}  // namespace

void build(lv_obj_t *screen, lv_obj_t *pages) {
  w.pages = pages;
  w.bar = card(screen, kSurface, kBorder);
  lv_obj_add_flag(w.bar, LV_OBJ_FLAG_IGNORE_LAYOUT);  // the screen's column holds the status bar and the pages
  lv_obj_set_pos(w.bar, kX, kY);
  lv_obj_set_size(w.bar, kW, kH);
  lv_obj_set_style_radius(w.bar, 12, 0);
  lv_obj_set_style_pad_ver(w.bar, 0, 0);
  lv_obj_set_style_pad_left(w.bar, kPadL, 0);
  lv_obj_set_style_pad_right(w.bar, kPadR, 0);
  lv_obj_set_style_bg_grad_dir(w.bar, LV_GRAD_DIR_HOR, 0);
  lv_obj_set_style_bg_main_stop(w.bar, 0, 0);
  lv_obj_set_style_bg_grad_stop(w.bar, 100, 0);  // the fade is done 40 % of the way across
  flexRow(w.bar, kKeyGap);

  lv_obj_t *chip = box(w.bar);
  lv_obj_set_size(chip, kChipW, LV_PCT(100));
  flexRow(chip, 8);
  lv_obj_t *holder = box(chip);
  lv_obj_set_size(holder, icons::kSize, icons::kSize);
  w.image = lv_image_create(holder);
  lv_obj_set_size(w.image, icons::kSize, icons::kSize);
  w.letter = card(holder, kRaised, kRaised);  // no icon (yet): the app's first letter
  lv_obj_set_size(w.letter, icons::kSize, icons::kSize);
  lv_obj_set_style_pad_all(w.letter, 0, 0);
  lv_obj_set_style_radius(w.letter, 8, 0);
  lv_obj_center(label(w.letter, "", &body_font, kText));
  w.app = label(chip, "", &body_font, kText);
  lv_obj_set_style_max_width(w.app, kChipW - icons::kSize - 8, 0);
  lv_label_set_long_mode(w.app, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_height(w.app, lv_font_get_line_height(&body_font));

  w.divider = box(w.bar);
  lv_obj_set_size(w.divider, 1, 28);
  lv_obj_set_style_bg_opa(w.divider, LV_OPA_COVER, 0);
  lv_obj_set_style_margin_right(w.divider, 2, 0);

  for (int i = 0; i < model::Context::kMax; i++) {
    KeyView &k = w.keys[i];
    k.root = button(w.bar, 80, kKeyH, kRaised, onKey, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(k.root, 10, 0);
    lv_obj_set_style_pad_hor(k.root, kKeyPad, 0);
    flexRow(k.root, kIconGap);
    lv_obj_set_flex_align(k.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    k.icon = label(k.root, "", &font_icons_15, kAccent);
    k.name = label(k.root, "", &small_font, kText);
  }
  lv_obj_set_hidden(w.bar, true);
}

void update() {
  const bool want = pc::connected() && model::context.count > 0 && currentPage() != kNumpadPage;
  if (want != shown) {
    shown = want;
    lv_obj_set_hidden(w.bar, !want);
    lv_obj_set_style_pad_bottom(w.pages, want ? kSlot : 0, 0);  // the pages make room, or take it back
    if (want) context_seen = UINT32_MAX;
  }
  if (!shown) return;
  const bool icon_due = model::context.icon.length() && !icons::get(model::context.icon) && millis() - asked_ms >= kIconRetryMs;
  if (model::context.version == context_seen && icons::version() == icons_seen && !icon_due) return;
  context_seen = model::context.version;
  icons_seen = icons::version();
  fill();
}

}  // namespace ui::ctxbar
