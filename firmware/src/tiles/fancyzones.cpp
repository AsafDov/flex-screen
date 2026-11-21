// The FancyZones tile (Windows): the shown monitor's layout to pick (your own layouts first,
// then the built-in ones, each with a thumbnail of its zones), a map of its zones to snap the
// window in front into, and a button per monitor when there's more than one. One of a kind
// (the manager places each kind once), so its state lives here.
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr int kThumbW = 28;
constexpr int kThumbH = 18;
constexpr uint32_t kCodeBg = 0x0A0C0F;
constexpr uint32_t kScreenBg = 0x14181D;
constexpr uint32_t kThumbOff = 0x4A525C;
constexpr uint32_t kPickHoldMs = 20000;  // a monitor picked by hand stays shown this long

// Layout buttons come from the PC: the user's custom FancyZones layouts first, then
// built-in ones, each with its zones for a thumbnail.
constexpr int kLayoutCount = model::Zones::kMaxLayouts;
constexpr int kCellCount = model::LayoutChoice::kMaxCells;

struct {
  lv_obj_t *layout_buttons[kLayoutCount], *layout_labels[kLayoutCount];
  lv_obj_t *layout_cells[kLayoutCount][kCellCount];
  lv_obj_t *snap_title, *map, *screen;
  lv_obj_t *zones[model::Monitor::kMaxZones], *zone_apps[model::Monitor::kMaxZones];
  int zone_count;
  lv_obj_t *monitors, *mon_buttons[model::Zones::kMax], *mon_labels[model::Zones::kMax];
} w;

uint32_t zones_seen = UINT32_MAX;
int shown_seen = -2;
int32_t map_size_seen = -1;
uint32_t geometry_seen = UINT32_MAX;  // 0 is "no monitor": starting there left the empty map's placeholder showing
int picked = -1;
uint32_t picked_ms = 0;

// The monitor the zone map shows: one picked by hand recently, else the active window's.
int shownMonitor() {
  const model::Zones &z = model::zones;
  if (z.mon_count == 0) return -1;
  if (picked >= 0 && picked < z.mon_count && millis() - picked_ms < kPickHoldMs) return picked;
  return z.active >= 0 && z.active < z.mon_count ? z.active : 0;
}

// ---- actions

int indexOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

void refresh();

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
  refresh();
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

void buildZones(lv_obj_t *c) {
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

void refresh() {
  const int shown = pc::connected() ? shownMonitor() : -1;
  const int32_t map_size = lv_obj_get_width(w.map) << 16 | lv_obj_get_height(w.map);  // the context bar changes it
  if (model::zones.version == zones_seen && shown == shown_seen && map_size == map_size_seen) return;
  zones_seen = model::zones.version;
  shown_seen = shown;
  map_size_seen = map_size;
  updateZones();
}

class FancyZones : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildZones(c); }
  void update() override { refresh(); }
};

}  // namespace

extern const Kind kFancyZones = {"fancyzones", {5, 12}, {}, []() -> Tile * { return new FancyZones(); }};

}  // namespace ui::tiles
