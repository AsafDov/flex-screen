#include "launcher.h"

#include <Arduino.h>
#include <time.h>

#include "icons.h"
#include "idle.h"
#include "model.h"
#include "page_numpad.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::launcher {
namespace {

// Square tiles, six to a row: twelve make two rows. The panel is as wide as they need and
// sits above the launcher button.
constexpr int kCols = 6;
constexpr int kTile = 104;
constexpr int kTileGap = 8;
constexpr int kPad = 14, kBorderW = 2;
constexpr int kW = kCols * kTile + (kCols - 1) * kTileGap + 2 * (kPad + kBorderW);
constexpr int kHeadH = 40;  // the Numpad button's height
constexpr int kBadge = 40;
constexpr int kButtonH = 44;
constexpr uint32_t kIconRetryMs = 3000;  // asking again for an icon the deck lacks

constexpr int kTilePadHor = 5;
constexpr int kNameW = kTile - 2 * kTilePadHor;

struct Tile {
  lv_obj_t *root, *badge, *image, *glyph, *name;
  uint32_t asked_ms;
  int lines;  // the name's height, in lines (1 or 2)
};

struct {
  lv_obj_t *button, *button_icon;
  lv_obj_t *scrim, *panel, *note, *grid;
  Tile tiles[model::Shortcuts::kMax];
} w;

bool is_open = false;
bool open_seen = true;
uint32_t shortcuts_seen = UINT32_MAX, icons_seen = UINT32_MAX, tick_ms = 0;
int link_seen = -1;
int64_t day_seen = -1;

void setOpen(bool open) {
  is_open = open;
  if (open) shortcuts_seen = UINT32_MAX;  // fill it afresh
}

void onButton(lv_event_t *) { setOpen(!is_open); }
void onScrim(lv_event_t *) { setOpen(false); }
// The Numpad isn't a page: it pops up from here, over whatever page is on show.
void onNumpad(lv_event_t *) {
  setOpen(false);
  numpad::open();
}

void onTile(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::shortcuts.count && pc::connected()) pc::runShortcut(model::shortcuts.list[i].id.c_str());
  setOpen(false);
}

// Four small squares: the launcher's symbol (there's no such glyph in the fonts).
lv_obj_t *gridIcon(lv_obj_t *parent, uint32_t color) {
  constexpr int kDot = 7, kGap = 4;
  lv_obj_t *g = box(parent);
  lv_obj_set_size(g, 2 * kDot + kGap, 2 * kDot + kGap);
  for (int i = 0; i < 4; i++) {
    lv_obj_t *dot = card(g, color, color);
    lv_obj_set_size(dot, kDot, kDot);
    lv_obj_set_style_pad_all(dot, 0, 0);
    lv_obj_set_style_border_width(dot, 0, 0);
    lv_obj_set_style_radius(dot, 2, 0);
    lv_obj_set_pos(dot, (i % 2) * (kDot + kGap), (i / 2) * (kDot + kGap));
  }
  return g;
}

void setGridColor(lv_obj_t *g, uint32_t color) {
  for (uint32_t i = 0; i < lv_obj_get_child_count(g); i++) setBgColor(lv_obj_get_child(g, i), color);
}

// A tile's symbol and colors when it has no icon, by the name the companion gave it.
struct Look {
  const char *glyph;
  uint32_t bg, fg;
};

Look look(const String &glyph) {
  if (glyph == "mail") return {LV_SYMBOL_ENVELOPE, 0x3A2A10, kAmber};
  if (glyph == "calendar") return {"", kHighlight, kAccent};  // today's date
  if (glyph == "note") return {LV_SYMBOL_FILE, 0x2B2142, 0xB49CFF};
  if (glyph == "terminal") return {">_", 0x0A0C0F, kAccent};
  if (glyph == "code") return {"</>", 0x10273D, kAccent};
  if (glyph == "music" || glyph == "media") return {LV_SYMBOL_PLAY, 0x143A2E, kGreen};
  if (glyph == "volume") return {LV_SYMBOL_VOLUME_MAX, kHighlight, kAccent};
  if (glyph == "lock") return {LV_SYMBOL_POWER, 0x3A1A1A, 0xFF8A80};
  if (glyph == "desktop") return {LV_SYMBOL_HOME, kSurface, kText};
  if (glyph == "folder") return {LV_SYMBOL_DIRECTORY, 0x3A2A10, kAmber};
  if (glyph == "keys") return {LV_SYMBOL_KEYBOARD, kSurface, kText};
  if (glyph == "git") return {LV_SYMBOL_SHUFFLE, 0x3A2318, 0xE8936A};
  if (glyph == "call") return {LV_SYMBOL_CALL, 0x143A2E, kGreen};
  return {"", 0x2D5C8A, kText};  // the name's first letter
}

void buildTiles() {
  const int line = lv_font_get_line_height(&small_font);
  for (int i = 0; i < model::Shortcuts::kMax; i++) {
    Tile &t = w.tiles[i];
    t.asked_ms = 0;
    t.root = button(w.grid, kTile, kTile, kRaised, onTile, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(t.root, 14, 0);
    lv_obj_set_style_pad_ver(t.root, 6, 0);
    lv_obj_set_style_pad_hor(t.root, kTilePadHor, 0);
    flexCol(t.root, 7);
    // The badge and the name together, in the middle of the square.
    lv_obj_set_flex_align(t.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);

    t.badge = card(t.root, kSurface, kSurface);
    lv_obj_set_size(t.badge, kBadge, kBadge);
    lv_obj_set_style_pad_all(t.badge, 0, 0);
    lv_obj_set_style_radius(t.badge, 11, 0);
    t.image = lv_image_create(t.badge);
    lv_obj_set_size(t.image, icons::kSize, icons::kSize);
    lv_obj_center(t.image);
    t.glyph = label(t.badge, "", &lv_font_montserrat_20, kText);
    lv_obj_center(t.glyph);

    // Up to two lines of name, then "…" (which needs a fixed height: one line or two, set
    // to what the name takes).
    t.name = label(t.root, "", &small_font, kText);
    lv_obj_set_width(t.name, kNameW);
    lv_obj_set_style_text_align(t.name, LV_TEXT_ALIGN_CENTER, 0);
    lv_label_set_long_mode(t.name, LV_LABEL_LONG_MODE_DOTS);
    t.lines = 1;
    lv_obj_set_height(t.name, line);
  }
}

// How many lines (1 or 2) a name takes in a tile.
int nameLines(const char *name) {
  lv_point_t size;
  lv_text_get_size(&size, name, &small_font, 0, 0, kNameW, LV_TEXT_FLAG_NONE);
  return size.y > lv_font_get_line_height(&small_font) ? 2 : 1;
}

void fill() {
  const model::Shortcuts &s = model::shortcuts;
  const bool online = pc::connected();
  const int count = online ? s.count : 0;
  setText(w.note, !online ? "PC offline" : "No shortcuts yet. Add apps, links and actions from the tray icon on the PC: Shortcuts…");
  setHidden(w.note, count > 0);
  setHidden(w.grid, count == 0);

  const int64_t now = model::localNow();
  char day[4] = "";
  if (now > 0) {
    const time_t t = time_t(now);
    struct tm tm;
    gmtime_r(&t, &tm);  // already shifted to local time
    snprintf(day, sizeof(day), "%d", tm.tm_mday);
  }
  for (int i = 0; i < model::Shortcuts::kMax; i++) {
    Tile &t = w.tiles[i];
    setHidden(t.root, i >= count);
    if (i >= count) continue;
    const model::Shortcut &x = s.list[i];
    const int lines = nameLines(x.name.c_str());
    if (lines != t.lines) {
      t.lines = lines;
      lv_obj_set_height(t.name, lines * lv_font_get_line_height(&small_font));
      lv_label_set_text(t.name, "");  // cut again for the new height
    }
    setText(t.name, x.name.c_str());
    const lv_image_dsc_t *icon = icons::get(x.icon);
    if (!icon && x.icon.length() && millis() - t.asked_ms >= kIconRetryMs) {
      t.asked_ms = millis();
      pc::requestIcon(x.icon.c_str());
    }
    setHidden(t.image, icon == nullptr);
    setHidden(t.glyph, icon != nullptr);
    if (icon) {
      if (lv_image_get_src(t.image) != icon) lv_image_set_src(t.image, icon);
      setBgColor(t.badge, kSurface);
      continue;
    }
    const Look l = look(x.glyph);
    char letter[2] = {x.name.length() ? char(toupper(x.name[0])) : '?', 0};
    const bool calendar = x.glyph == "calendar" && day[0];
    setText(t.glyph, calendar ? day : l.glyph[0] ? l.glyph : letter);
    setTextColor(t.glyph, l.fg);
    setBgColor(t.badge, l.bg);
  }
}

}  // namespace

void build(lv_obj_t *rail) {
  w.button = box(rail);
  lv_obj_set_size(w.button, LV_PCT(100), kButtonH);
  lv_obj_set_clickable(w.button, true);
  lv_obj_set_style_radius(w.button, 10, 0);
  lv_obj_set_style_bg_color(w.button, hex(kSelected), LV_STATE_PRESSED);
  lv_obj_set_style_bg_opa(w.button, LV_OPA_COVER, LV_STATE_PRESSED);
  lv_obj_add_event_cb(w.button, onButton, LV_EVENT_CLICKED, nullptr);
  w.button_icon = gridIcon(w.button, kMuted);
  lv_obj_center(w.button_icon);

  // Taps beside the panel close it rather than reaching the page underneath.
  w.scrim = box(lv_layer_top());
  lv_obj_set_size(w.scrim, 800, 480);
  lv_obj_set_clickable(w.scrim, true);
  lv_obj_add_event_cb(w.scrim, onScrim, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_hidden(w.scrim, true);

  w.panel = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_size(w.panel, kW, LV_SIZE_CONTENT);
  lv_obj_align(w.panel, LV_ALIGN_BOTTOM_LEFT, kColX, -(480 - kRailH));  // beside the rail's button, above the context bar
  lv_obj_set_style_border_width(w.panel, kBorderW, 0);
  lv_obj_set_style_radius(w.panel, 18, 0);
  lv_obj_set_style_pad_all(w.panel, kPad, 0);
  lv_obj_set_style_shadow_width(w.panel, 30, 0);
  lv_obj_set_style_shadow_color(w.panel, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.panel, LV_OPA_60, 0);
  lv_obj_set_clickable(w.panel, true);  // a tap between tiles stays on the panel
  flexCol(w.panel, 10);

  lv_obj_t *head = box(w.panel);
  lv_obj_set_size(head, LV_PCT(100), kHeadH);
  flexRow(head, 10);
  gridIcon(head, kMuted);
  label(head, "Shortcuts", &title_font, kText);
  lv_obj_t *gap = box(head);
  lv_obj_set_size(gap, 1, 1);
  lv_obj_set_flex_grow(gap, 1);
  label(head, "Change them from the tray icon: Shortcuts…", &small_font, kMuted);
  lv_obj_t *numpad = button(head, LV_SIZE_CONTENT, kHeadH, kRaised, onNumpad);
  lv_obj_set_style_radius(numpad, 10, 0);
  lv_obj_set_style_pad_hor(numpad, 14, 0);
  lv_obj_set_style_margin_left(numpad, 6, 0);
  flexRow(numpad, 8);
  label(numpad, LV_SYMBOL_KEYBOARD, &lv_font_montserrat_16, kAccent);
  label(numpad, "Numpad", &body_font, kText);

  w.note = label(w.panel, "", &body_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);
  lv_obj_set_style_pad_ver(w.note, 18, 0);

  w.grid = box(w.panel);
  lv_obj_set_size(w.grid, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(w.grid, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_style_pad_column(w.grid, kTileGap, 0);
  lv_obj_set_style_pad_row(w.grid, kTileGap, 0);
  buildTiles();
  lv_obj_set_hidden(w.panel, true);
}

void open() { setOpen(true); }
void close() { setOpen(false); }

void update() {
  if (is_open && idle::asleep()) setOpen(false);  // woken up, it's the page again
  if (is_open != open_seen) {
    open_seen = is_open;
    setHidden(w.scrim, !is_open);
    setHidden(w.panel, !is_open);
    lv_obj_set_style_bg_color(w.button, hex(kSelected), 0);
    lv_obj_set_style_bg_opa(w.button, is_open ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
    setGridColor(w.button_icon, is_open ? kText : kMuted);
  }
  if (!is_open) return;  // filled when it opens
  const int online = pc::connected();
  const int64_t day = model::localNow() / 86400;
  const bool icon_due = millis() - tick_ms >= kIconRetryMs;
  if (model::shortcuts.version == shortcuts_seen && icons::version() == icons_seen && online == link_seen && day == day_seen && !icon_due) {
    return;
  }
  shortcuts_seen = model::shortcuts.version;
  icons_seen = icons::version();
  link_seen = online;
  day_seen = day;
  tick_ms = millis();
  fill();
}

}  // namespace ui::launcher
