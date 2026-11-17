#include "page_panels.h"

#include "fonts/font_icons.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::panels {
namespace {

lv_obj_t *page_obj = nullptr;
uint32_t panels_seen = UINT32_MAX;
int connected_seen = -1;
int32_t height_seen = -1;

// A tap on a row with a link: which panel and row, packed (panel * kMaxRows + row).
void onRow(lv_event_t *e) {
  const int packed = int(intptr_t(lv_event_get_user_data(e)));
  const int p = packed / model::Panel::kMaxRows, r = packed % model::Panel::kMaxRows;
  const model::Panels &all = model::panels;
  if (p >= all.count || r >= all.list[p].row_count || !all.list[p].rows[r].link) return;
  pc::openPanelRow(all.list[p].id.c_str(), all.list[p].rows[r].id.c_str());
}

void note(lv_obj_t *parent, const char *text) {
  lv_obj_t *l = label(parent, text, &body_font, kMuted);
  lv_obj_set_width(l, LV_PCT(100));
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_WRAP);
}

// One panel's card: its symbol and name, its note (when it couldn't be read, why), its rows.
void card(lv_obj_t *parent, int p, int32_t w, int32_t h) {
  const model::Panel &panel = model::panels.list[p];
  lv_obj_t *c = ui::card(parent);
  lv_obj_set_size(c, w, h);
  lv_obj_set_style_pad_all(c, 10, 0);
  lv_obj_set_style_pad_bottom(c, 4, 0);
  flexCol(c, 4);

  lv_obj_t *head = box(c);
  lv_obj_set_size(head, LV_PCT(100), 26);
  flexRow(head, 8);
  const char *glyph = glyphOf(panel.glyph.c_str());
  if (glyph[0]) label(head, glyph, &font_icons_15, kAccent);
  String name = panel.name;
  name.toUpperCase();
  lv_obj_t *title = label(head, name.c_str(), &small_font, kMuted);
  fillRow(title);
  if (panel.row_count) {
    char count[8];
    snprintf(count, sizeof(count), "%d", panel.row_count);
    label(head, count, &small_font, kMuted);
  }

  const bool failed = panel.status == "error";
  if (failed || panel.note.length()) {
    lv_obj_t *n = label(c, panel.note.length() ? panel.note.c_str() : "Can't be read right now.", &small_font, failed ? kAmber : kSoftText);
    lv_obj_set_width(n, LV_PCT(100));
    lv_label_set_long_mode(n, LV_LABEL_LONG_MODE_WRAP);
  }

  lv_obj_t *rows = box(c);
  lv_obj_set_width(rows, LV_PCT(100));
  lv_obj_set_flex_grow(rows, 1);
  lv_obj_set_clickable(rows, true);  // so a drag anywhere on it scrolls it
  lv_obj_set_scrollable(rows, true);
  lv_obj_set_scroll_dir(rows, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(rows, LV_SCROLLBAR_MODE_ACTIVE);
  flexCol(rows, 4);
  if (panel.status.length() == 0) note(rows, "Reading…");
  else if (panel.row_count == 0 && !failed) note(rows, "Nothing here.");
  for (int r = 0; r < panel.row_count; r++) {
    const model::PanelRow &row = panel.rows[r];
    lv_obj_t *b = row.link ? button(rows, LV_PCT(100), LV_SIZE_CONTENT, kRaised, onRow, reinterpret_cast<void *>(intptr_t(p * model::Panel::kMaxRows + r)))
                           : ui::card(rows, kRaised, kRaised);
    if (!row.link) lv_obj_set_size(b, LV_PCT(100), LV_SIZE_CONTENT);
    lv_obj_set_style_radius(b, 10, 0);
    lv_obj_set_style_min_height(b, 40, 0);
    lv_obj_set_style_pad_ver(b, 5, 0);
    lv_obj_set_style_pad_hor(b, 10, 0);
    flexRow(b, 8);
    lv_obj_t *text = box(b);
    lv_obj_set_height(text, LV_SIZE_CONTENT);
    lv_obj_set_flex_grow(text, 1);
    flexCol(text, 0);
    lv_obj_t *t = label(text, row.text.c_str(), &body_font, kText);
    ellipsize(t);
    if (row.sub.length()) ellipsize(label(text, row.sub.c_str(), &small_font, kMuted));
    if (row.badge.length()) {
      lv_obj_t *badge = ui::card(b, kAmberBg, kAmberBg);
      lv_obj_set_size(badge, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
      lv_obj_set_style_pad_hor(badge, 8, 0);
      lv_obj_set_style_pad_ver(badge, 2, 0);
      lv_obj_set_style_radius(badge, 10, 0);
      lv_obj_t *l = label(badge, row.badge.c_str(), &small_font, kAmber);
      lv_obj_set_style_max_width(l, 110, 0);
      lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
    }
    if (row.link) label(b, glyphOf("external"), &font_icons_15, kMuted);
  }
}

// Rebuilt whole when the panels change: a few dozen objects. One to three panels side by
// side, four two by two.
void fill() {
  lv_obj_clean(page_obj);
  const model::Panels &all = model::panels;
  if (!pc::connected()) {
    note(page_obj, "PC offline: the panels come from the companion.");
    return;
  }
  if (all.count == 0) {
    note(page_obj, "No panels yet: they're the profile's \"panels\" (the README shows how).");
    return;
  }
  lv_obj_update_layout(page_obj);
  const int32_t w = lv_obj_get_content_width(page_obj), h = lv_obj_get_content_height(page_obj);
  const int per_row = all.count == 4 ? 2 : all.count;
  const int lines = all.count == 4 ? 2 : 1;
  const int32_t cw = (w - (per_row - 1) * kGap) / per_row, ch = (h - (lines - 1) * kGap) / lines;
  for (int p = 0; p < all.count; p++) card(page_obj, p, cw, ch);
}

}  // namespace

void build(lv_obj_t *page) {
  page_obj = page;
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  lv_obj_set_flex_flow(page, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_style_pad_column(page, kGap, 0);
  lv_obj_set_style_pad_row(page, kGap, 0);
}

void update() {
  const int connected = pc::connected() ? 1 : 0;
  const int32_t height = lv_obj_get_content_height(page_obj);  // the context bar takes room, or gives it back
  if (model::panels.version == panels_seen && connected == connected_seen && height == height_seen) return;
  panels_seen = model::panels.version;
  connected_seen = connected;
  height_seen = height;
  fill();
}

}  // namespace ui::panels
