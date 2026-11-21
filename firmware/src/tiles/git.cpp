// The Git tile (Dev): VS Code's active repo, the branch with ahead/behind, the changes, the
// last commit, each on a line of its own, and Pull, Push, Fetch, Stash and Commit in a grid
// below with room for their names. One of a kind (the manager places each kind once), so
// its state lives here.
#include <string.h>

#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kTickMs = 1000;  // "n min ago"
constexpr uint32_t kFailText = 0xFF9A70;

struct {
  lv_obj_t *title, *busy, *branch, *ab, *chips, *last, *buttons;
  lv_obj_t *chip_m, *chip_a, *chip_d, *chip_u, *chip_clean;
  lv_obj_t *push_badge;
} w;

uint32_t git_seen = UINT32_MAX;
uint32_t tick_ms = 0;

void onGit(lv_event_t *e) { pc::git(static_cast<const char *>(lv_event_get_user_data(e))); }

lv_obj_t *chip(lv_obj_t *parent, uint32_t color) {
  lv_obj_t *c = card(parent, kRaised, kRaised);
  lv_obj_set_size(c, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  lv_obj_set_style_pad_hor(c, 7, 0);
  lv_obj_set_style_pad_ver(c, 3, 0);
  lv_obj_set_style_radius(c, 6, 0);
  label(c, "", &small_font, color);
  return c;
}

void setChip(lv_obj_t *c, int count, const char *suffix) {
  lv_label_set_text_fmt(lv_obj_get_child(c, 0), "%d %s", count, suffix);
  lv_obj_set_hidden(c, count == 0);
}

void buildGit(lv_obj_t *c) {
  lv_obj_set_style_pad_all(c, 10, 0);
  flexCol(c, 6);

  lv_obj_t *line = box(c);
  lv_obj_set_size(line, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(line, 8);
  w.title = label(line, "GIT", &small_font, kMuted);
  fillRow(w.title);
  w.busy = label(line, "", &small_font, kAccent);
  line = box(c);
  lv_obj_set_size(line, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(line, 8);
  w.branch = label(line, "", &body_font, kText);
  w.ab = label(line, "", &small_font, 0x8CCBFF);
  w.chips = box(c);
  lv_obj_set_size(w.chips, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  flexRow(w.chips, 4);
  w.chip_m = chip(w.chips, 0xF5C36B);
  w.chip_a = chip(w.chips, kGreen);
  w.chip_d = chip(w.chips, kFailText);
  w.chip_u = chip(w.chips, kMuted);
  w.chip_clean = label(w.chips, "No changes", &small_font, kMuted);
  w.last = label(c, "", &small_font, kMuted);
  ellipsize(w.last);

  static const int32_t cols[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  static const int32_t rows[] = {34, 34, LV_GRID_TEMPLATE_LAST};
  w.buttons = box(c);
  lv_obj_set_size(w.buttons, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_grid_dsc_array(w.buttons, cols, rows);
  lv_obj_set_style_pad_row(w.buttons, 6, 0);
  lv_obj_set_style_pad_column(w.buttons, 6, 0);
  lv_obj_set_style_margin_top(w.buttons, 2, 0);
  static const char *const ops[][2] = {{LV_SYMBOL_DOWN " Pull", "pull"}, {LV_SYMBOL_UP " Push", "push"},
                                       {LV_SYMBOL_REFRESH " Fetch", "fetch"}, {LV_SYMBOL_SAVE " Stash", "stash"},
                                       {LV_SYMBOL_EDIT " Commit", "commit"}};
  int n = 0;
  for (const auto &op : ops) {
    lv_obj_t *b = textButton(w.buttons, 0, 34, kRaised, op[0], kText, onGit, const_cast<char *>(op[1]));
    lv_obj_set_style_pad_hor(b, 3, 0);
    // Pull, Push, Fetch; then Stash, and Commit over two cells.
    lv_obj_set_grid_cell(b, LV_GRID_ALIGN_STRETCH, n % 3, n == 4 ? 2 : 1, LV_GRID_ALIGN_STRETCH, n / 3, 1);
    n++;
    if (!strcmp(op[1], "push")) {
      w.push_badge = card(b, kAccent, kAccent);
      lv_obj_set_size(w.push_badge, LV_SIZE_CONTENT, 16);
      lv_obj_set_style_pad_hor(w.push_badge, 5, 0);
      lv_obj_set_style_pad_ver(w.push_badge, 0, 0);
      lv_obj_set_style_radius(w.push_badge, 8, 0);
      lv_obj_set_ignore_layout(w.push_badge, true);
      lv_obj_align(w.push_badge, LV_ALIGN_TOP_RIGHT, 4, -6);
      lv_obj_center(label(w.push_badge, "", &small_font, kOnAccent));
    }
  }
}

void updateGit() {
  const model::Git &g = model::git;
  const bool usable = pc::connected() && g.repo.length() && !g.error.length();
  setTextf(w.title, "GIT · %s", g.repo.length() ? g.repo.c_str() : "NO REPO");
  if (g.busy.length()) setTextf(w.busy, "%s…", g.busy.c_str());
  else setText(w.busy, "");

  if (!usable) {
    setTextUpTo(w.branch, !pc::connected() ? "PC offline"
                          : g.error.length() ? g.error.c_str()
                                             : "Open a folder in VS Code", 260);
    setTextColor(w.branch, kMuted);
    setText(w.ab, "");
    setHidden(w.chips, true);
    setText(w.last, "");
  } else {
    setTextUpTo(w.branch, g.branch.c_str(), 160);
    setTextColor(w.branch, kText);
    setTextf(w.ab, LV_SYMBOL_UP "%d  " LV_SYMBOL_DOWN "%d", g.ahead, g.behind);
    setHidden(w.chips, false);
    setChip(w.chip_m, g.modified, "M");
    setChip(w.chip_a, g.added, "A");
    setChip(w.chip_d, g.deleted, "D");
    setChip(w.chip_u, g.untracked, "new");
    setHidden(w.chip_clean, g.modified + g.added + g.deleted + g.untracked > 0);
    char ago[24];
    formatAgo(ago, sizeof(ago), model::agedSecs(g.last_secs, g.at_ms));
    if (g.last.length()) setTextf(w.last, "Last: %s · %s", g.last.c_str(), ago);
    else setText(w.last, "No commits yet");
  }
  setHidden(w.push_badge, !usable || g.ahead == 0);
  setTextf(lv_obj_get_child(w.push_badge, 0), "%d", g.ahead);
  const bool enabled = usable && !g.busy.length();
  for (uint32_t i = 0; i < lv_obj_get_child_count(w.buttons); i++) {
    lv_obj_t *b = lv_obj_get_child(w.buttons, i);
    if (lv_obj_is_clickable(b) == enabled) continue;
    lv_obj_set_style_opa(b, enabled ? LV_OPA_COVER : LV_OPA_40, 0);
    lv_obj_set_clickable(b, enabled);
  }
}

class Git : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildGit(c); }
  void update() override {
    const bool tick = millis() - tick_ms >= kTickMs;
    if (tick) tick_ms = millis();
    if (!tick && model::git.version == git_seen) return;
    git_seen = model::git.version;
    updateGit();
  }
};

}  // namespace

extern const Kind kGit = {"git", {5, 7}, {}, []() -> Tile * { return new Git(); }};

}  // namespace ui::tiles
