#include "page_windows.h"

#include "icons.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::windows {
namespace {

constexpr int kZonePanelW = 300;
constexpr int kTileW = 143;
constexpr int kTileH = 100;
constexpr int kThumbW = 40;
constexpr int kThumbH = 26;
constexpr uint32_t kCodeBg = 0x0A0C0F;
constexpr uint32_t kScreenBg = 0x14181D;
constexpr uint32_t kThumbOff = 0x4A525C;
constexpr uint32_t kPickHoldMs = 20000;  // a monitor picked by hand stays shown this long
constexpr uint32_t kIconRetryMs = 3000;

// Initials backgrounds for windows without an icon, from the design canvas.
constexpr uint32_t kTints[] = {0x2D5C8A, 0x3A3F2A, 0x3B3366, 0x1F4466, 0x4A2F66, 0x1E4A3A, 0x4D3B1E, 0x2E333A};

// Layout buttons come from the PC: the user's custom FancyZones layouts first, then
// built-in ones, each with its zones for a thumbnail.
constexpr int kLayoutCount = model::Zones::kMaxLayouts;
constexpr int kCellCount = model::LayoutChoice::kMaxCells;

struct Tile {
  lv_obj_t *root, *icon, *initials, *app, *title;
};

struct {
  Tile tiles[model::Windows::kMax];
  lv_obj_t *empty;
  lv_obj_t *layout_buttons[kLayoutCount], *layout_labels[kLayoutCount];
  lv_obj_t *layout_cells[kLayoutCount][kCellCount];
  lv_obj_t *snap_title, *map, *screen;
  lv_obj_t *zones[model::Monitor::kMaxZones], *zone_apps[model::Monitor::kMaxZones];
  int zone_count;
  lv_obj_t *monitors, *mon_buttons[model::Zones::kMax], *mon_labels[model::Zones::kMax];
} w;

uint32_t windows_seen = UINT32_MAX, zones_seen = UINT32_MAX, icons_seen = UINT32_MAX;
int connected_seen = -1;
int shown_seen = -2;
int32_t map_size_seen = -1;
uint32_t geometry_seen = UINT32_MAX;  // 0 is "no monitor": starting there left the empty map's placeholder showing
int picked = -1;
uint32_t picked_ms = 0;

struct Request {
  String id;
  uint32_t ms = 0;
};
Request requests[model::Windows::kMax];
int next_request = 0;

// The monitor the zone map shows: one picked by hand recently, else the active window's.
int shownMonitor() {
  const model::Zones &z = model::zones;
  if (z.mon_count == 0) return -1;
  if (picked >= 0 && picked < z.mon_count && millis() - picked_ms < kPickHoldMs) return picked;
  return z.active >= 0 && z.active < z.mon_count ? z.active : 0;
}

// ---- actions

int indexOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

// Tapping a window brings it forward; tapping the one in front minimizes it, as its
// taskbar button does.
void onTileTap(lv_event_t *e) {
  const int i = indexOf(e);
  if (i >= model::windows.count) return;
  const bool minimizing = model::windows.list[i].active;
  for (int t = 0; t < model::Windows::kMax; t++) {  // show it now; the PC confirms in ~300 ms
    const bool lit = t == i && !minimizing;
    lv_obj_set_style_bg_color(w.tiles[t].root, hex(lit ? kSelected : kRaised), 0);
    lv_obj_set_style_border_width(w.tiles[t].root, lit ? 2 : 0, 0);
  }
  pc::focusWindow(model::windows.list[i].id.c_str());
}

void onTileHold(lv_event_t *e) {
  const int i = indexOf(e);
  if (i >= model::windows.count) return;
  // Gone at once, as an unused slot; the PC's next list (~300 ms) no longer has it, or has
  // it back after a few seconds if the window stayed open (unsaved work, say).
  lv_obj_set_style_opa(w.tiles[i].root, LV_OPA_TRANSP, 0);
  lv_obj_set_clickable(w.tiles[i].root, false);
  pc::closeWindow(model::windows.list[i].id.c_str());
}

void onDesktop(lv_event_t *) { pc::showDesktop(); }
void onLock(lv_event_t *) { pc::lockPc(); }

void onLayout(lv_event_t *e) {
  const int mon = shownMonitor();
  const int i = indexOf(e);
  if (mon >= 0 && i < model::zones.layout_count) pc::setLayout(mon, model::zones.layouts[i].id.c_str());
}

void onZone(lv_event_t *e) {
  const int mon = shownMonitor();
  if (mon >= 0) pc::snapWindow(mon, indexOf(e));
}

void onMonitor(lv_event_t *e) {
  picked = indexOf(e);
  picked_ms = millis();
  update();
}

// ---- building

lv_obj_t *header(lv_obj_t *parent, const char *left, const char *right) {
  lv_obj_t *h = box(parent);
  lv_obj_set_size(h, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(h, 6);
  lv_obj_set_flex_align(h, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_t *l = label(h, left, &small_font, kMuted);
  if (right) label(h, right, &small_font, kMuted);
  return l;
}

void buildTile(lv_obj_t *grid, int i) {
  Tile &t = w.tiles[i];
  t.root = button(grid, kTileW, kTileH, kRaised, nullptr);
  lv_obj_set_grid_cell(t.root, LV_GRID_ALIGN_STRETCH, i % 3, 1, LV_GRID_ALIGN_STRETCH, i / 3, 1);
  lv_obj_set_style_radius(t.root, 12, 0);
  lv_obj_set_style_pad_all(t.root, 8, 0);
  lv_obj_set_style_border_color(t.root, hex(kAccent), 0);
  lv_obj_set_style_border_width(t.root, 0, 0);
  lv_obj_add_event_cb(t.root, onTileTap, LV_EVENT_SHORT_CLICKED, reinterpret_cast<void *>(intptr_t(i)));
  lv_obj_add_event_cb(t.root, onTileHold, LV_EVENT_LONG_PRESSED, reinterpret_cast<void *>(intptr_t(i)));
  // The icon beside the app and title: stacked, they didn't fit once the context bar took
  // some of the page's height.
  flexRow(t.root, 8);

  lv_obj_t *top = box(t.root);
  lv_obj_set_size(top, icons::kSize, icons::kSize);
  t.icon = lv_image_create(top);
  lv_obj_set_size(t.icon, icons::kSize, icons::kSize);
  t.initials = card(top, kTints[i % 8], kTints[i % 8]);
  lv_obj_set_size(t.initials, icons::kSize, icons::kSize);
  lv_obj_set_style_pad_all(t.initials, 0, 0);
  lv_obj_set_style_radius(t.initials, 8, 0);
  lv_obj_center(label(t.initials, "", &small_font, kText));

  lv_obj_t *text = box(t.root);
  lv_obj_set_size(text, 1, LV_SIZE_CONTENT);  // any width: the tile's row gives it the rest
  lv_obj_set_flex_grow(text, 1);
  flexCol(text, 0);
  t.app = label(text, "", &body_font, kText);
  ellipsize(t.app);
  t.title = label(text, "", &small_font, kMuted);
  ellipsize(t.title);
}

void buildWindows(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_flex_grow(c, 1);
  lv_obj_set_height(c, LV_PCT(100));
  flexCol(c, 8);
  lv_obj_t *head = box(c);
  lv_obj_set_size(head, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(head, 8);
  label(head, "OPEN WINDOWS", &small_font, kMuted);
  lv_obj_t *hint = label(head, "Tap: focus · hold: close", &small_font, kMuted);  // the longer one was cut
  fillRow(hint);
  lv_obj_set_style_text_align(hint, LV_TEXT_ALIGN_RIGHT, 0);
  lv_obj_t *lock = button(head, LV_SIZE_CONTENT, 28, kRaised, onLock);
  lv_obj_set_style_radius(lock, 8, 0);
  lv_obj_set_style_pad_hor(lock, 10, 0);
  lv_obj_center(label(lock, LV_SYMBOL_EYE_CLOSE "  Lock PC", &small_font, kText));

  // A 3x3 grid with fixed gaps: spacing tiles out with flex ran out of room once the
  // header grew, and the tiles ended up touching.
  static const int32_t cols[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  static const int32_t rows[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  lv_obj_t *grid = box(c);
  lv_obj_set_width(grid, LV_PCT(100));
  lv_obj_set_flex_grow(grid, 1);
  lv_obj_set_grid_dsc_array(grid, cols, rows);
  lv_obj_set_style_pad_column(grid, 8, 0);
  lv_obj_set_style_pad_row(grid, 8, 0);
  for (int i = 0; i < model::Windows::kMax; i++) buildTile(grid, i);

  lv_obj_t *desktop = button(grid, kTileW, kTileH, kSurface, onDesktop);
  lv_obj_set_grid_cell(desktop, LV_GRID_ALIGN_STRETCH, 2, 1, LV_GRID_ALIGN_STRETCH, 2, 1);
  lv_obj_set_style_radius(desktop, 12, 0);
  lv_obj_set_style_border_color(desktop, hex(0x2E343C), 0);
  lv_obj_set_style_border_width(desktop, 2, 0);
  flexCol(desktop, 6);
  lv_obj_set_flex_align(desktop, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  label(desktop, LV_SYMBOL_HOME, &lv_font_montserrat_20, kSoftText);
  label(desktop, "Show desktop", &body_font, kSoftText);

  w.empty = label(grid, "", &body_font, kMuted);
  lv_obj_set_ignore_layout(w.empty, true);
  lv_obj_center(w.empty);
}

void buildLayoutButton(lv_obj_t *parent, int i) {
  lv_obj_t *b = button(parent, 134, 40, kRaised, onLayout, reinterpret_cast<void *>(intptr_t(i)));
  lv_obj_set_style_radius(b, 12, 0);
  lv_obj_set_style_pad_hor(b, 8, 0);
  lv_obj_set_style_border_color(b, hex(kAccent), 0);
  flexRow(b, 6);
  lv_obj_t *thumb = box(b);
  lv_obj_set_size(thumb, kThumbW, kThumbH);
  for (int c = 0; c < kCellCount; c++) {  // placed and shown once the PC sends the layouts
    lv_obj_t *cell = box(thumb);
    lv_obj_set_style_radius(cell, 2, 0);
    lv_obj_set_style_bg_opa(cell, LV_OPA_COVER, 0);
    lv_obj_set_hidden(cell, true);
    w.layout_cells[i][c] = cell;
  }
  lv_obj_t *name = label(b, "", &small_font, kSoftText);
  fillRow(name);
  w.layout_labels[i] = name;
  w.layout_buttons[i] = b;
}

// A thumbnail cell's place, set only when it moves (each set relayouts the button).
void placeCell(lv_obj_t *cell, int x, int y, int cw, int ch) {
  if (lv_obj_get_style_x(cell, LV_PART_MAIN) != x || lv_obj_get_style_y(cell, LV_PART_MAIN) != y) lv_obj_set_pos(cell, x, y);
  if (lv_obj_get_style_width(cell, LV_PART_MAIN) != cw || lv_obj_get_style_height(cell, LV_PART_MAIN) != ch) {
    lv_obj_set_size(cell, cw, ch);
  }
}

void buildZones(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_size(c, kZonePanelW, LV_PCT(100));
  flexCol(c, 8);
  header(c, "FANCYZONES LAYOUT", nullptr);

  lv_obj_t *layouts = box(c);
  lv_obj_set_size(layouts, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(layouts, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_flex_align(layouts, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  lv_obj_set_style_pad_row(layouts, 8, 0);
  for (int i = 0; i < kLayoutCount; i++) buildLayoutButton(layouts, i);

  w.snap_title = label(c, "", &small_font, kMuted);
  ellipsize(w.snap_title);

  w.map = card(c, kCodeBg, kCodeBg);
  lv_obj_set_width(w.map, LV_PCT(100));
  lv_obj_set_flex_grow(w.map, 1);
  lv_obj_set_style_pad_all(w.map, 6, 0);
  w.screen = box(w.map);
  lv_obj_set_style_bg_color(w.screen, hex(kScreenBg), 0);
  lv_obj_set_style_bg_opa(w.screen, LV_OPA_COVER, 0);
  lv_obj_set_style_radius(w.screen, 6, 0);

  w.monitors = box(c);
  lv_obj_set_size(w.monitors, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(w.monitors, 8);
  for (int i = 0; i < model::Zones::kMax; i++) {
    lv_obj_t *b = button(w.monitors, 0, 36, kRaised, onMonitor, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_flex_grow(b, 1);
    lv_obj_set_style_radius(b, 10, 0);
    lv_obj_set_style_pad_hor(b, 6, 0);
    w.mon_labels[i] = label(b, "", &small_font, kSoftText);
    ellipsize(w.mon_labels[i]);
    lv_obj_set_style_text_align(w.mon_labels[i], LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_center(w.mon_labels[i]);
    w.mon_buttons[i] = b;
  }
}

// ---- updating

void initials(const String &app, char out[3]) {
  out[0] = app.length() > 0 ? app[0] : '?';
  out[1] = app.length() > 1 && app[1] != ' ' ? app[1] : '\0';
  out[2] = '\0';
}

// Asks for an icon the deck doesn't have, at most every few seconds per icon.
void requestIcon(const String &id) {
  for (Request &r : requests) {
    if (r.id == id) {
      if (millis() - r.ms < kIconRetryMs) return;
      r.ms = millis();
      pc::requestIcon(id.c_str());
      return;
    }
  }
  Request &r = requests[next_request];
  next_request = (next_request + 1) % model::Windows::kMax;
  r.id = id;
  r.ms = millis();
  pc::requestIcon(id.c_str());
}

void updateTiles() {
  const model::Windows &ws = model::windows;
  const bool online = pc::connected();
  const int count = online ? ws.count : 0;
  for (int i = 0; i < model::Windows::kMax; i++) {
    Tile &t = w.tiles[i];
    const bool used = i < count;
    // Unused tiles keep their place, so Show desktop stays in the corner.
    if (lv_obj_is_clickable(t.root) != used || lv_obj_get_style_opa(t.root, LV_PART_MAIN) != (used ? LV_OPA_COVER : LV_OPA_TRANSP)) {
      lv_obj_set_style_opa(t.root, used ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
      lv_obj_set_clickable(t.root, used);
    }
    if (!used) continue;
    const model::Window &win = ws.list[i];
    setBgColor(t.root, win.active ? kSelected : kRaised);
    if (lv_obj_get_style_border_width(t.root, LV_PART_MAIN) != (win.active ? 2 : 0)) lv_obj_set_style_border_width(t.root, win.active ? 2 : 0, 0);
    setText(t.app, win.app.c_str());
    setText(t.title, win.title.c_str());
    setTextColor(t.title, win.active ? kHighlightText : kMuted);
    // A minimized window keeps its tile, its icon dimmed; a tap brings it back.
    const lv_opa_t icon_opa = win.minimized ? LV_OPA_40 : LV_OPA_COVER;
    if (lv_obj_get_style_opa(t.icon, LV_PART_MAIN) != icon_opa) {
      lv_obj_set_style_opa(t.icon, icon_opa, 0);
      lv_obj_set_style_opa(t.initials, icon_opa, 0);
    }
    const lv_image_dsc_t *icon = icons::get(win.icon);
    if (!icon && win.icon.length()) requestIcon(win.icon);
    setHidden(t.icon, icon == nullptr);
    setHidden(t.initials, icon != nullptr);
    if (icon) {
      if (lv_image_get_src(t.icon) != icon) lv_image_set_src(t.icon, icon);
    } else {
      char letters[3];
      initials(win.app, letters);
      setText(lv_obj_get_child(t.initials, 0), letters);
    }
  }
  setText(w.empty, !online ? "PC offline" : count == 0 ? "No open windows" : "");
}

// Zones are rebuilt only when the shown monitor's geometry changes, or the map's size
// (the context bar coming or going): zones placed for the taller map ran past the shorter one.
uint32_t geometry(int mon) {
  if (mon < 0) return 0;
  const model::Monitor &m = model::zones.mons[mon];
  uint32_t h = 2166136261u ^ uint32_t(mon);
  auto mix = [&h](int v) { h = (h ^ uint32_t(v)) * 16777619u; };
  mix(lv_obj_get_content_width(w.map));
  mix(lv_obj_get_content_height(w.map));
  mix(m.w);
  mix(m.h);
  mix(m.zone_count);
  for (int i = 0; i < m.zone_count; i++) {
    mix(m.zones[i].x);
    mix(m.zones[i].y);
    mix(m.zones[i].w);
    mix(m.zones[i].h);
  }
  return h | 1;
}

void rebuildZones(int mon) {
  lv_obj_clean(w.screen);
  w.zone_count = 0;
  if (mon < 0) {
    lv_obj_set_size(w.screen, 0, 0);
    return;
  }
  const model::Monitor &m = model::zones.mons[mon];
  if (m.w <= 0 || m.h <= 0) return;
  lv_obj_update_layout(w.map);
  const int mw = lv_obj_get_content_width(w.map);
  const int mh = lv_obj_get_content_height(w.map);
  const float scale = fminf(float(mw) / m.w, float(mh) / m.h);
  const int sw = int(m.w * scale), sh = int(m.h * scale);
  lv_obj_set_size(w.screen, sw, sh);
  lv_obj_center(w.screen);
  for (int i = 0; i < m.zone_count; i++) {
    const model::Zone &z = m.zones[i];
    const int x = int(z.x * scale) + 2, y = int(z.y * scale) + 2;
    const int zw = int(z.w * scale) - 4, zh = int(z.h * scale) - 4;
    lv_obj_t *b = button(w.screen, zw > 8 ? zw : 8, zh > 8 ? zh : 8, kRaised, onZone, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_pos(b, x, y);
    lv_obj_set_style_radius(b, 6, 0);
    setBorderColor(b, kAccent);
    flexCol(b, 0);
    lv_obj_set_flex_align(b, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    char num[4];
    snprintf(num, sizeof(num), "%d", i + 1);
    label(b, num, zh >= 48 ? &lv_font_montserrat_20 : &lv_font_montserrat_14, kText);
    lv_obj_t *app = label(b, "", &small_font, kMuted);
    ellipsize(app);
    lv_obj_set_style_text_align(app, LV_TEXT_ALIGN_CENTER, 0);
    setHidden(app, zh < 48);  // the number and the app need about that much
    w.zones[i] = b;
    w.zone_apps[i] = app;
    w.zone_count++;
  }
}

void updateZones() {
  const model::Zones &z = model::zones;
  const bool online = pc::connected() && z.mon_count > 0;
  const int mon = online ? shownMonitor() : -1;
  const uint32_t geo = geometry(mon);
  if (geo != geometry_seen) {
    geometry_seen = geo;
    rebuildZones(mon);
  }

  if (!online) {
    setText(w.snap_title, pc::connected() ? "NO MONITORS" : "PC OFFLINE");
  } else if (z.app.length()) {
    String app = z.app;
    app.toUpperCase();
    setTextf(w.snap_title, "SNAP %s TO", app.c_str());
  } else {
    setText(w.snap_title, "FOCUS A WINDOW TO SNAP IT");
  }

  const model::Monitor *m = mon >= 0 ? &z.mons[mon] : nullptr;
  for (int i = 0; i < w.zone_count && m && i < m->zone_count; i++) {
    const String &app = m->zones[i].app;
    const bool here = app.length() && app == z.app;
    setText(w.zone_apps[i], app.c_str());
    setBgColor(w.zones[i], here ? kSelected : kRaised);
    if (lv_obj_get_style_border_width(w.zones[i], LV_PART_MAIN) != (here ? 2 : 0)) lv_obj_set_style_border_width(w.zones[i], here ? 2 : 0, 0);
    setTextColor(w.zone_apps[i], here ? kHighlightText : kMuted);
  }

  for (int i = 0; i < kLayoutCount; i++) {
    lv_obj_t *b = w.layout_buttons[i];
    const bool used = online && i < z.layout_count;
    setHidden(b, !used);
    if (!used) continue;
    const model::LayoutChoice &l = z.layouts[i];
    const bool on = m && m->layout == l.id;
    setBgColor(b, on ? kSelected : kRaised);
    if (lv_obj_get_style_border_width(b, LV_PART_MAIN) != (on ? 2 : 0)) lv_obj_set_style_border_width(b, on ? 2 : 0, 0);
    // A built-in layout shows its zone count once applied; tapping it again changes it.
    if (on && !l.custom) setTextf(w.layout_labels[i], "%s %d", l.name.c_str(), m->count);
    else setText(w.layout_labels[i], l.name.c_str());
    setTextColor(w.layout_labels[i], on ? kText : kSoftText);
    for (int c = 0; c < kCellCount; c++) {
      lv_obj_t *cell = w.layout_cells[i][c];
      const bool shown = c < l.cell_count;
      setHidden(cell, !shown);
      if (!shown) continue;
      placeCell(cell, l.cells[c].x, l.cells[c].y, l.cells[c].w, l.cells[c].h);
      setBgColor(cell, on ? kAccent : kThumbOff);
      const lv_opa_t opa = on && c > 0 ? LV_OPA_60 : LV_OPA_COVER;
      if (lv_obj_get_style_bg_opa(cell, LV_PART_MAIN) != opa) lv_obj_set_style_bg_opa(cell, opa, 0);
    }
  }

  // Monitor buttons only when there's a choice.
  setHidden(w.monitors, !online || z.mon_count < 2);
  for (int i = 0; i < model::Zones::kMax; i++) {
    const bool used = online && i < z.mon_count;
    setHidden(w.mon_buttons[i], !used);
    if (!used) continue;
    const bool on = i == mon;
    setTextf(w.mon_labels[i], "%s%s", z.mons[i].primary ? LV_SYMBOL_HOME " " : "", z.mons[i].name.c_str());
    setBgColor(w.mon_buttons[i], on ? kSelected : kRaised);
    setTextColor(w.mon_labels[i], on ? kText : kSoftText);
  }
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  flexRow(page, kGap);
  lv_obj_set_flex_align(page, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  buildWindows(page);
  buildZones(page);
}

void update() {
  const int connected = pc::connected() ? 1 : 0;
  if (model::windows.version != windows_seen || icons::version() != icons_seen || connected != connected_seen) {
    windows_seen = model::windows.version;
    icons_seen = icons::version();
    connected_seen = connected;
    updateTiles();
  }
  const int shown = connected ? shownMonitor() : -1;
  const int32_t map_size = lv_obj_get_width(w.map) << 16 | lv_obj_get_height(w.map);  // the context bar changes it
  if (model::zones.version != zones_seen || shown != shown_seen || map_size != map_size_seen) {
    zones_seen = model::zones.version;
    shown_seen = shown;
    map_size_seen = map_size;
    updateZones();
  }
}

}  // namespace ui::windows
