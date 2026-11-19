#include "ctxbar.h"

#include <Arduino.h>

#include "fonts/font_icons.h"
#include "icons.h"
#include "idle.h"
#include "model.h"
#include "pc.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::ctxbar {
namespace {

constexpr int kH = kCtxH;
constexpr int kSlot = kH + 8;  // the room at the pages' bottom: the bar and the gap above it
constexpr int kX = 10, kW = 800 - 2 * kX;  // the whole width, under the rail too
constexpr int kY = kCtxY;                  // 6 px above the screen's edge
constexpr int kPadL = 10, kPadR = 5;
constexpr int kChipW = 112;
constexpr int kChipPad = 4;  // the app's name and icon, a key to its page: its pressed color's edge
constexpr int kKeyGap = 6;
constexpr int kKeyH = 36;
constexpr int kKeysW = kW - 2 - kPadL - kPadR - kChipW - kKeyGap - 1 - 2 - kKeyGap;  // border, chip, divider
constexpr int kKeyPad = 6, kIconGap = 6;  // a key's side padding; between its icon and name
constexpr int kKeyPadTight = 3, kIconGapTight = 4;  // when the keys don't all fit
constexpr int kPlusW = 34;  // the key that adds a key
constexpr uint32_t kIconRetryMs = 3000;

// The long-press menu: the app's actions to choose from, above the bar.
constexpr int kMenuX = 10, kMenuW = 800 - 2 * kMenuX;
constexpr int kMenuBottom = kY - 6;
constexpr int kMenuMaxH = kMenuBottom - kStatusH - 6;  // up to just below the status line
constexpr int kMenuPad = 12, kMenuBorder = 2;
constexpr int kMenuHeadH = 30;
constexpr int kMenuCols = 5;
constexpr int kItemGap = 6;
constexpr int kItemW = (kMenuW - 2 * (kMenuPad + kMenuBorder) - (kMenuCols - 1) * kItemGap) / kMenuCols;
constexpr int kItemH = 38;

struct KeyView {
  lv_obj_t *root, *icon, *name;
};

struct {
  lv_obj_t *pages;
  lv_obj_t *bar, *chip, *image, *letter, *app, *divider;
  KeyView keys[model::Context::kMax];
  lv_obj_t *plus, *plus_icon;
} w;

struct {
  lv_obj_t *scrim, *panel, *title, *remove, *reset, *list, *own_label, *every_label;
  KeyView items[model::Context::kBankMax];
} m;

bool shown = false;  // as built: hidden
bool room = false;   // the pages but the Numpad keep the bar's room at their bottom
constexpr int kPages = kNumpadPage + 1;
int32_t page_pad[kPages] = {};  // each page's own bottom padding, as it built itself
uint32_t context_seen = UINT32_MAX, icons_seen = UINT32_MAX, asked_ms = 0;
int menu_slot = -1;  // the key the menu changes (the key count: a new one); -1 while it's closed
String menu_key;     // the app whose bar it changes
uint32_t menu_seen = UINT32_MAX;
// The same menu as a list a key opened (Claude's model or effort): its choices instead.
bool picking = false;
uint32_t picker_seen = 0;

// `part` 0–255 of `color` over `base`.
lv_color_t mix(uint32_t color, uint32_t base, uint8_t part) { return lv_color_mix(hex(color), hex(base), part); }

uint32_t barColor() { return model::context.color ? model::context.color : kAccent; }

int textWidth(const char *text, const lv_font_t *font) {
  lv_point_t size;
  lv_text_get_size(&size, text, font, 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
  return size.x;
}

const char *glyph(const String &name) { return glyphOf(name.c_str()); }

int indexOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

// ---- the menu

void closeMenu() {
  menu_slot = -1;
  picking = false;
  lv_obj_set_hidden(m.scrim, true);
  lv_obj_set_hidden(m.panel, true);
}

// The menu's height for `rows` rows of items under `labels` section labels.
void sizeMenu(int rows, int labels) {
  const int line = lv_font_get_line_height(&small_font);
  const int list_h = labels * (line + kItemGap) + rows * (kItemH + kItemGap);
  const int h = min(kMenuMaxH, 2 * (kMenuPad + kMenuBorder) + kMenuHeadH + 8 + list_h);
  lv_obj_set_height(m.panel, h);
  lv_obj_set_y(m.panel, kMenuBottom - h);
  lv_obj_scroll_to_y(m.list, 0, LV_ANIM_OFF);
}

void fillMenu() {
  const model::Context &c = model::context;
  const uint32_t color = barColor();
  const bool adding = menu_slot >= c.count;
  const String current = adding ? String() : c.keys[menu_slot].id;
  if (adding) setText(m.title, "Add a key");
  else setTextf(m.title, "Instead of %s", c.keys[menu_slot].name.c_str());
  setHidden(m.remove, adding);
  setHidden(m.reset, false);

  String app = c.app;
  app.toUpperCase();
  setText(m.own_label, app.c_str());
  setHidden(m.own_label, c.own == 0);
  setHidden(m.every_label, c.own == c.bank_count);
  lv_obj_move_to_index(m.every_label, c.own + 1);  // after the app's own actions

  for (int i = 0; i < model::Context::kBankMax; i++) {
    KeyView &v = m.items[i];
    setHidden(v.root, i >= c.bank_count);
    if (i >= c.bank_count) continue;
    const model::ContextKey &x = c.bank[i];
    bool on_bar = false;
    for (int k = 0; k < c.count; k++) on_bar = on_bar || c.keys[k].id == x.id;
    const bool here = x.id == current;
    // This key's action filled in the app's color; the others already on the bar outlined.
    lv_obj_set_style_bg_color(v.root, here ? mix(color, kSurface, 115) : hex(kRaised), 0);
    lv_obj_set_style_border_width(v.root, on_bar ? 2 : 0, 0);
    lv_obj_set_style_border_color(v.root, mix(color, kSurface, 170), 0);
    const char *g = glyph(x.glyph);
    setText(v.icon, g);
    setHidden(v.icon, !*g);
    lv_obj_set_style_text_color(v.icon, lv_color_mix(hex(0xFFFFFF), hex(color), 97), 0);
    setTextUpTo(v.name, x.name.c_str(), kItemW - 16 - (*g ? textWidth(g, &font_icons_15) + 6 : 0));
  }

  // As tall as its rows, up to just below the status bar; past that it scrolls.
  const int every = c.bank_count - c.own;
  sizeMenu((c.own + kMenuCols - 1) / kMenuCols + (every + kMenuCols - 1) / kMenuCols, (c.own > 0) + (every > 0));
  menu_seen = c.version;
}

// A key's list: its choices, the one in effect filled in.
void fillPicker() {
  const model::Picker &p = model::picker;
  const uint32_t color = barColor();
  setText(m.title, p.title.c_str());
  setHidden(m.remove, true);
  setHidden(m.reset, true);
  setHidden(m.own_label, true);
  setHidden(m.every_label, true);
  for (int i = 0; i < model::Context::kBankMax; i++) {
    KeyView &v = m.items[i];
    setHidden(v.root, i >= p.count);
    if (i >= p.count) continue;
    const bool here = p.choices[i].id == p.current;
    lv_obj_set_style_bg_color(v.root, here ? mix(color, kSurface, 115) : hex(kRaised), 0);
    lv_obj_set_style_border_width(v.root, 0, 0);
    setHidden(v.icon, true);
    setTextUpTo(v.name, p.choices[i].name.c_str(), kItemW - 16);
  }
  sizeMenu((p.count + kMenuCols - 1) / kMenuCols, 0);
}

void openPicker() {
  if (model::picker.count == 0) return;
  closeMenu();
  picking = true;
  fillPicker();
  lv_obj_set_hidden(m.scrim, false);
  lv_obj_set_hidden(m.panel, false);
}

void openMenu(int slot) {
  const model::Context &c = model::context;
  if (!c.key.length() || c.bank_count == 0) return;
  menu_slot = min(slot, c.count);
  menu_key = c.key;
  fillMenu();
  lv_obj_set_hidden(m.scrim, false);
  lv_obj_set_hidden(m.panel, false);
}

void onItem(lv_event_t *e) {
  const int i = indexOf(e);
  if (picking) {
    const model::Picker &p = model::picker;
    if (i < p.count) pc::contextPick(p.id.c_str(), p.choices[i].id.c_str());
    closeMenu();
    return;
  }
  const model::Context &c = model::context;
  if (menu_slot >= 0 && i < c.bank_count && c.key == menu_key) pc::contextSet(menu_key.c_str(), menu_slot, c.bank[i].id.c_str());
  closeMenu();
}

void onRemove(lv_event_t *) {
  if (menu_slot >= 0 && menu_slot < model::context.count) pc::contextRemove(menu_key.c_str(), menu_slot);
  closeMenu();
}

void onReset(lv_event_t *) {
  if (menu_slot >= 0) pc::contextReset(menu_key.c_str());
  closeMenu();
}

void onScrim(lv_event_t *) { closeMenu(); }

lv_obj_t *headButton(lv_obj_t *parent, const char *text, lv_event_cb_t cb) {
  lv_obj_t *b = button(parent, LV_SIZE_CONTENT, kMenuHeadH, kRaised, cb);
  lv_obj_set_style_radius(b, 10, 0);
  lv_obj_set_style_pad_hor(b, 14, 0);
  lv_obj_center(label(b, text, &small_font, kText));
  return b;
}

lv_obj_t *sectionLabel(lv_obj_t *parent, const char *text) {
  lv_obj_t *l = label(parent, text, &small_font, kMuted);
  lv_obj_set_width(l, LV_PCT(100));  // a row of its own
  return l;
}

void buildMenu() {
  // Taps beside the menu close it rather than reaching the page underneath.
  m.scrim = box(lv_layer_top());
  lv_obj_set_size(m.scrim, 800, 480);
  lv_obj_set_clickable(m.scrim, true);
  lv_obj_add_event_cb(m.scrim, onScrim, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_hidden(m.scrim, true);

  m.panel = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_pos(m.panel, kMenuX, kMenuBottom - kMenuMaxH);
  lv_obj_set_size(m.panel, kMenuW, kMenuMaxH);
  lv_obj_set_style_border_width(m.panel, kMenuBorder, 0);
  lv_obj_set_style_radius(m.panel, 16, 0);
  lv_obj_set_style_pad_all(m.panel, kMenuPad, 0);
  lv_obj_set_style_shadow_width(m.panel, 30, 0);
  lv_obj_set_style_shadow_color(m.panel, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(m.panel, LV_OPA_60, 0);
  lv_obj_set_clickable(m.panel, true);  // a tap between items stays on the menu
  flexCol(m.panel, 8);

  lv_obj_t *head = box(m.panel);
  lv_obj_set_size(head, LV_PCT(100), kMenuHeadH);
  flexRow(head, 8);
  m.title = label(head, "", &body_font, kText);
  lv_obj_t *gap = box(head);
  lv_obj_set_size(gap, 1, 1);
  lv_obj_set_flex_grow(gap, 1);
  m.reset = headButton(head, "Reset", onReset);
  m.remove = headButton(head, "Remove", onRemove);

  m.list = box(m.panel);
  lv_obj_set_width(m.list, LV_PCT(100));
  lv_obj_set_flex_grow(m.list, 1);
  lv_obj_set_flex_flow(m.list, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_style_pad_row(m.list, kItemGap, 0);
  lv_obj_set_style_pad_column(m.list, kItemGap, 0);
  lv_obj_set_scrollable(m.list, true);
  lv_obj_set_clickable(m.list, true);  // a drag starting between items scrolls it too
  lv_obj_set_scroll_dir(m.list, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(m.list, LV_SCROLLBAR_MODE_ACTIVE);
  m.own_label = sectionLabel(m.list, "");
  for (int i = 0; i < model::Context::kBankMax; i++) {
    KeyView &v = m.items[i];
    v.root = button(m.list, kItemW, kItemH, kRaised, nullptr);
    lv_obj_add_event_cb(v.root, onItem, LV_EVENT_CLICKED, reinterpret_cast<void *>(intptr_t(i)));  // not after a scroll
    lv_obj_set_style_radius(v.root, 10, 0);
    lv_obj_set_style_pad_hor(v.root, 8, 0);
    flexRow(v.root, 6);
    v.icon = label(v.root, "", &font_icons_15, kAccent);
    v.name = label(v.root, "", &small_font, kText);
  }
  m.every_label = sectionLabel(m.list, "EVERYWHERE");
  lv_obj_set_hidden(m.panel, true);
}

// ---- the bar

void onKey(lv_event_t *e) {
  const int i = indexOf(e);
  if (i < model::context.count) pc::runContextKey(model::context.keys[i].id.c_str());
}

void onKeyHold(lv_event_t *e) { openMenu(indexOf(e)); }

// The deck's page for the app in front: the one with the rest of its controls.
int pageFor(const String &key) {
  const int colon = key.indexOf(':');
  const String exe = colon < 0 ? key : key.substring(0, colon);
  const String site = colon < 0 ? String() : key.substring(colon + 1);
  if (site == "claude") return kDevPage;  // Claude Code's chat in VS Code
  if (site == "youtube") return kAudioPage;
  if (site == "gmail" || site == "calendar" || site == "call") return kTodayPage;
  static const char *const dev[] = {"code", "claude", "windowsterminal", "devenv", "cursor", "powershell", "cmd", "pwsh"};
  static const char *const audio[] = {"spotify", "discord", "vlc", "foobar2000", "applemusic"};
  static const char *const today[] = {"ms-teams", "teams", "outlook", "olk", "zoom"};
  static const char *const capture[] = {"obsidian", "notepad", "onenote", "snippingtool"};
  for (const char *x : dev) if (exe == x) return kDevPage;
  for (const char *x : audio) if (exe == x) return kAudioPage;
  for (const char *x : today) if (exe == x) return kTodayPage;
  for (const char *x : capture) if (exe == x) return kCapturePage;
  return kWindowsPage;  // Explorer, browsers and the rest: the window switcher
}

void onChip(lv_event_t *) {
  if (model::context.key.length()) showPage(pageFor(model::context.key));
}
void onPlus(lv_event_t *) { openMenu(model::context.count); }

void fill() {
  const model::Context &c = model::context;
  const uint32_t color = barColor();

  // The app's color, strongest at the left where its name is, fading into the bar.
  lv_obj_set_style_bg_color(w.bar, mix(color, kSurface, 107), 0);  // 42 %
  lv_obj_set_style_bg_grad_color(w.bar, mix(color, kSurface, 26), 0);  // 10 %
  lv_obj_set_style_border_color(w.bar, mix(color, kSurface, 140), 0);
  lv_obj_set_style_bg_color(w.divider, mix(color, kSurface, 115), 0);
  lv_obj_set_style_bg_color(w.chip, mix(color, kSurface, 115), LV_STATE_PRESSED);

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

  // "+" adds a key while there's room for one.
  const int n = c.count;
  const bool plus = n < model::Context::kMax && c.bank_count > 0;
  setHidden(w.plus, !plus);
  lv_obj_set_style_bg_color(w.plus, mix(color, kSurface, 38), 0);
  lv_obj_set_style_bg_color(w.plus, mix(color, kSurface, 115), LV_STATE_PRESSED);
  lv_obj_set_style_text_color(w.plus_icon, lv_color_mix(hex(0xFFFFFF), hex(color), 97), 0);

  // Each key as wide as its icon and name, plus an even share of what's left over, so a
  // short "Find" makes room for "Commands". If they can't all fit, the keys' padding
  // tightens first; then the widest give way: every key keeps its width up to a cap, the
  // cap set so they just fill the bar, and a name cut short ends in "…" (sharing evenly cut
  // "Mute" to "M…" beside a long name).
  const int room = kKeysW - max(n - 1, 0) * kKeyGap - (plus ? kPlusW + kKeyGap : 0);
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
  for (int i = 0; i < kPages && i < int(lv_obj_get_child_count(pages)); i++) {
    page_pad[i] = lv_obj_get_style_pad_bottom(lv_obj_get_child(pages, i), LV_PART_MAIN);
  }
  w.bar = card(screen, kSurface, kBorder);
  lv_obj_set_pos(w.bar, kX, kY);
  lv_obj_set_size(w.bar, kW, kH);
  lv_obj_set_style_radius(w.bar, 12, 0);
  lv_obj_set_style_pad_ver(w.bar, 0, 0);
  lv_obj_set_style_pad_left(w.bar, kPadL - kChipPad, 0);
  lv_obj_set_style_pad_right(w.bar, kPadR, 0);
  lv_obj_set_style_bg_grad_dir(w.bar, LV_GRAD_DIR_HOR, 0);
  lv_obj_set_style_bg_main_stop(w.bar, 0, 0);
  lv_obj_set_style_bg_grad_stop(w.bar, 100, 0);  // the fade is done 40 % of the way across
  flexRow(w.bar, kKeyGap);

  lv_obj_t *chip = box(w.bar);
  lv_obj_set_size(chip, kChipW + kChipPad, kKeyH);  // its padding taken from the bar's
  flexRow(chip, 8);
  // A tap on the app's name goes to its page (Spotify to Audio, VS Code to Dev…).
  lv_obj_set_clickable(chip, true);
  lv_obj_add_event_cb(chip, onChip, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_style_radius(chip, 10, 0);
  lv_obj_set_style_pad_left(chip, kChipPad, 0);
  lv_obj_set_style_bg_opa(chip, LV_OPA_TRANSP, 0);
  lv_obj_set_style_bg_opa(chip, LV_OPA_COVER, LV_STATE_PRESSED);
  w.chip = chip;
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

  // A tap runs a key; a long press picks another action for its place.
  for (int i = 0; i < model::Context::kMax; i++) {
    KeyView &k = w.keys[i];
    k.root = button(w.bar, 80, kKeyH, kRaised, nullptr);
    lv_obj_add_event_cb(k.root, onKey, LV_EVENT_SHORT_CLICKED, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_add_event_cb(k.root, onKeyHold, LV_EVENT_LONG_PRESSED, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(k.root, 10, 0);
    lv_obj_set_style_pad_hor(k.root, kKeyPad, 0);
    flexRow(k.root, kIconGap);
    lv_obj_set_flex_align(k.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    k.icon = label(k.root, "", &font_icons_15, kAccent);
    k.name = label(k.root, "", &small_font, kText);
  }
  w.plus = button(w.bar, kPlusW, kKeyH, kRaised, nullptr);
  lv_obj_add_event_cb(w.plus, onPlus, LV_EVENT_SHORT_CLICKED, nullptr);
  lv_obj_add_event_cb(w.plus, onPlus, LV_EVENT_LONG_PRESSED, nullptr);
  lv_obj_set_style_radius(w.plus, 10, 0);
  w.plus_icon = label(w.plus, glyph("plus"), &font_icons_15, kText);
  lv_obj_center(w.plus_icon);
  lv_obj_set_hidden(w.bar, true);

  buildMenu();
}

void update() {
  // The pages make room for the bar while it has keys, each but the Numpad, whose keys
  // are where the bar goes, and they keep it whichever page is on show: going to and from
  // the Numpad then only hides or shows the bar. Making room laid out all six pages again,
  // a frame after the page had been drawn without it, which took the bar almost a second
  // to come back.
  const bool keys = pc::connected() && (model::context.count > 0 || model::context.bank_count > 0);
  if (keys != room) {
    room = keys;
    for (int i = 0; i < kPages && i < int(lv_obj_get_child_count(w.pages)); i++) {
      const bool slot = keys && i != kNumpadPage;
      lv_obj_set_style_pad_bottom(lv_obj_get_child(w.pages, i), page_pad[i] + (slot ? kSlot : 0), 0);
    }
  }
  const bool want = keys && currentPage() != kNumpadPage;
  if (want != shown) {
    shown = want;
    lv_obj_set_hidden(w.bar, !want);  // filled below if anything changed while it was hidden
  }
  if (model::picker.version != picker_seen) {  // a key's list came from the companion
    picker_seen = model::picker.version;
    if (shown) openPicker();
  }
  if (picking && (!shown || idle::asleep())) closeMenu();
  if (menu_slot >= 0) {
    // Gone with the bar, the screen going dark, or another app coming to the front.
    if (!shown || idle::asleep() || model::context.key != menu_key) closeMenu();
    else if (model::context.version != menu_seen) fillMenu();
  }
  if (!shown) return;
  const bool icon_due = model::context.icon.length() && !icons::get(model::context.icon) && millis() - asked_ms >= kIconRetryMs;
  if (model::context.version == context_seen && icons::version() == icons_seen && !icon_due) return;
  context_seen = model::context.version;
  icons_seen = icons::version();
  fill();
}

}  // namespace ui::ctxbar
