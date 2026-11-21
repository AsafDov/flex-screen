// The Obsidian tile (Capture): today's daily note, and the recent notes in a grid of two
// even columns, each name cut to its cell (chips as wide as their names looked heaped one
// after the other); as many rows as fit. One of a kind (the manager places each kind once),
// so its state lives here.
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kPurple = 0xB48CFF;  // Obsidian's purple, from the design canvas
constexpr uint32_t kOnObsidian = 0x1A1030;

struct {
  lv_obj_t *card, *vault_title, *note;
  lv_obj_t *today;
  lv_obj_t *recent[model::Obsidian::kMaxRecent], *recent_text[model::Obsidian::kMaxRecent];
} w;

uint32_t obsidian_seen = UINT32_MAX;
int connected_seen = -1;
int32_t height_seen = -1;  // the card's: the context bar coming or going changes what fits

void onObsidian(lv_event_t *e) { pc::obsidian(static_cast<const char *>(lv_event_get_user_data(e))); }

void onRecent(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::obsidian.recent_count) pc::openNote(model::obsidian.recent[i].path.c_str());
}

void buildObsidian(lv_obj_t *c) {
  w.card = c;
  lv_obj_set_style_pad_all(c, 10, 0);
  flexCol(c, 6);
  w.vault_title = label(c, "", &small_font, kMuted);
  ellipsize(w.vault_title);
  w.today = button(c, LV_PCT(100), 40, kPurple, onObsidian, const_cast<char *>("today"));
  lv_obj_set_style_radius(w.today, 12, 0);
  lv_obj_center(label(w.today, LV_SYMBOL_FILE "  Today's note", &body_font, kOnObsidian));
  w.note = label(c, "", &small_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);
  lv_obj_t *grid = box(c);
  lv_obj_set_width(grid, LV_PCT(100));
  lv_obj_set_flex_grow(grid, 1);
  lv_obj_set_flex_flow(grid, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_flex_align(grid, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  lv_obj_set_style_pad_row(grid, 5, 0);  // two rows under Today's note: at 28 and 6 the second missed by a pixel
  for (int i = 0; i < model::Obsidian::kMaxRecent; i++) {
    lv_obj_t *b = button(grid, LV_PCT(48), 26, kRaised, onRecent, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(b, 8, 0);
    lv_obj_set_style_pad_hor(b, 8, 0);
    flexRow(b, 6);
    label(b, LV_SYMBOL_FILE, &small_font, kPurple);
    w.recent_text[i] = label(b, "", &small_font, kText);
    fillRow(w.recent_text[i]);
    w.recent[i] = b;
  }
}

void updateObsidian() {
  const model::Obsidian &o = model::obsidian;
  const bool online = pc::connected();
  const bool ok = online && o.found;
  if (ok) {
    String title = "OBSIDIAN · " + o.vault;
    title.toUpperCase();
    setText(w.vault_title, title.c_str());
  } else {
    setText(w.vault_title, "OBSIDIAN");
  }
  setText(w.note, !online ? "PC offline" : !o.found ? "No Obsidian vault found on the PC." : "");
  setHidden(w.note, ok);
  lv_obj_set_style_opa(w.today, ok ? LV_OPA_COVER : LV_OPA_40, 0);
  lv_obj_set_clickable(w.today, ok);
  for (int i = 0; i < model::Obsidian::kMaxRecent; i++) {
    const bool used = ok && i < o.recent_count;
    setHidden(w.recent[i], !used);
    if (used) setText(w.recent_text[i], o.recent[i].name.c_str());
  }
  // Only the notes that fit: past the card's foot they'd run out of it.
  lv_obj_update_layout(w.card);
  lv_area_t inside;
  lv_obj_get_content_coords(w.card, &inside);
  bool past = false;
  for (lv_obj_t *row : w.recent) {
    if (lv_obj_is_hidden(row)) continue;
    lv_area_t a;
    lv_obj_get_coords(row, &a);
    past = past || a.y2 > inside.y2;
    if (past) lv_obj_set_hidden(row, true);
  }
}

class Obsidian : public Tile {
 public:
  void build(lv_obj_t *c, Span) override {
    buildObsidian(c);
    updateObsidian();
  }
  void update() override {
    const int connected = pc::connected() ? 1 : 0;
    const int32_t height = lv_obj_get_height(w.card);
    if (connected == connected_seen && height == height_seen && model::obsidian.version == obsidian_seen) return;
    connected_seen = connected;
    height_seen = height;
    obsidian_seen = model::obsidian.version;
    updateObsidian();
  }
};

}  // namespace

// Five rows: Today's note and two rows of recent notes under it, as the 150 px it had.
extern const Kind kObsidian = {"obsidian", {6, 5}, {}, []() -> Tile * { return new Obsidian(); }};

}  // namespace ui::tiles
