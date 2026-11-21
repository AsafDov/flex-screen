#include "page_windows.h"

#include "icons.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::windows {
namespace {

constexpr int kZonePanelW = 234;  // the tiles get the rest: the pages are 704 px wide beside the rail
constexpr int kTileW = 143;
constexpr int kTileH = 100;
constexpr int kThumbW = 28;
constexpr int kThumbH = 18;
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

// ---- the desktop map: the open windows card's other view
//
// The monitors as they're arranged, each open window drawn where it is on them, the one in
// front on top. A tap on a window brings it up (or minimizes the one in front, as a tile
// does); a drag moves it: the FancyZones zones show while it's dragged, the one under the
// finger lit, and letting go snaps the window into it, on whichever monitor. On a monitor
// without zones it goes where it's let go, its size kept. Let go off the monitors, or back
// in the zone it already fills, and nothing happens. Minimized windows stay on the tiles.
constexpr int kMaxMons = model::Zones::kMax;
constexpr int kMaxZoneViews = kMaxMons * model::Monitor::kMaxZones;
constexpr int kMapPad = 4;
constexpr int kDragStart = 8;  // px a press moves before it's a drag

struct MapWin {
  lv_obj_t *root, *icon, *name;
  int index;  // in model::windows.list; -1 when unused
};

struct {
  bool on = false;  // the map shows instead of the tiles
  lv_obj_t *grid, *toggle_label, *hint;
  lv_obj_t *area, *note;
  lv_obj_t *mons[kMaxMons], *mon_names[kMaxMons];
  lv_obj_t *zones[kMaxZoneViews];
  int8_t zone_mon[kMaxZoneViews], zone_index[kMaxZoneViews];  // bytes: this sits in internal RAM
  int zone_count = 0;
  MapWin wins[model::Windows::kMax];
  // The PC's screens onto the map: map = (screen - v) * scale + o.
  float scale = 0;
  int vx = 0, vy = 0, ox = 0, oy = 0;
  uint32_t windows_seen = UINT32_MAX, zones_seen = UINT32_MAX, icons_seen = UINT32_MAX;
  int32_t size_seen = -1;
  int connected_seen = -1;
} mp;

struct {
  int slot = -1;  // the MapWin pressed
  bool dragging = false;
  lv_point_t start;  // the finger when it went down, on the deck's screen
  int32_t x0 = 0, y0 = 0;  // the window's place on the map then
  int lit = -1;            // the zone view under the finger (this drag's or a tile's)
} drag;

// A tile dragged: the desktop map comes up in the tiles' place with the zones showing, a
// chip of the window under the finger, and letting go on a zone snaps the window into it;
// then the tiles come back. No Map press first.
constexpr int kTileDragStart = 16;  // px: as far as the hold ring lets the finger wander (past it there's no hold)
constexpr int kGhostLift = 14;      // px the chip sits above the finger, so the finger doesn't hide it

struct {
  int tile = -1;          // the tile pressed
  bool dragging = false;  // the map is up for it
  bool dragged = false;   // this press became a drag: its tap and hold don't count (until the next press)
  String id;              // the window's: the list may change under the finger
  lv_point_t start;
  lv_obj_t *ghost, *icon, *initials, *name;
} tile_drag;

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
  if (i >= model::windows.count || tile_drag.dragged) return;
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
  // LVGL's long press comes during a drag too (it's timed from the press): not a close then.
  if (i >= model::windows.count || tile_drag.dragged) return;
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

// ---- the desktop map's actions

int mapX(int x) { return mp.ox + int(lroundf((x - mp.vx) * mp.scale)); }
int mapY(int y) { return mp.oy + int(lroundf((y - mp.vy) * mp.scale)); }
int screenX(int x) { return mp.vx + int(lroundf((x - mp.ox) / mp.scale)); }
int screenY(int y) { return mp.vy + int(lroundf((y - mp.oy) / mp.scale)); }

// The finger on the map, in the PC's screen pixels.
lv_point_t fingerOnScreens(const lv_point_t &p) {
  lv_area_t a;
  lv_obj_get_coords(mp.area, &a);
  return {screenX(p.x - a.x1), screenY(p.y - a.y1)};
}

int monitorAt(int x, int y) {
  const model::Zones &z = model::zones;
  for (int i = 0; i < z.mon_count; i++) {
    const model::Monitor &m = z.mons[i];
    if (x >= m.x && x < m.x + m.w && y >= m.y && y < m.y + m.h) return i;
  }
  return -1;
}

// The zone view at that point, -1 for none.
int zoneViewAt(int x, int y) {
  const int mon = monitorAt(x, y);
  if (mon < 0) return -1;
  const model::Monitor &m = model::zones.mons[mon];
  for (int v = 0; v < mp.zone_count; v++) {
    if (mp.zone_mon[v] != mon) continue;
    const model::Zone &z = m.zones[mp.zone_index[v]];
    if (x >= m.x + z.x && x < m.x + z.x + z.w && y >= m.y + z.y && y < m.y + z.y + z.h) return v;
  }
  return -1;
}

void showZones(bool show) {
  for (int v = 0; v < mp.zone_count; v++) setHidden(mp.zones[v], !show);
}

void lightZone(int v) {
  if (v == drag.lit) return;
  if (drag.lit >= 0) lv_obj_set_style_bg_opa(mp.zones[drag.lit], LV_OPA_TRANSP, 0);
  if (v >= 0) lv_obj_set_style_bg_opa(mp.zones[v], LV_OPA_40, 0);
  drag.lit = v;
}

// How much two boxes overlap, 0 to 1 (as the companion judges a window filling a zone).
float overlap(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh) {
  const int w = min(ax + aw, bx + bw) - max(ax, bx), h = min(ay + ah, by + bh) - max(ay, by);
  if (w <= 0 || h <= 0) return 0;
  const float inter = float(w) * h;
  return inter / (float(aw) * ah + float(bw) * bh - inter);
}

// A window let go where the PC is to move it stays there until the PC's next list (~300 ms)
// says where it went; with no word this long, it's laid out again as the PC last said.
constexpr uint32_t kMovedWaitMs = 1500;
uint32_t relayout_ms = 0;

// Without a move asked for, the map is laid out again at once (the window goes back).
void endDrag(bool moved = false) {
  lightZone(-1);
  showZones(false);
  drag.slot = -1;
  drag.dragging = false;
  if (moved) relayout_ms = millis() + kMovedWaitMs;
  else mp.windows_seen = UINT32_MAX;
}

void onMapPress(lv_event_t *e) {
  lv_indev_t *indev = lv_indev_active();
  const int slot = indexOf(e);
  if (!indev || mp.wins[slot].index < 0) return;
  drag.slot = slot;
  drag.dragging = false;
  drag.lit = -1;
  lv_indev_get_point(indev, &drag.start);
  drag.x0 = lv_obj_get_x(mp.wins[slot].root);
  drag.y0 = lv_obj_get_y(mp.wins[slot].root);
}

void onMapPressing(lv_event_t *e) {
  lv_indev_t *indev = lv_indev_active();
  const int slot = indexOf(e);
  if (!indev || slot != drag.slot) return;
  lv_point_t p;
  lv_indev_get_point(indev, &p);
  const int dx = p.x - drag.start.x, dy = p.y - drag.start.y;
  lv_obj_t *root = mp.wins[slot].root;
  if (!drag.dragging) {
    if (abs(dx) < kDragStart && abs(dy) < kDragStart) return;
    drag.dragging = true;
    lv_obj_move_foreground(root);
    showZones(true);
  }
  lv_obj_set_pos(root, drag.x0 + dx, drag.y0 + dy);
  const lv_point_t at = fingerOnScreens(p);
  lightZone(zoneViewAt(at.x, at.y));
}

// Asks the PC to move the window where it was let go; false when it isn't to move.
bool drop(int slot, const lv_point_t &p) {
  const model::Window &win = model::windows.list[mp.wins[slot].index];
  const lv_point_t at = fingerOnScreens(p);
  const int mon = monitorAt(at.x, at.y);
  if (mon < 0) return false;  // off the monitors: it goes back
  const model::Monitor &m = model::zones.mons[mon];
  lv_obj_t *root = mp.wins[slot].root;
  const int v = zoneViewAt(at.x, at.y);
  if (v >= 0) {
    const model::Zone &z = m.zones[mp.zone_index[v]];
    // Back in the zone it fills already: nothing (the companion would maximize it, as a tap
    // on the zone map does).
    if (overlap(win.x, win.y, win.w, win.h, m.x + z.x, m.y + z.y, z.w, z.h) > 0.9f) return false;
    pc::snapWindowTo(win.id.c_str(), mon, mp.zone_index[v]);
    lv_obj_set_pos(root, mapX(m.x + z.x), mapY(m.y + z.y));  // there at once
    lv_obj_set_size(root, max(int(z.w * mp.scale), 12), max(int(z.h * mp.scale), 12));
    return true;
  }
  pc::moveWindow(win.id.c_str(), mon, screenX(lv_obj_get_x(root)) - m.x, screenY(lv_obj_get_y(root)) - m.y);
  return true;
}

void onMapRelease(lv_event_t *e) {
  lv_indev_t *indev = lv_indev_active();
  const int slot = indexOf(e);
  if (slot != drag.slot) return;
  const int index = mp.wins[slot].index;
  bool moved = false;
  if (index >= 0 && index < model::windows.count) {
    if (!drag.dragging) {
      pc::focusWindow(model::windows.list[index].id.c_str());
    } else if (indev) {
      lv_point_t p;
      lv_indev_get_point(indev, &p);
      moved = drop(slot, p);
    }
  }
  endDrag(moved);
}

void onMapLost(lv_event_t *e) {
  if (indexOf(e) == drag.slot) endDrag();
}

void showMap(bool on) {
  mp.on = on;
  setHidden(mp.grid, on);
  setHidden(mp.area, !on);
  setText(mp.toggle_label, on ? "Tiles" : "Map");
  setText(mp.hint, on ? "Drag: move" : "Tap: focus · hold: close");  // a longer one was cut beside Tiles
  mp.windows_seen = UINT32_MAX;
}

void onToggle(lv_event_t *) {
  if (drag.slot >= 0) endDrag();
  showMap(!mp.on);
}

// ---- a tile dragged onto the map

void updateMap();

const model::Window *windowById(const String &id) {
  for (int i = 0; i < model::windows.count; i++) {
    if (model::windows.list[i].id == id) return &model::windows.list[i];
  }
  return nullptr;
}

// The map is placed (and the PC's screens can be read off it).
bool mapPlaced() { return mp.scale > 0 && pc::connected() && model::zones.mon_count > 0; }

void placeGhost(const lv_point_t &p) {
  lv_obj_t *g = tile_drag.ghost;
  const int gw = lv_obj_get_width(g), gh = lv_obj_get_height(g);
  const int x = max(0, min(int(p.x) - gw / 2, int(lv_obj_get_width(lv_layer_top())) - gw));
  const int y = max(0, int(p.y) - gh - kGhostLift);
  lv_obj_set_pos(g, x, y);
}

void lightZoneAt(const lv_point_t &p) {
  if (!mapPlaced()) return;
  const lv_point_t at = fingerOnScreens(p);
  lightZone(zoneViewAt(at.x, at.y));
}

// The map comes up in the tiles' place, the zones showing, the window's chip under the finger.
void startTileDrag(const model::Window &win, const lv_point_t &p) {
  tile_drag.dragged = true;
  tile_drag.id = win.id;
  if (drag.slot >= 0) endDrag();
  showMap(true);
  setText(mp.hint, "Let go on a zone");
  updateMap();  // placed now, not at the next update: the zones are needed under the finger at once
  tile_drag.dragging = true;  // from here the map holds still until it's let go
  // The zones are what's being picked: over the windows, which fade back (the map's own
  // drag keeps them under the windows, beside the one moving).
  for (MapWin &mw : mp.wins) lv_obj_set_style_opa(mw.root, LV_OPA_30, 0);
  for (int v = 0; v < mp.zone_count; v++) lv_obj_move_foreground(mp.zones[v]);
  showZones(true);
  drag.lit = -1;

  const lv_image_dsc_t *icon = icons::get(win.icon);
  setHidden(tile_drag.icon, icon == nullptr);
  setHidden(tile_drag.initials, icon != nullptr);
  if (icon) {
    if (lv_image_get_src(tile_drag.icon) != icon) lv_image_set_src(tile_drag.icon, icon);
  } else {
    char letters[3] = {win.app.length() ? win.app[0] : '?', win.app.length() > 1 && win.app[1] != ' ' ? win.app[1] : '\0', '\0'};
    setText(lv_obj_get_child(tile_drag.initials, 0), letters);
  }
  setText(tile_drag.name, win.app.c_str());
  lv_obj_move_foreground(tile_drag.ghost);
  lv_obj_set_hidden(tile_drag.ghost, false);
  lv_obj_update_layout(tile_drag.ghost);
  placeGhost(p);
  lightZoneAt(p);
}

// Where the dragged tile's window goes: into the zone under the finger, or, on a monitor
// with no zone there, its middle where the finger is, its size kept. Off the monitors, or
// in the zone it fills already, nowhere.
void dropTile(const lv_point_t &p) {
  const model::Window *win = windowById(tile_drag.id);
  if (!win || !mapPlaced()) return;
  const lv_point_t at = fingerOnScreens(p);
  const int mon = monitorAt(at.x, at.y);
  if (mon < 0) return;
  const model::Monitor &m = model::zones.mons[mon];
  const int v = zoneViewAt(at.x, at.y);
  if (v >= 0) {
    const model::Zone &z = m.zones[mp.zone_index[v]];
    if (!win->minimized && overlap(win->x, win->y, win->w, win->h, m.x + z.x, m.y + z.y, z.w, z.h) > 0.9f) return;
    pc::snapWindowTo(win->id.c_str(), mon, mp.zone_index[v]);
    return;
  }
  // A minimized window's place is Windows' stand-in, not its size: its corner goes there.
  const int ww = win->minimized ? 0 : win->w, wh = win->minimized ? 0 : win->h;
  pc::moveWindow(win->id.c_str(), mon, at.x - m.x - ww / 2, at.y - m.y - wh / 2);
}

void endTileDrag() {
  tile_drag.tile = -1;
  if (!tile_drag.dragging) return;
  tile_drag.dragging = false;
  lv_obj_set_hidden(tile_drag.ghost, true);
  lightZone(-1);
  showZones(false);
  for (MapWin &mw : mp.wins) lv_obj_set_style_opa(mw.root, LV_OPA_COVER, 0);
  showMap(false);  // laid out afresh when it next shows: the windows over the zones again
}

void onTilePress(lv_event_t *e) {
  lv_indev_t *indev = lv_indev_active();
  tile_drag.dragged = false;
  tile_drag.tile = -1;
  if (!indev || indexOf(e) >= model::windows.count || mp.on) return;
  tile_drag.tile = indexOf(e);
  lv_indev_get_point(indev, &tile_drag.start);
}

void onTilePressing(lv_event_t *e) {
  lv_indev_t *indev = lv_indev_active();
  const int i = indexOf(e);
  if (!indev || i != tile_drag.tile) return;
  lv_point_t p;
  lv_indev_get_point(indev, &p);
  if (!tile_drag.dragging) {
    const int dx = p.x - tile_drag.start.x, dy = p.y - tile_drag.start.y;
    if (abs(dx) <= kTileDragStart && abs(dy) <= kTileDragStart) return;
    // Held to close already (the tile's gone), or the list moved on under the finger.
    if (!lv_obj_is_clickable(w.tiles[i].root) || i >= model::windows.count) {
      tile_drag.tile = -1;
      return;
    }
    startTileDrag(model::windows.list[i], p);
    return;
  }
  placeGhost(p);
  lightZoneAt(p);
}

void onTileRelease(lv_event_t *e) {
  lv_indev_t *indev = lv_indev_active();
  if (indexOf(e) != tile_drag.tile) return;
  if (tile_drag.dragging && indev) {
    lv_point_t p;
    lv_indev_get_point(indev, &p);
    dropTile(p);
  }
  endTileDrag();
}

void onTileLost(lv_event_t *e) {
  if (indexOf(e) == tile_drag.tile) endTileDrag();
}

// The chip that follows the finger: the window's icon (or initials) and app.
void buildGhost() {
  lv_obj_t *g = tile_drag.ghost = card(lv_layer_top(), kSelected, kAccent);
  lv_obj_set_clickable(g, false);
  lv_obj_set_size(g, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  lv_obj_set_style_max_width(g, 180, 0);
  lv_obj_set_style_border_width(g, 2, 0);
  lv_obj_set_style_radius(g, 10, 0);
  lv_obj_set_style_pad_all(g, 6, 0);
  lv_obj_set_style_pad_right(g, 10, 0);
  lv_obj_set_style_shadow_width(g, 16, 0);
  lv_obj_set_style_shadow_opa(g, LV_OPA_50, 0);
  lv_obj_set_style_shadow_color(g, hex(0x000000), 0);
  flexRow(g, 8);
  lv_obj_set_flex_align(g, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_t *top = box(g);
  lv_obj_set_size(top, icons::kSize, icons::kSize);
  tile_drag.icon = lv_image_create(top);
  lv_obj_set_size(tile_drag.icon, icons::kSize, icons::kSize);
  tile_drag.initials = card(top, kTints[0], kTints[0]);
  lv_obj_set_size(tile_drag.initials, icons::kSize, icons::kSize);
  lv_obj_set_style_pad_all(tile_drag.initials, 0, 0);
  lv_obj_set_style_radius(tile_drag.initials, 8, 0);
  lv_obj_center(label(tile_drag.initials, "", &small_font, kText));
  tile_drag.name = label(g, "", &body_font, kHighlightText);
  lv_label_set_long_mode(tile_drag.name, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_style_max_width(tile_drag.name, 180 - icons::kSize - 32, 0);
  lv_obj_set_height(tile_drag.name, lv_font_get_line_height(&body_font));  // one line, then "…"
  lv_obj_set_hidden(g, true);
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
  holdRing(t.root, 0xFF9A70);  // closes the window
  // A drag takes the window to a zone on the map: not a swipe to another page, nor a scroll.
  void *user = reinterpret_cast<void *>(intptr_t(i));
  lv_obj_set_gesture_bubble(t.root, false);
  lv_obj_set_scroll_chain_hor(t.root, false);
  lv_obj_set_scroll_chain_ver(t.root, false);
  lv_obj_add_event_cb(t.root, onTilePress, LV_EVENT_PRESSED, user);
  lv_obj_add_event_cb(t.root, onTilePressing, LV_EVENT_PRESSING, user);
  lv_obj_add_event_cb(t.root, onTileRelease, LV_EVENT_RELEASED, user);
  lv_obj_add_event_cb(t.root, onTileLost, LV_EVENT_PRESS_LOST, user);
  // The icon above the app and title, so they have the tile's whole width: beside the rail
  // the tiles are ~126 px wide, and beside the icon "Obsidian" read "Obsid…". (With the
  // tab bar gone there's the height for it again.)
  flexCol(t.root, 4);
  lv_obj_set_flex_align(t.root, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);

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
  lv_obj_set_size(text, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(text, 0);
  t.app = label(text, "", &body_font, kText);
  ellipsize(t.app);
  t.title = label(text, "", &small_font, kMuted);
  ellipsize(t.title);
}

// The map's pieces, made once and placed when the windows or monitors change: monitors at
// the back, the zones over them (shown while a window is dragged), the windows on top.
void buildMap(lv_obj_t *card) {
  lv_obj_t *a = mp.area = box(card);
  lv_obj_set_width(a, LV_PCT(100));
  lv_obj_set_flex_grow(a, 1);
  lv_obj_set_style_bg_color(a, hex(kCodeBg), 0);
  lv_obj_set_style_bg_opa(a, LV_OPA_COVER, 0);
  lv_obj_set_style_radius(a, 10, 0);
  lv_obj_set_style_clip_corner(a, true, 0);
  for (int i = 0; i < kMaxMons; i++) {
    lv_obj_t *m = mp.mons[i] = box(a);
    lv_obj_set_style_bg_color(m, hex(kScreenBg), 0);
    lv_obj_set_style_bg_opa(m, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(m, 6, 0);
    lv_obj_set_style_border_color(m, hex(kBorder), 0);
    lv_obj_set_style_border_width(m, 1, 0);
    mp.mon_names[i] = label(m, "", &small_font, kMuted);
    lv_obj_align(mp.mon_names[i], LV_ALIGN_BOTTOM_MID, 0, -4);
    lv_obj_set_hidden(m, true);
  }
  for (int v = 0; v < kMaxZoneViews; v++) {
    lv_obj_t *z = mp.zones[v] = box(a);
    lv_obj_set_style_radius(z, 4, 0);
    lv_obj_set_style_border_color(z, hex(kAccent), 0);
    lv_obj_set_style_border_width(z, 2, 0);
    lv_obj_set_style_border_opa(z, LV_OPA_70, 0);
    lv_obj_set_style_bg_color(z, hex(kAccent), 0);
    lv_obj_set_style_bg_opa(z, LV_OPA_TRANSP, 0);
    lv_obj_set_hidden(z, true);
  }
  for (int i = 0; i < model::Windows::kMax; i++) {
    MapWin &mw = mp.wins[i];
    mw.index = -1;
    mw.root = box(a);
    lv_obj_set_clickable(mw.root, true);
    // A drag here moves the window: not a swipe to another page, nor a scroll.
    lv_obj_set_gesture_bubble(mw.root, false);
    lv_obj_set_scroll_chain_hor(mw.root, false);
    lv_obj_set_scroll_chain_ver(mw.root, false);
    lv_obj_set_style_bg_color(mw.root, hex(kRaised), 0);
    lv_obj_set_style_bg_opa(mw.root, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(mw.root, 5, 0);
    lv_obj_set_style_border_color(mw.root, hex(kBorder), 0);
    lv_obj_set_style_border_width(mw.root, 1, 0);
    lv_obj_set_style_pad_all(mw.root, 4, 0);
    void *user = reinterpret_cast<void *>(intptr_t(i));
    lv_obj_add_event_cb(mw.root, onMapPress, LV_EVENT_PRESSED, user);
    lv_obj_add_event_cb(mw.root, onMapPressing, LV_EVENT_PRESSING, user);
    lv_obj_add_event_cb(mw.root, onMapRelease, LV_EVENT_RELEASED, user);
    lv_obj_add_event_cb(mw.root, onMapLost, LV_EVENT_PRESS_LOST, user);
    mw.icon = lv_image_create(mw.root);
    lv_obj_set_size(mw.icon, icons::kSize, icons::kSize);
    mw.name = label(mw.root, "", &small_font, kText);
    lv_label_set_long_mode(mw.name, LV_LABEL_LONG_MODE_DOTS);
    lv_obj_set_height(mw.name, lv_font_get_line_height(&small_font));  // one line, then "…"
    lv_obj_set_hidden(mw.root, true);
  }
  mp.note = label(a, "", &body_font, kMuted);
  lv_obj_center(mp.note);
  lv_obj_set_hidden(a, true);
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
  lv_obj_t *hint = mp.hint = label(head, "Tap: focus · hold: close", &small_font, kMuted);  // the longer one was cut
  fillRow(hint);
  lv_obj_set_style_text_align(hint, LV_TEXT_ALIGN_RIGHT, 0);
  // The tiles or the desktop map.
  lv_obj_t *toggle = button(head, LV_SIZE_CONTENT, 28, kRaised, onToggle);
  lv_obj_set_style_radius(toggle, 8, 0);
  lv_obj_set_style_pad_hor(toggle, 10, 0);
  mp.toggle_label = label(toggle, "Map", &small_font, kText);
  lv_obj_center(mp.toggle_label);
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
  mp.grid = grid;
  buildMap(c);
}

void buildLayoutButton(lv_obj_t *parent, int i) {
  lv_obj_t *b = button(parent, LV_PCT(100), 32, kRaised, onLayout, reinterpret_cast<void *>(intptr_t(i)));  // a row each, the whole name
  lv_obj_set_style_radius(b, 12, 0);
  lv_obj_set_style_pad_hor(b, 6, 0);
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

// The PC draws each layout's zones in a 40x26 thumbnail, a pixel apart (protocol `zones`);
// the button's is smaller since the rail (28x18), so they're scaled into it. Until 0.31.2
// they were placed as sent, and the box showed only the thumbnail's top-left corner.
constexpr int kCellsW = 40, kCellsH = 26;

// A cell's edge from the PC's thumbnail scaled into the button's, rounded.
int thumbX(int x) { return (x * kThumbW + kCellsW / 2) / kCellsW; }
int thumbY(int y) { return (y * kThumbH + kCellsH / 2) / kCellsH; }

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

  // A list, a layout a row: two to a row beside the rail cut "Columns" to "Colu…".
  lv_obj_t *layouts = box(c);
  lv_obj_set_size(layouts, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(layouts, 5);
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
    lv_obj_t *b = button(w.monitors, 0, 40, kRaised, onMonitor, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_flex_grow(b, 1);
    lv_obj_set_style_radius(b, 10, 0);
    lv_obj_set_style_pad_hor(b, 6, 0);
    // Up to two lines: in the narrower column one cut "DELL P2722H" to "DELL P…".
    w.mon_labels[i] = label(b, "", &small_font, kSoftText);
    lv_obj_set_width(w.mon_labels[i], LV_PCT(100));
    lv_label_set_long_mode(w.mon_labels[i], LV_LABEL_LONG_MODE_WRAP);
    lv_obj_set_style_max_height(w.mon_labels[i], 2 * lv_font_get_line_height(&small_font), 0);
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
      // Scaled by edges, the pixel between cells kept: the next cell starts a pixel past this
      // one's end, so this one ends a pixel before where that one starts once scaled.
      const auto &src = l.cells[c];
      const int x0 = thumbX(src.x), y0 = thumbY(src.y);
      const int x1 = thumbX(src.x + src.w + 1) - 1, y1 = thumbY(src.y + src.h + 1) - 1;
      placeCell(cell, x0, y0, max(x1 - x0, 1), max(y1 - y0, 1));
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

// Places the monitors, their zones and the windows on the map, scaled to fit it.
void layoutMap() {
  const model::Zones &z = model::zones;
  const model::Windows &ws = model::windows;
  const bool online = pc::connected();
  lv_obj_update_layout(mp.area);
  const int aw = lv_obj_get_content_width(mp.area), ah = lv_obj_get_content_height(mp.area);
  const int mons = online ? z.mon_count : 0;
  setText(mp.note, !online ? "PC offline" : mons == 0 ? "No monitors" : "");
  setHidden(mp.note, mons > 0);

  // The monitors' bounds together, on the PC's screens.
  int left = INT32_MAX, top = INT32_MAX, right = INT32_MIN, bottom = INT32_MIN;
  for (int i = 0; i < mons; i++) {
    const model::Monitor &m = z.mons[i];
    left = min(left, m.x);
    top = min(top, m.y);
    right = max(right, m.x + m.w);
    bottom = max(bottom, m.y + m.h);
  }
  const bool placed = mons > 0 && right > left && bottom > top && aw > 2 * kMapPad && ah > 2 * kMapPad;
  if (placed) {
    mp.scale = fminf(float(aw - 2 * kMapPad) / (right - left), float(ah - 2 * kMapPad) / (bottom - top));
    mp.vx = left;
    mp.vy = top;
    mp.ox = (aw - int((right - left) * mp.scale)) / 2;
    mp.oy = (ah - int((bottom - top) * mp.scale)) / 2;
  }

  mp.zone_count = 0;
  for (int i = 0; i < kMaxMons; i++) {
    const bool used = placed && i < mons;
    setHidden(mp.mons[i], !used);
    if (!used) continue;
    const model::Monitor &m = z.mons[i];
    const int mw = int(m.w * mp.scale), mh = int(m.h * mp.scale);
    lv_obj_set_pos(mp.mons[i], mapX(m.x) + 1, mapY(m.y) + 1);  // a pixel apart
    lv_obj_set_size(mp.mons[i], mw - 2, mh - 2);
    setTextf(mp.mon_names[i], "%s%s", m.primary ? LV_SYMBOL_HOME " " : "", m.name.c_str());
    for (int k = 0; k < m.zone_count && mp.zone_count < kMaxZoneViews; k++) {
      const model::Zone &zone = m.zones[k];
      const int v = mp.zone_count++;
      mp.zone_mon[v] = int8_t(i);
      mp.zone_index[v] = int8_t(k);
      lv_obj_set_pos(mp.zones[v], mapX(m.x + zone.x) + 2, mapY(m.y + zone.y) + 2);
      lv_obj_set_size(mp.zones[v], max(int(zone.w * mp.scale) - 4, 6), max(int(zone.h * mp.scale) - 4, 6));
    }
  }
  for (int v = mp.zone_count; v < kMaxZoneViews; v++) setHidden(mp.zones[v], true);

  // The windows, back to front, so the one in front is drawn last.
  int order[model::Windows::kMax], n = 0;
  for (int i = 0; placed && i < ws.count; i++) {
    if (!ws.list[i].minimized && ws.list[i].w > 0 && ws.list[i].h > 0) order[n++] = i;
  }
  for (int a = 1; a < n; a++) {  // by z, the furthest back first
    for (int b = a; b > 0 && ws.list[order[b]].z > ws.list[order[b - 1]].z; b--) {
      const int t = order[b];
      order[b] = order[b - 1];
      order[b - 1] = t;
    }
  }
  for (int s = 0; s < model::Windows::kMax; s++) {
    MapWin &mw = mp.wins[s];
    mw.index = s < n ? order[s] : -1;
    setHidden(mw.root, mw.index < 0);
    if (mw.index < 0) continue;
    const model::Window &win = ws.list[mw.index];
    const int x = mapX(win.x), y = mapY(win.y);
    const int w = max(int(win.w * mp.scale), 12), h = max(int(win.h * mp.scale), 12);
    lv_obj_set_pos(mw.root, x, y);
    lv_obj_set_size(mw.root, w, h);
    lv_obj_move_foreground(mw.root);
    setBgColor(mw.root, win.active ? kSelected : kRaised);
    setBorderColor(mw.root, win.active ? kAccent : kBorder);
    if (lv_obj_get_style_border_width(mw.root, LV_PART_MAIN) != (win.active ? 2 : 1)) lv_obj_set_style_border_width(mw.root, win.active ? 2 : 1, 0);
    // The icon when there's room, the app's name beside it (or alone) when there's more.
    const lv_image_dsc_t *icon = icons::get(win.icon);
    if (!icon && win.icon.length()) requestIcon(win.icon);
    const bool show_icon = icon && w >= icons::kSize + 10 && h >= icons::kSize + 10;
    setHidden(mw.icon, !show_icon);
    if (show_icon && lv_image_get_src(mw.icon) != icon) lv_image_set_src(mw.icon, icon);
    lv_obj_align(mw.icon, LV_ALIGN_TOP_LEFT, 0, 0);
    const int name_x = show_icon ? icons::kSize + 6 : 0;
    const int name_w = w - 10 - name_x;
    const bool show_name = name_w >= 30 && h >= 26;
    setHidden(mw.name, !show_name);
    if (show_name) {
      lv_obj_set_width(mw.name, name_w);
      lv_obj_align(mw.name, LV_ALIGN_TOP_LEFT, name_x, show_icon ? (icons::kSize - lv_font_get_line_height(&small_font)) / 2 : 0);
      setText(mw.name, win.app.c_str());
      setTextColor(mw.name, win.active ? kHighlightText : kText);
    }
  }
  lv_obj_move_foreground(mp.note);
}

void updateMap() {
  const int connected = pc::connected() ? 1 : 0;
  const int32_t size = lv_obj_get_width(mp.area) << 16 | lv_obj_get_height(mp.area);
  const bool waited = relayout_ms && int32_t(millis() - relayout_ms) >= 0;
  if (model::windows.version == mp.windows_seen && model::zones.version == mp.zones_seen && icons::version() == mp.icons_seen &&
      size == mp.size_seen && connected == mp.connected_seen && !waited) {
    return;
  }
  if (drag.slot >= 0 || tile_drag.dragging) return;  // not under the finger; once it's let go
  relayout_ms = 0;
  mp.windows_seen = model::windows.version;
  mp.zones_seen = model::zones.version;
  mp.icons_seen = icons::version();
  mp.size_seen = size;
  mp.connected_seen = connected;
  layoutMap();
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  flexRow(page, kGap);
  lv_obj_set_flex_align(page, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  buildWindows(page);
  buildZones(page);
  buildGhost();
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
  if (mp.on) updateMap();
}

}  // namespace ui::windows
