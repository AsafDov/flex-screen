#include "ui_kit.h"

#include "fonts/font_icons.h"

#include <stdarg.h>

#include "esp_heap_caps.h"
#include "model.h"

extern "C" {
LV_FONT_DECLARE(font_extra_14)
LV_FONT_DECLARE(font_extra_16)
LV_FONT_DECLARE(font_extra_20)
}

namespace ui {

lv_font_t title_font;
lv_font_t body_font;
lv_font_t small_font;

uint32_t kBg, kSurface, kBorder, kRaised, kSelected, kText, kSoftText, kMuted, kTrack;
uint32_t kAccent, kOnAccent, kGreen, kAmber, kAmberBg, kArtBg;
uint32_t kHighlight, kHighlightLine, kHighlightText;

void usePalette(const char *name) {
  if (!strcmp(name, "vibrant")) {
    kBg = 0x0A0D1A, kSurface = 0x121931, kBorder = 0x26305A, kRaised = 0x1A2445, kSelected = 0x223A78;
    kText = 0xF2F5FF, kSoftText = 0xCAD3F5, kMuted = 0x97A3CF, kTrack = 0x2A3866;
    kAccent = 0x4C8DFF, kOnAccent = 0x050F2E, kGreen = 0x2BE3A0, kAmber = 0xFFB020, kAmberBg = 0x45300A;
    kArtBg = 0x2B3C80;
    kHighlight = 0x13202D, kHighlightLine = 0x24405C, kHighlightText = 0xA9B6C4;
    return;
  }
  // Warm charcoal with an orange accent.
  if (!strcmp(name, "ember")) {
    kBg = 0x110D0B, kSurface = 0x1B1512, kBorder = 0x33261F, kRaised = 0x251C17, kSelected = 0x3A2618;
    kText = 0xF6EEE8, kSoftText = 0xD9C9BD, kMuted = 0xA8968A, kTrack = 0x3A2D25;
    kAccent = 0xFF8A3D, kOnAccent = 0x2A1205, kGreen = 0x5FD49A, kAmber = 0xFFC247, kAmberBg = 0x45320F;
    kArtBg = 0x3D2A1F;
    kHighlight = 0x2A1A10, kHighlightLine = 0x5A3418, kHighlightText = 0xD6B8A2;
    return;
  }
  // Deep indigo with a violet accent.
  if (!strcmp(name, "violet")) {
    kBg = 0x0E0B16, kSurface = 0x171226, kBorder = 0x2C2347, kRaised = 0x201936, kSelected = 0x33275E;
    kText = 0xF1EDFF, kSoftText = 0xCFC6F0, kMuted = 0x9D93C4, kTrack = 0x2F2650;
    kAccent = 0xA78BFA, kOnAccent = 0x1A0F3A, kGreen = 0x4ADE9E, kAmber = 0xFBBF24, kAmberBg = 0x45340A;
    kArtBg = 0x342A63;
    kHighlight = 0x1E1638, kHighlightLine = 0x4A3A80, kHighlightText = 0xB9AEE0;
    return;
  }
  // Dusky plum-grey with pastel colors: pink accent, mint, butter yellow, lavender Up next.
  if (!strcmp(name, "pastel")) {
    kBg = 0x15131A, kSurface = 0x1E1B26, kBorder = 0x342E40, kRaised = 0x292434, kSelected = 0x3D2A3C;
    kText = 0xF6F0F8, kSoftText = 0xDAD0E0, kMuted = 0xA79DB3, kTrack = 0x3A3346;
    kAccent = 0xF5A9C8, kOnAccent = 0x3A1426, kGreen = 0x9FE3C4, kAmber = 0xF7D58B, kAmberBg = 0x3D3320;
    kArtBg = 0x3B3356;
    kHighlight = 0x231E34, kHighlightLine = 0x4B4070, kHighlightText = 0xCABFEB;
    return;
  }
  kBg = 0x0D0F12, kSurface = 0x16191E, kBorder = 0x232830, kRaised = 0x1F242B, kSelected = 0x1F2A36;
  kText = 0xECEEF1, kSoftText = 0xC4CBD3, kMuted = 0x9BA4AF, kTrack = 0x2A3038;
  kAccent = 0x5AB4FF, kOnAccent = 0x0B1520, kGreen = 0x4FD1A5, kAmber = 0xF5A623, kAmberBg = 0x3A2A10;
  kArtBg = 0x263445;
  kHighlight = 0x13202D, kHighlightLine = 0x24405C, kHighlightText = 0xA9B6C4;
}

namespace {

// Two descriptors so the one on screen stays valid while the next one is set up. LVGL's
// image and header caches are off in lv_conf.h, so once no lv_image points at a
// descriptor its pixels can be freed.
struct ArtSlot {
  lv_image_dsc_t dsc = {};
  String id;
  uint8_t *pixels = nullptr;
};
ArtSlot slots[2];
int current_slot = -1;
int old_slot = -1;

}  // namespace

void initFonts() {
  // Montserrat -> the characters it lacks (src/fonts/font_extra.c) -> DejaVu.
  title_font = lv_font_montserrat_20;
  title_font.fallback = &font_extra_20;
  body_font = lv_font_montserrat_16;
  body_font.fallback = &font_extra_16;
  small_font = lv_font_montserrat_14;
  small_font.fallback = &font_extra_14;
}

lv_color_t hex(uint32_t c) { return lv_color_hex(c); }

lv_obj_t *box(lv_obj_t *parent) {
  lv_obj_t *o = lv_obj_create(parent);
  lv_obj_remove_style_all(o);
  lv_obj_set_scrollable(o, false);
  // LVGL objects take presses by default, so a box inside a button swallowed every tap
  // that landed on it; the button only heard taps on its padding.
  lv_obj_set_clickable(o, false);
  return o;
}

lv_obj_t *card(lv_obj_t *parent, uint32_t bg, uint32_t border) {
  lv_obj_t *o = box(parent);
  lv_obj_set_style_bg_color(o, hex(bg), 0);
  lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
  lv_obj_set_style_border_color(o, hex(border), 0);
  lv_obj_set_style_border_width(o, 1, 0);
  lv_obj_set_style_radius(o, 12, 0);
  lv_obj_set_style_pad_all(o, 12, 0);
  return o;
}

lv_obj_t *label(lv_obj_t *parent, const char *text, const lv_font_t *font, uint32_t color) {
  lv_obj_t *l = lv_label_create(parent);
  lv_label_set_text(l, text);
  lv_obj_set_style_text_font(l, font, 0);
  lv_obj_set_style_text_color(l, hex(color), 0);
  return l;
}

void flexCol(lv_obj_t *o, int gap) {
  lv_obj_set_flex_flow(o, LV_FLEX_FLOW_COLUMN);
  lv_obj_set_style_pad_row(o, gap, 0);
}

void flexRow(lv_obj_t *o, int gap) {
  lv_obj_set_flex_flow(o, LV_FLEX_FLOW_ROW);
  lv_obj_set_flex_align(o, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_set_style_pad_column(o, gap, 0);
}

lv_obj_t *pill(lv_obj_t *parent) {
  lv_obj_t *p = card(parent, kSurface, 0x262C34);
  lv_obj_set_size(p, LV_SIZE_CONTENT, 28);
  lv_obj_set_style_radius(p, 14, 0);
  lv_obj_set_style_pad_hor(p, 10, 0);
  lv_obj_set_style_pad_ver(p, 0, 0);
  flexRow(p, 6);
  return p;
}

lv_obj_t *button(lv_obj_t *parent, int w, int h, uint32_t bg, lv_event_cb_t cb, void *user) {
  lv_obj_t *b = lv_button_create(parent);
  lv_obj_remove_style_all(b);
  lv_obj_set_size(b, w, h);
  lv_obj_set_style_bg_color(b, hex(bg), 0);
  lv_obj_set_style_bg_opa(b, bg == kSurface ? LV_OPA_TRANSP : LV_OPA_COVER, 0);
  lv_obj_set_style_bg_color(b, hex(kSelected), LV_STATE_PRESSED);
  lv_obj_set_style_bg_opa(b, LV_OPA_COVER, LV_STATE_PRESSED);
  lv_obj_set_style_radius(b, LV_RADIUS_CIRCLE, 0);
  if (cb) lv_obj_add_event_cb(b, cb, LV_EVENT_CLICKED, user);
  return b;
}

lv_obj_t *iconButton(lv_obj_t *parent, int w, int h, uint32_t bg, const char *symbol, uint32_t color,
                     lv_event_cb_t cb, void *user) {
  lv_obj_t *b = button(parent, w, h, bg, cb, user);
  lv_obj_t *l = label(b, symbol, &lv_font_montserrat_20, color);
  lv_obj_center(l);
  return l;
}

void ellipsize(lv_obj_t *l) {
  lv_obj_set_width(l, LV_PCT(100));
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
  // Dots only kick in once the height is fixed; until then a long text wraps instead.
  lv_obj_set_height(l, lv_font_get_line_height(lv_obj_get_style_text_font(l, LV_PART_MAIN)));
}

void setText(lv_obj_t *l, const char *text) {
  if (strcmp(lv_label_get_text(l), text) != 0) lv_label_set_text(l, text);
}

const char *glyphOf(const char *name) {
  for (int i = 0; i < font_icon_count; i++) {
    if (!strcmp(name, font_icon_names[i].name)) return font_icon_names[i].text;
  }
  return "";
}

void fillRow(lv_obj_t *l) {
  lv_obj_set_width(l, 1);  // any fixed width: the row's flex layout gives it the rest
  lv_obj_set_flex_grow(l, 1);
  // Only a label has a long mode: setting one on a box wrote past the box's memory, and the
  // simulator crashed now and then on start-up, deep in LVGL (2026-11-09).
  if (!lv_obj_check_type(l, &lv_label_class)) return;
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_height(l, lv_font_get_line_height(lv_obj_get_style_text_font(l, LV_PART_MAIN)));
}

// As wide as the text up to `max`, on one line, "…" past it. The limit is a max-width
// style rather than a width set after measuring: a label capped that way kept its old
// width when only the limit changed (the status bar shortens the meeting to fit), and with
// its height left free a long text wrapped instead of ending in "…" (the git branch).
void setTextUpTo(lv_obj_t *l, const char *text, int32_t max) {
  if (lv_obj_get_style_max_width(l, LV_PART_MAIN) != max) {
    lv_obj_set_width(l, LV_SIZE_CONTENT);
    lv_obj_set_style_max_width(l, max, 0);
    lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
    lv_obj_set_height(l, lv_font_get_line_height(lv_obj_get_style_text_font(l, LV_PART_MAIN)));
  }
  setText(l, text);
}

void setTextf(lv_obj_t *l, const char *fmt, ...) {
  char text[256];
  va_list args;
  va_start(args, fmt);
  vsnprintf(text, sizeof(text), fmt, args);
  va_end(args);
  setText(l, text);
}

void setTextColor(lv_obj_t *o, uint32_t color) {
  if (!lv_color_eq(lv_obj_get_style_text_color(o, LV_PART_MAIN), hex(color))) lv_obj_set_style_text_color(o, hex(color), 0);
}

void setBgColor(lv_obj_t *o, uint32_t color) {
  if (!lv_color_eq(lv_obj_get_style_bg_color(o, LV_PART_MAIN), hex(color))) lv_obj_set_style_bg_color(o, hex(color), 0);
}

void setBorderColor(lv_obj_t *o, uint32_t color) {
  if (!lv_color_eq(lv_obj_get_style_border_color(o, LV_PART_MAIN), hex(color))) {
    lv_obj_set_style_border_color(o, hex(color), 0);
  }
}

void setHidden(lv_obj_t *o, bool hidden) {
  if (lv_obj_is_hidden(o) != hidden) lv_obj_set_hidden(o, hidden);
}

uint32_t mediaAccent(bool connected) {
  const model::Media &m = model::media;
  return connected && m.present && m.accent ? m.accent : kAccent;
}

uint32_t mixColor(uint32_t color, uint32_t base, uint8_t amount) {
  return lv_color_to_u32(lv_color_mix(hex(color), hex(base), amount)) & 0xFFFFFF;
}

void tintMediaCard(lv_obj_t *card, uint32_t accent) {
  const bool theme = accent == kAccent;  // no cover color: the plain card
  setBgColor(card, theme ? kSurface : mixColor(accent, kSurface, 26));
  setBorderColor(card, theme ? kBorder : mixColor(accent, kBorder, 90));
}

namespace {

constexpr int kHoldRingD = 92, kHoldRingW = 6;  // round a fingertip, so it shows past it
constexpr uint32_t kHoldRingDelayMs = 100;       // a tap is over before it shows
constexpr int kHoldSlop = 16;                    // px the finger may wander: more is a drag
lv_obj_t *hold_ring = nullptr;
lv_point_t hold_from;

void holdRingSet(void *, int32_t v) { lv_arc_set_value(hold_ring, v); }
void holdRingShow(lv_anim_t *) { lv_obj_set_hidden(hold_ring, false); }

void holdRingStop() {
  if (!hold_ring) return;
  lv_anim_delete(hold_ring, holdRingSet);
  setHidden(hold_ring, true);
}

void makeHoldRing() {
  hold_ring = lv_arc_create(lv_layer_top());
  lv_obj_remove_style_all(hold_ring);
  lv_obj_set_clickable(hold_ring, false);
  lv_obj_set_size(hold_ring, kHoldRingD, kHoldRingD);
  lv_arc_set_rotation(hold_ring, 270);  // from the top, clockwise
  lv_arc_set_bg_angles(hold_ring, 0, 360);
  lv_arc_set_range(hold_ring, 0, 100);
  lv_obj_set_style_arc_width(hold_ring, kHoldRingW, LV_PART_MAIN);
  lv_obj_set_style_arc_color(hold_ring, hex(0xFFFFFF), LV_PART_MAIN);
  lv_obj_set_style_arc_opa(hold_ring, LV_OPA_20, LV_PART_MAIN);
  lv_obj_set_style_arc_width(hold_ring, kHoldRingW, LV_PART_INDICATOR);
  lv_obj_set_style_arc_rounded(hold_ring, true, LV_PART_INDICATOR);
  lv_obj_set_hidden(hold_ring, true);
}

void onHoldRing(lv_event_t *e) {
  const lv_event_code_t code = lv_event_get_code(e);
  lv_indev_t *indev = lv_indev_active();
  if (code == LV_EVENT_PRESSED) {
    if (!indev) return;
    if (!hold_ring) makeHoldRing();
    lv_indev_get_point(indev, &hold_from);
    const uint32_t color = uint32_t(uintptr_t(lv_event_get_user_data(e)));
    lv_obj_set_style_arc_color(hold_ring, hex(color ? color : kAccent), LV_PART_INDICATOR);
    lv_obj_set_pos(hold_ring, hold_from.x - kHoldRingD / 2, hold_from.y - kHoldRingD / 2);
    lv_anim_delete(hold_ring, holdRingSet);
    lv_arc_set_value(hold_ring, 0);
    lv_obj_set_hidden(hold_ring, true);
    lv_obj_move_foreground(hold_ring);  // over whatever opened on the top layer since
    // Full when LVGL's long press fires, which is when the hold takes effect.
    lv_anim_t a;
    lv_anim_init(&a);
    lv_anim_set_var(&a, hold_ring);
    lv_anim_set_exec_cb(&a, holdRingSet);
    lv_anim_set_values(&a, 0, 100);
    lv_anim_set_delay(&a, kHoldRingDelayMs);
    lv_anim_set_duration(&a, LV_INDEV_DEF_LONG_PRESS_TIME - kHoldRingDelayMs);
    lv_anim_set_start_cb(&a, holdRingShow);
    lv_anim_start(&a);
  } else if (code == LV_EVENT_PRESSING) {
    if (!indev || !hold_ring) return;
    lv_point_t p;
    lv_indev_get_point(indev, &p);
    if (abs(p.x - hold_from.x) > kHoldSlop || abs(p.y - hold_from.y) > kHoldSlop) holdRingStop();
  } else {
    holdRingStop();  // the long press fired, or the finger lifted or slid off
  }
}

}  // namespace

void holdRing(lv_obj_t *obj, uint32_t color) {
  void *user = reinterpret_cast<void *>(uintptr_t(color));
  for (lv_event_code_t code : {LV_EVENT_PRESSED, LV_EVENT_PRESSING, LV_EVENT_LONG_PRESSED, LV_EVENT_RELEASED, LV_EVENT_PRESS_LOST}) {
    lv_obj_add_event_cb(obj, onHoldRing, code, user);
  }
}

bool holdRingShown() { return hold_ring && !lv_obj_is_hidden(hold_ring); }

void formatTime(char *buf, size_t n, float seconds) {
  const int s = seconds > 0 ? int(seconds) : 0;
  if (s >= 3600) {
    snprintf(buf, n, "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60);
  } else {
    snprintf(buf, n, "%d:%02d", s / 60, s % 60);
  }
}

void formatAgo(char *buf, size_t n, int32_t secs) {
  if (secs < 0) snprintf(buf, n, "%s", "");
  else if (secs < 10) snprintf(buf, n, "just now");
  else if (secs < 60) snprintf(buf, n, "%d s ago", int(secs));
  else if (secs < 3600) snprintf(buf, n, "%d min ago", int(secs / 60));
  else if (secs < 86400) snprintf(buf, n, "%d h ago", int(secs / 3600));
  else snprintf(buf, n, "%d d ago", int(secs / 86400));
}

void formatDuration(char *buf, size_t n, uint32_t ms) {
  if (ms < 60000) snprintf(buf, n, "%.1f s", ms / 1000.0f);
  else snprintf(buf, n, "%u min %u s", unsigned(ms / 60000), unsigned(ms / 1000 % 60));
}

const lv_image_dsc_t *artFor(const String &id) {
  if (current_slot < 0 || id.length() == 0 || slots[current_slot].id != id) return nullptr;
  return &slots[current_slot].dsc;
}

bool installPendingArt() {
  model::Art &art = model::art;
  if (!art.pending) return false;
  const int next = current_slot == 0 ? 1 : 0;
  ArtSlot &slot = slots[next];
  if (slot.pixels) {  // an old image nobody released; never on screen now
    heap_caps_free(slot.pixels);
  }
  slot.pixels = art.pending;
  slot.id = art.pending_id;
  slot.dsc = {};
  slot.dsc.header.magic = LV_IMAGE_HEADER_MAGIC;
  slot.dsc.header.cf = LV_COLOR_FORMAT_RGB565;
  slot.dsc.header.w = art.w;
  slot.dsc.header.h = art.h;
  slot.dsc.header.stride = art.w * 2;
  slot.dsc.data_size = uint32_t(art.w) * art.h * 2;
  slot.dsc.data = slot.pixels;
  art.pending = nullptr;
  old_slot = current_slot;
  current_slot = next;
  return true;
}

void releaseOldArt() {
  if (old_slot < 0) return;
  ArtSlot &slot = slots[old_slot];
  heap_caps_free(slot.pixels);
  slot.pixels = nullptr;
  slot.id = "";
  old_slot = -1;
}

MicIcon micIcon(lv_obj_t *parent) {
  static const lv_point_precise_t slash_points[] = {{1, 1}, {17, 19}};
  MicIcon m;
  m.root = box(parent);
  lv_obj_set_size(m.root, 18, 20);

  m.capsule = box(m.root);
  lv_obj_set_size(m.capsule, 8, 12);
  lv_obj_set_pos(m.capsule, 5, 0);
  lv_obj_set_style_radius(m.capsule, 4, 0);
  lv_obj_set_style_border_width(m.capsule, 2, 0);

  m.arc = lv_arc_create(m.root);
  lv_obj_remove_style(m.arc, nullptr, LV_PART_KNOB);
  lv_obj_set_clickable(m.arc, false);
  lv_obj_set_size(m.arc, 14, 14);
  lv_obj_set_pos(m.arc, 2, 3);
  lv_obj_set_style_pad_all(m.arc, 0, 0);
  lv_arc_set_bg_angles(m.arc, 0, 180);  // the cup under the capsule
  lv_obj_set_style_arc_width(m.arc, 2, LV_PART_MAIN);
  lv_obj_set_style_arc_opa(m.arc, LV_OPA_TRANSP, LV_PART_INDICATOR);

  m.stem = box(m.root);
  lv_obj_set_size(m.stem, 2, 4);
  lv_obj_set_pos(m.stem, 8, 16);
  lv_obj_set_style_bg_opa(m.stem, LV_OPA_COVER, 0);

  m.slash = lv_line_create(m.root);
  lv_line_set_points(m.slash, slash_points, 2);
  lv_obj_set_style_line_width(m.slash, 2, 0);
  lv_obj_set_style_line_rounded(m.slash, true, 0);
  return m;
}

void setMicIcon(const MicIcon &m, uint32_t color, bool slashed) {
  const lv_color_t c = hex(color);
  lv_obj_set_style_border_color(m.capsule, c, 0);
  lv_obj_set_style_arc_color(m.arc, c, LV_PART_MAIN);
  lv_obj_set_style_bg_color(m.stem, c, 0);
  lv_obj_set_style_line_color(m.slash, c, 0);
  lv_obj_set_hidden(m.slash, !slashed);
}

}  // namespace ui
