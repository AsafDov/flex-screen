// The Windows tile (Windows): the 8 most recently used windows as tiles (tap to focus or
// minimize, hold to close, Show desktop, Lock PC), and the desktop map in their place: the
// monitors as arranged with each window drawn where it is, to tap or drag into a zone. A
// tile dragged brings the map up too, with Full screen for each monitor. One of a kind (the
// manager places each kind once), so its state lives here.
#include "fonts/font_icons.h"
#include "icons.h"
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr int kTileW = 143;
constexpr int kTileH = 100;
constexpr uint32_t kCodeBg = 0x0A0C0F;
constexpr uint32_t kScreenBg = 0x14181D;
constexpr uint32_t kIconRetryMs = 3000;

// Initials backgrounds for windows without an icon, from the design canvas.
constexpr uint32_t kTints[] = {0x2D5C8A, 0x3A3F2A, 0x3B3366, 0x1F4466, 0x4A2F66, 0x1E4A3A, 0x4D3B1E, 0x2E333A};

// A window's tile. (Not tiles::Tile: these are buttons inside this one.)
struct WinTile {
  lv_obj_t *root, *icon, *initials, *app, *title;
};

struct {
  WinTile tiles[model::Windows::kMax];
  lv_obj_t *empty;
} w;

uint32_t windows_seen = UINT32_MAX, icons_seen = UINT32_MAX;
int connected_seen = -1;

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
  lv_obj_t *zone_apps[kMaxZoneViews];  // what fills each zone now, named while a tile is dragged
  int8_t zone_mon[kMaxZoneViews], zone_index[kMaxZoneViews];  // bytes: this sits in internal RAM
  int zone_count = 0;
  MapWin wins[model::Windows::kMax];
  // Full screen, one per monitor, above the map while a tile is dragged (not in the Map view:
  // its room there cost the windows their names).
  lv_obj_t *full, *full_tiles[kMaxMons], *full_names[kMaxMons];
  bool full_on = false;
  int full_lit = -1;
  lv_obj_t *preview, *preview_icon, *preview_name;  // where a dragged tile's window would go
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

// A tile dragged: the desktop map comes up in the tiles' place, the zones drawn on it with
// what fills each named (the windows themselves aren't drawn: their names ran into the
// dragged one's), and Full screen for each monitor above it. Where the window would go
// shows as it would be there: the zone under the finger filled with it, or the whole
// monitor, or its own size on a monitor without zones. Letting go puts it there; then the
// tiles come back. No Map press first.
constexpr int kTileDragStart = 16;  // px: as far as the hold ring lets the finger wander (past it there's no hold)
constexpr int kFullH = 42;          // the Full screen tiles: two lines

struct {
  int tile = -1;          // the tile pressed
  bool dragging = false;  // the map is up for it
  bool dragged = false;   // this press became a drag: its tap and hold don't count (until the next press)
  model::Window win;      // a copy: the list may change under the finger
  bool has_icon = false;
  lv_point_t start;
} tile_drag;


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

// The Full screen tile under a point on the deck's screen, -1 for none.
int fullAt(const lv_point_t &p) {
  if (lv_obj_is_hidden(mp.full)) return -1;
  for (int i = 0; i < kMaxMons; i++) {
    if (lv_obj_is_hidden(mp.full_tiles[i])) continue;
    lv_area_t a;
    lv_obj_get_coords(mp.full_tiles[i], &a);
    if (p.x >= a.x1 && p.x <= a.x2 && p.y >= a.y1 && p.y <= a.y2) return i;
  }
  return -1;
}

void lightFull(int i) {
  if (i == mp.full_lit) return;
  if (mp.full_lit >= 0) {
    setBgColor(mp.full_tiles[mp.full_lit], kRaised);
    lv_obj_set_style_border_width(mp.full_tiles[mp.full_lit], 0, 0);
  }
  if (i >= 0) {
    setBgColor(mp.full_tiles[i], kSelected);
    lv_obj_set_style_border_width(mp.full_tiles[i], 2, 0);
  }
  mp.full_lit = i;
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
  if (!on) setHidden(mp.full, true);
  setText(mp.toggle_label, on ? "Tiles" : "Map");
  setText(mp.hint, on ? "Drag: move" : "Hold: close");  // tapping focuses, as anyone would try first
  mp.windows_seen = UINT32_MAX;
}

void onToggle(lv_event_t *) {
  if (drag.slot >= 0) endDrag();
  showMap(!mp.on);
}

// ---- a tile dragged onto the map

void updateMap();

// The map is placed (and the PC's screens can be read off it).
bool mapPlaced() { return mp.scale > 0 && pc::connected() && model::zones.mon_count > 0; }

// Where a window let go at a point on the deck's screen would go.
struct Target {
  enum Kind : uint8_t { kNone, kZone, kFull, kMove };
  Kind kind = kNone;
  int mon = -1, view = -1;         // its monitor; for a zone, the zone view
  int x = 0, y = 0, w = 0, h = 0;  // the box it would fill, on the PC's screens
};

Target targetAt(const model::Window &win, const lv_point_t &p) {
  Target t;
  if (!mapPlaced()) return t;
  const int full = fullAt(p);
  if (full >= 0) {
    const model::Monitor &m = model::zones.mons[full];
    t.kind = Target::kFull;
    t.mon = full;
    t.x = m.x, t.y = m.y, t.w = m.w, t.h = m.h;
    return t;
  }
  const lv_point_t at = fingerOnScreens(p);
  const int mon = monitorAt(at.x, at.y);
  if (mon < 0) return t;
  const model::Monitor &m = model::zones.mons[mon];
  t.mon = mon;
  t.view = zoneViewAt(at.x, at.y);
  if (t.view >= 0) {
    const model::Zone &z = m.zones[mp.zone_index[t.view]];
    t.kind = Target::kZone;
    t.x = m.x + z.x, t.y = m.y + z.y, t.w = z.w, t.h = z.h;
    return t;
  }
  // Its own size, its middle under the finger, inside the monitor (as the PC keeps it); a
  // minimized window's size isn't known here, so a third of the monitor stands in.
  const bool sized = !win.minimized && win.w > 0 && win.h > 0;
  t.kind = Target::kMove;
  t.w = min(sized ? win.w : m.w / 3, m.w);
  t.h = min(sized ? win.h : m.h / 3, m.h);
  t.x = max(m.x, min(int(at.x) - t.w / 2, m.x + m.w - t.w));
  t.y = max(m.y, min(int(at.y) - t.h / 2, m.y + m.h - t.h));
  return t;
}

// The window drawn where it would go, over what's there now; nothing off the monitors.
void showPreview(const Target &t) {
  lightFull(t.kind == Target::kFull ? t.mon : -1);
  if (t.kind == Target::kNone) {
    setHidden(mp.preview, true);
    return;
  }
  lv_obj_t *pv = mp.preview;
  const int x = mapX(t.x) + 2, y = mapY(t.y) + 2;
  const int pw = max(int(t.w * mp.scale) - 4, 8), ph = max(int(t.h * mp.scale) - 4, 8);
  if (lv_obj_get_style_x(pv, LV_PART_MAIN) != x || lv_obj_get_style_y(pv, LV_PART_MAIN) != y) lv_obj_set_pos(pv, x, y);
  if (lv_obj_get_style_width(pv, LV_PART_MAIN) != pw || lv_obj_get_style_height(pv, LV_PART_MAIN) != ph) {
    lv_obj_set_size(pv, pw, ph);
    const int line = lv_font_get_line_height(&body_font);
    setHidden(mp.preview_icon, !tile_drag.has_icon || ph < icons::kSize + line + 14 || pw < icons::kSize + 8);
    setHidden(mp.preview_name, ph < line + 8 || pw < 30);
    lv_obj_set_width(mp.preview_name, max(pw - 12, 1));
  }
  setHidden(pv, false);
}

// The map comes up in the tiles' place: the zones, what fills them, Full screen above.
void startTileDrag(const model::Window &win, const lv_point_t &p) {
  tile_drag.dragged = true;
  tile_drag.win = win;
  if (drag.slot >= 0) endDrag();
  mp.full_on = true;
  showMap(true);
  setTextf(mp.hint, "Moving %s", win.app.c_str());
  updateMap();  // placed now, not at the next update: the zones are needed under the finger at once
  tile_drag.dragging = true;  // from here the map holds still until it's let go
  for (MapWin &mw : mp.wins) setHidden(mw.root, true);  // drawn again when the map next shows
  showZones(true);
  const int line = lv_font_get_line_height(&small_font);
  for (int v = 0; v < mp.zone_count; v++) {
    const bool named = lv_label_get_text(mp.zone_apps[v])[0] && lv_obj_get_style_height(mp.zones[v], LV_PART_MAIN) >= line + 6;
    setHidden(mp.zone_apps[v], !named);
  }
  const lv_image_dsc_t *icon = icons::get(win.icon);
  tile_drag.has_icon = icon != nullptr;
  if (icon && lv_image_get_src(mp.preview_icon) != icon) lv_image_set_src(mp.preview_icon, icon);
  setText(mp.preview_name, win.app.c_str());
  lv_obj_set_size(mp.preview, 1, 1);  // sized afresh for this window
  lv_obj_move_foreground(mp.preview);
  showPreview(targetAt(win, p));
}

// Puts the dragged tile's window where it was let go. In the zone it fills already, or off
// the monitors, nothing.
void dropTile(const lv_point_t &p) {
  const model::Window &win = tile_drag.win;
  const Target t = targetAt(win, p);
  switch (t.kind) {
    case Target::kFull:
      pc::maximizeWindow(win.id.c_str(), t.mon);
      break;
    case Target::kZone:
      if (!win.minimized && overlap(win.x, win.y, win.w, win.h, t.x, t.y, t.w, t.h) > 0.9f) break;
      pc::snapWindowTo(win.id.c_str(), t.mon, mp.zone_index[t.view]);
      break;
    case Target::kMove:
      pc::moveWindow(win.id.c_str(), t.mon, t.x - model::zones.mons[t.mon].x, t.y - model::zones.mons[t.mon].y);
      break;
    case Target::kNone:
      break;
  }
}

void endTileDrag() {
  tile_drag.tile = -1;
  if (!tile_drag.dragging) return;
  tile_drag.dragging = false;
  setHidden(mp.preview, true);
  lightFull(-1);
  for (int v = 0; v < kMaxZoneViews; v++) setHidden(mp.zone_apps[v], true);
  showZones(false);
  mp.full_on = false;
  showMap(false);  // laid out afresh when it next shows, the windows drawn again, no Full screen
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
  showPreview(targetAt(tile_drag.win, p));
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

// ---- building

void buildTile(lv_obj_t *grid, int i) {
  WinTile &t = w.tiles[i];
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
  // Full screen on each monitor, above the map while a tile is dragged: the window let go
  // on one is maximized there. Two lines, "Full screen" over the monitor's name: on one,
  // "Full screen · DELL P2722H" was cut.
  lv_obj_t *row = mp.full = box(card);
  lv_obj_set_size(row, LV_PCT(100), kFullH);
  flexRow(row, 8);
  const int line = lv_font_get_line_height(&small_font);
  for (int i = 0; i < kMaxMons; i++) {
    lv_obj_t *b = mp.full_tiles[i] = box(row);
    lv_obj_set_size(b, 0, kFullH);
    lv_obj_set_style_bg_color(b, hex(kRaised), 0);
    lv_obj_set_style_bg_opa(b, LV_OPA_COVER, 0);
    lv_obj_set_flex_grow(b, 1);
    lv_obj_set_style_radius(b, 10, 0);
    lv_obj_set_style_pad_hor(b, 10, 0);
    lv_obj_set_style_border_color(b, hex(kAccent), 0);
    flexRow(b, 6);
    lv_obj_set_flex_align(b, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    label(b, glyphOf("expand"), &font_icons_15, kText);
    lv_obj_t *text = box(b);
    lv_obj_set_size(text, 1, 2 * line);
    lv_obj_set_flex_grow(text, 1);
    flexCol(text, 0);
    label(text, "Full screen", &small_font, kText);
    lv_obj_t *name = mp.full_names[i] = label(text, "", &small_font, kMuted);
    lv_obj_set_size(name, LV_PCT(100), line);
    lv_label_set_long_mode(name, LV_LABEL_LONG_MODE_DOTS);
  }
  lv_obj_set_hidden(row, true);

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
    lv_obj_t *app = mp.zone_apps[v] = label(z, "", &small_font, kMuted);
    lv_label_set_long_mode(app, LV_LABEL_LONG_MODE_DOTS);
    lv_obj_set_height(app, lv_font_get_line_height(&small_font));
    lv_obj_set_style_text_align(app, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_center(app);
    lv_obj_set_hidden(app, true);
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
  // Where a dragged tile's window would go, drawn as it would be there.
  lv_obj_t *pv = mp.preview = box(a);
  lv_obj_set_style_bg_color(pv, hex(kSelected), 0);
  lv_obj_set_style_bg_opa(pv, LV_OPA_COVER, 0);
  lv_obj_set_style_border_color(pv, hex(kAccent), 0);
  lv_obj_set_style_border_width(pv, 2, 0);
  lv_obj_set_style_radius(pv, 4, 0);
  lv_obj_set_style_pad_all(pv, 4, 0);
  flexCol(pv, 2);
  lv_obj_set_flex_align(pv, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  mp.preview_icon = lv_image_create(pv);
  lv_obj_set_size(mp.preview_icon, icons::kSize, icons::kSize);
  mp.preview_name = label(pv, "", &body_font, kHighlightText);
  lv_label_set_long_mode(mp.preview_name, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_height(mp.preview_name, lv_font_get_line_height(&body_font));
  lv_obj_set_style_text_align(mp.preview_name, LV_TEXT_ALIGN_CENTER, 0);
  lv_obj_set_hidden(pv, true);
  lv_obj_set_hidden(a, true);
}

void buildWindows(lv_obj_t *c) {
  flexCol(c, 8);
  // Snug, so the hint has room for "Moving Terminal" in the tile's 449 px.
  lv_obj_t *head = box(c);
  lv_obj_set_size(head, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(head, 6);
  label(head, "OPEN WINDOWS", &small_font, kMuted);
  // "Tap: focus · hold: close" was cut to "hold:…" beside Map and Lock PC.
  lv_obj_t *hint = mp.hint = label(head, "Hold: close", &small_font, kMuted);
  fillRow(hint);
  lv_obj_set_style_text_align(hint, LV_TEXT_ALIGN_RIGHT, 0);
  // The tiles or the desktop map.
  lv_obj_t *toggle = button(head, LV_SIZE_CONTENT, 28, kRaised, onToggle);
  lv_obj_set_style_radius(toggle, 8, 0);
  lv_obj_set_style_pad_hor(toggle, 8, 0);
  mp.toggle_label = label(toggle, "Map", &small_font, kText);
  lv_obj_center(mp.toggle_label);
  lv_obj_t *lock = button(head, LV_SIZE_CONTENT, 28, kRaised, onLock);
  lv_obj_set_style_radius(lock, 8, 0);
  lv_obj_set_style_pad_hor(lock, 8, 0);
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
    WinTile &t = w.tiles[i];
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

// Places the monitors, their zones and the windows on the map, scaled to fit it.
void layoutMap() {
  const model::Zones &z = model::zones;
  const model::Windows &ws = model::windows;
  const bool online = pc::connected();
  const int mons = online ? z.mon_count : 0;
  // Full screen for each monitor first (while a tile is dragged): it takes the map's room.
  setHidden(mp.full, !mp.full_on || mons == 0);
  for (int i = 0; i < kMaxMons; i++) {
    setHidden(mp.full_tiles[i], i >= mons);
    if (i < mons) setText(mp.full_names[i], z.mons[i].name.c_str());
  }
  lv_obj_update_layout(mp.area);
  const int aw = lv_obj_get_content_width(mp.area), ah = lv_obj_get_content_height(mp.area);
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
      const int zw = max(int(zone.w * mp.scale) - 4, 6), zh = max(int(zone.h * mp.scale) - 4, 6);
      lv_obj_set_size(mp.zones[v], zw, zh);
      setText(mp.zone_apps[v], zone.app.c_str());
      lv_obj_set_width(mp.zone_apps[v], max(zw - 10, 1));
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

class Windows : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildWindows(c); }
  void update() override {
    const int connected = pc::connected() ? 1 : 0;
    if (model::windows.version != windows_seen || icons::version() != icons_seen || connected != connected_seen) {
      windows_seen = model::windows.version;
      icons_seen = icons::version();
      connected_seen = connected;
      updateTiles();
    }
    if (mp.on) updateMap();
  }
};

}  // namespace

extern const Kind kWindows = {"windows", {9, 12}, {}, []() -> Tile * { return new Windows(); }};

}  // namespace ui::tiles
