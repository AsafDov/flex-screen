#include "page_dev.h"

#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::dev {
namespace {

constexpr int kOutputLines = 8;  // "$ command" plus the last seven lines of output
constexpr int kOutputW = 340;
constexpr int kRecentH = 37;
constexpr int kHistory = 10;
constexpr uint32_t kTickMs = 1000;  // "n min ago" and the running timer

constexpr uint32_t kFailText = 0xFF9A70;
constexpr uint32_t kFailBox = 0xFF7A45;
constexpr uint32_t kCodeBg = 0x0A0C0F;

// The banner's look per Claude state.
struct BannerStyle {
  uint32_t bg, border, icon_bg, icon_fg, title, detail;
  const char *icon;
  const char *text;
};

// Made when used: the palette is chosen at boot, after statics are initialized.
BannerStyle bannerStyle(const model::ClaudeSession *s) {
  if (!s) return {kSurface, kBorder, kRaised, kMuted, kMuted, kMuted, LV_SYMBOL_EDIT, "No Claude sessions"};
  if (s->state == "input") return {0x2A2112, 0x4A3A1C, kAmber, 0x2A2112, 0xF5C36B, 0xD8C9A8, LV_SYMBOL_WARNING, "Claude needs your approval"};
  if (s->state == "working") return {0x13202D, 0x24405C, kAccent, kOnAccent, kText, 0xA9B6C4, LV_SYMBOL_REFRESH, "Claude is working"};
  if (s->state == "done") return {0x10241D, 0x1F4A3A, kGreen, 0x0B1F18, kText, 0xA9C4B8, LV_SYMBOL_OK, "Claude finished"};
  return {kSurface, kBorder, kRaised, kMuted, kText, kMuted, LV_SYMBOL_EDIT, "Claude is ready"};
}

struct {
  lv_obj_t *banner, *banner_icon_bg, *banner_icon, *banner_title, *banner_detail, *banner_more, *banner_open;

  lv_obj_t *git_title, *git_busy, *git_branch, *git_ab, *git_chips, *git_last, *git_buttons;
  lv_obj_t *chip_m, *chip_a, *chip_d, *chip_u, *chip_clean;
  lv_obj_t *push_badge;

  lv_obj_t *build_icon_bg, *build_icon, *build_state, *build_when, *build_cmd;
  lv_obj_t *history[kHistory];

  lv_obj_t *term_title, *term_lines[kOutputLines], *term_status, *recent_note;
  struct {
    lv_obj_t *root, *icon, *cmd, *when;
  } recent[model::Recent::kMax];
} w;

uint32_t claude_seen = UINT32_MAX, git_seen = UINT32_MAX, build_seen = UINT32_MAX;
uint32_t term_seen = UINT32_MAX, recent_seen = UINT32_MAX;
uint32_t tick_ms = 0;

// ---- actions

void onOpenClaude(lv_event_t *) {
  if (model::claude.count > 0) pc::openClaude(model::claude.sessions[0].id.c_str());
}

void onGit(lv_event_t *e) { pc::git(static_cast<const char *>(lv_event_get_user_data(e))); }
void onFocusTerminal(lv_event_t *) { pc::focusTerminal(); }
void onInterrupt(lv_event_t *) { pc::interruptCommand(); }

void onRecent(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::recent.count) pc::replayCommand(model::recent.list[i].cmd.c_str());
}

// ---- building

lv_obj_t *header(lv_obj_t *parent, const char *text, lv_obj_t **title) {
  lv_obj_t *h = box(parent);
  lv_obj_set_size(h, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(h, 6);
  lv_obj_set_flex_align(h, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  *title = label(h, text, &small_font, kMuted);
  fillRow(*title);
  return h;
}

lv_obj_t *textButton(lv_obj_t *parent, int width, int height, uint32_t bg, const char *text, uint32_t color,
                     lv_event_cb_t cb, void *user = nullptr) {
  lv_obj_t *b = button(parent, width, height, bg, cb, user);
  lv_obj_set_style_radius(b, 10, 0);
  lv_obj_set_style_pad_hor(b, 8, 0);
  lv_obj_t *l = label(b, text, &small_font, color);
  // Only a button of set width can cut its label short: a percentage of a button sized to
  // its label came out as nothing, and those buttons (Open, Focus, Ctrl+C) showed blank.
  if (width != LV_SIZE_CONTENT) {
    lv_obj_set_style_max_width(l, LV_PCT(100), 0);
    lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
  }
  lv_obj_center(l);
  return b;
}

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

void buildBanner(lv_obj_t *page) {
  w.banner = card(page);
  lv_obj_set_size(w.banner, LV_PCT(100), 52);
  lv_obj_set_style_pad_hor(w.banner, 12, 0);
  lv_obj_set_style_pad_ver(w.banner, 0, 0);
  flexRow(w.banner, 12);
  // The whole banner brings up the session's window, not just its button.
  lv_obj_set_clickable(w.banner, true);
  lv_obj_add_event_cb(w.banner, onOpenClaude, LV_EVENT_CLICKED, nullptr);
  w.banner_icon_bg = box(w.banner);
  lv_obj_set_size(w.banner_icon_bg, 34, 34);
  lv_obj_set_style_radius(w.banner_icon_bg, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_bg_opa(w.banner_icon_bg, LV_OPA_COVER, 0);
  w.banner_icon = label(w.banner_icon_bg, "", &lv_font_montserrat_20, kMuted);
  lv_obj_center(w.banner_icon);

  lv_obj_t *text = box(w.banner);
  lv_obj_set_flex_grow(text, 1);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  flexCol(text, 2);
  w.banner_title = label(text, "", &lv_font_montserrat_16, kText);
  w.banner_detail = label(text, "", &small_font, kMuted);
  ellipsize(w.banner_title);
  ellipsize(w.banner_detail);
  w.banner_more = label(w.banner, "", &small_font, kMuted);

  w.banner_open = textButton(w.banner, LV_SIZE_CONTENT, 38, kAccent, "Open  " LV_SYMBOL_RIGHT, kOnAccent, onOpenClaude);
  lv_obj_set_style_pad_hor(w.banner_open, 14, 0);
}

// The terminal across the page: the commands run lately on the left (tap one to run it
// again), the newest one's output on the right, Ctrl+C and Focus up top.
void buildTerminal(lv_obj_t *page) {
  lv_obj_t *c = card(page);
  lv_obj_set_width(c, LV_PCT(100));
  lv_obj_set_flex_grow(c, 1);
  lv_obj_set_style_pad_all(c, 10, 0);
  flexCol(c, 6);
  lv_obj_t *h = header(c, "TERMINAL", &w.term_title);
  w.term_status = label(h, "", &small_font, kMuted);
  lv_obj_t *stop = textButton(h, LV_SIZE_CONTENT, 26, kRaised, "Ctrl+C", kFailText, onInterrupt);
  lv_obj_set_style_margin_left(stop, 6, 0);
  lv_obj_t *focus = textButton(h, LV_SIZE_CONTENT, 26, kSurface, "Focus  " LV_SYMBOL_RIGHT, kAccent, onFocusTerminal);
  lv_obj_set_ext_click_area(focus, 8);

  lv_obj_t *body = box(c);
  lv_obj_set_width(body, LV_PCT(100));
  lv_obj_set_flex_grow(body, 1);
  flexRow(body, 8);
  lv_obj_set_flex_align(body, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);

  lv_obj_t *list = box(body);
  lv_obj_set_height(list, LV_PCT(100));
  lv_obj_set_flex_grow(list, 1);
  flexCol(list, 4);
  w.recent_note = label(list, "", &small_font, kMuted);
  lv_obj_set_width(w.recent_note, LV_PCT(100));
  lv_label_set_long_mode(w.recent_note, LV_LABEL_LONG_MODE_WRAP);
  for (int i = 0; i < model::Recent::kMax; i++) {
    auto &r = w.recent[i];
    r.root = button(list, LV_PCT(100), kRecentH, kRaised, onRecent, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(r.root, 10, 0);
    lv_obj_set_style_pad_hor(r.root, 10, 0);
    flexRow(r.root, 8);
    r.icon = label(r.root, "", &small_font, kMuted);
    r.cmd = label(r.root, "", &small_font, kText);
    fillRow(r.cmd);
    r.when = label(r.root, "", &small_font, kMuted);
    label(r.root, LV_SYMBOL_LOOP, &small_font, kAccent);
  }

  lv_obj_t *out = card(body, kCodeBg, kCodeBg);
  lv_obj_set_size(out, kOutputW, LV_PCT(100));
  lv_obj_set_style_pad_all(out, 8, 0);
  lv_obj_set_style_radius(out, 10, 0);
  flexCol(out, 1);
  for (int i = 0; i < kOutputLines; i++) {
    w.term_lines[i] = label(out, "", &small_font, i == 0 ? kAccent : kSoftText);
    lv_obj_set_width(w.term_lines[i], LV_PCT(100));
    lv_label_set_long_mode(w.term_lines[i], LV_LABEL_LONG_MODE_CLIP);
  }
}

// Git in a strip: repo, branch, changes and last commit on one line, the operations below.
void buildGit(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_height(c, LV_PCT(100));
  lv_obj_set_flex_grow(c, 1);
  lv_obj_set_style_pad_all(c, 10, 0);
  flexCol(c, 6);

  lv_obj_t *line = box(c);
  lv_obj_set_size(line, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(line, 8);
  w.git_title = label(line, "GIT", &small_font, kMuted);
  w.git_branch = label(line, "", &body_font, kText);
  w.git_ab = label(line, "", &small_font, 0x8CCBFF);
  w.git_chips = box(line);
  lv_obj_set_size(w.git_chips, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  flexRow(w.git_chips, 4);
  w.chip_m = chip(w.git_chips, 0xF5C36B);
  w.chip_a = chip(w.git_chips, kGreen);
  w.chip_d = chip(w.git_chips, kFailText);
  w.chip_u = chip(w.git_chips, kMuted);
  w.chip_clean = label(w.git_chips, "No changes", &small_font, kMuted);
  w.git_last = label(line, "", &small_font, kMuted);
  fillRow(w.git_last);
  w.git_busy = label(line, "", &small_font, kAccent);

  w.git_buttons = box(c);
  lv_obj_set_size(w.git_buttons, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(w.git_buttons, 6);
  static const char *const ops[][2] = {{LV_SYMBOL_DOWN " Pull", "pull"}, {LV_SYMBOL_UP " Push", "push"},
                                       {LV_SYMBOL_REFRESH " Fetch", "fetch"}, {LV_SYMBOL_SAVE " Stash", "stash"},
                                       {LV_SYMBOL_EDIT " Commit", "commit"}};
  for (const auto &op : ops) {
    lv_obj_t *b = textButton(w.git_buttons, 0, 32, kRaised, op[0], kText, onGit, const_cast<char *>(op[1]));
    lv_obj_set_flex_grow(b, 1);
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

// Build in a strip: result, when and how long, the last 10 runs. (Building again is
// the recent commands' job: the build command changes all the time.)
void buildBuild(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_size(c, 300, LV_PCT(100));
  lv_obj_set_style_pad_all(c, 10, 0);
  flexRow(c, 10);
  w.build_icon_bg = box(c);
  lv_obj_set_size(w.build_icon_bg, 36, 36);
  lv_obj_set_style_radius(w.build_icon_bg, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_bg_opa(w.build_icon_bg, LV_OPA_COVER, 0);
  w.build_icon = label(w.build_icon_bg, "", &lv_font_montserrat_16, kMuted);
  lv_obj_center(w.build_icon);

  lv_obj_t *text = box(c);
  lv_obj_set_flex_grow(text, 1);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  flexCol(text, 2);
  w.build_state = label(text, "", &body_font, kText);
  w.build_when = label(text, "", &small_font, kMuted);
  ellipsize(w.build_when);
  w.build_cmd = label(text, "", &small_font, kMuted);  // only in the companion's toast now
  lv_obj_set_hidden(w.build_cmd, true);
  lv_obj_t *hist = box(text);
  lv_obj_set_size(hist, LV_PCT(100), 12);
  flexRow(hist, 3);
  for (lv_obj_t *&box_ : w.history) {
    box_ = card(hist, kRaised, kRaised);
    lv_obj_set_size(box_, 11, 12);
    lv_obj_set_style_pad_all(box_, 0, 0);
    lv_obj_set_style_radius(box_, 3, 0);
    lv_obj_center(label(box_, "", &small_font, 0x2A1206));
  }
}

// ---- updating

const model::ClaudeSession *topSession() { return model::claude.count > 0 ? &model::claude.sessions[0] : nullptr; }

void updateClaude() {
  const model::ClaudeSession *s = pc::connected() ? topSession() : nullptr;
  const BannerStyle st = bannerStyle(s);
  setBgColor(w.banner, st.bg);
  setBorderColor(w.banner, st.border);
  setBgColor(w.banner_icon_bg, st.icon_bg);
  setText(w.banner_icon, st.icon);
  setTextColor(w.banner_icon, st.icon_fg);
  setText(w.banner_title, st.text);
  setTextColor(w.banner_title, st.title);
  setTextColor(w.banner_detail, st.detail);
  setHidden(w.banner_open, s == nullptr);

  if (!s) {
    setText(w.banner_detail, pc::connected() ? "Sessions in VS Code and the Claude app show here" : "PC offline");
    setText(w.banner_more, "");
    return;
  }
  char ago[24];
  formatAgo(ago, sizeof(ago), model::agedSecs(s->secs, model::claude.at_ms));
  String detail = s->project;
  if (s->detail.length()) detail += " · " + s->detail;
  if (ago[0]) detail += String(" · ") + ago;
  setText(w.banner_detail, detail.c_str());
  if (model::claude.count > 1) setTextf(w.banner_more, "+%d more", model::claude.count - 1);
  else setText(w.banner_more, "");
}

void updateGit() {
  const model::Git &g = model::git;
  const bool usable = pc::connected() && g.repo.length() && !g.error.length();
  setTextf(w.git_title, "GIT · %s", g.repo.length() ? g.repo.c_str() : "NO REPO");
  if (g.busy.length()) setTextf(w.git_busy, "%s…", g.busy.c_str());
  else setText(w.git_busy, "");

  if (!usable) {
    setTextUpTo(w.git_branch, !pc::connected() ? "PC offline"
                              : g.error.length() ? g.error.c_str()
                                                 : "Open a folder in VS Code", 260);
    setTextColor(w.git_branch, kMuted);
    setText(w.git_ab, "");
    setHidden(w.git_chips, true);
    setText(w.git_last, "");
  } else {
    setTextUpTo(w.git_branch, g.branch.c_str(), 160);
    setTextColor(w.git_branch, kText);
    setTextf(w.git_ab, LV_SYMBOL_UP "%d  " LV_SYMBOL_DOWN "%d", g.ahead, g.behind);
    setHidden(w.git_chips, false);
    setChip(w.chip_m, g.modified, "M");
    setChip(w.chip_a, g.added, "A");
    setChip(w.chip_d, g.deleted, "D");
    setChip(w.chip_u, g.untracked, "new");
    setHidden(w.chip_clean, g.modified + g.added + g.deleted + g.untracked > 0);
    char ago[24];
    formatAgo(ago, sizeof(ago), model::agedSecs(g.last_secs, g.at_ms));
    if (g.last.length()) setTextf(w.git_last, "Last: %s · %s", g.last.c_str(), ago);
    else setText(w.git_last, "No commits yet");
  }
  setHidden(w.push_badge, !usable || g.ahead == 0);
  setTextf(lv_obj_get_child(w.push_badge, 0), "%d", g.ahead);
  const bool enabled = usable && !g.busy.length();
  for (uint32_t i = 0; i < lv_obj_get_child_count(w.git_buttons); i++) {
    lv_obj_t *b = lv_obj_get_child(w.git_buttons, i);
    if (lv_obj_is_clickable(b) == enabled) continue;
    lv_obj_set_style_opa(b, enabled ? LV_OPA_COVER : LV_OPA_40, 0);
    lv_obj_set_clickable(b, enabled);
  }
}

void updateBuild() {
  const model::Build &b = model::build;
  const char *icon = LV_SYMBOL_MINUS;
  const char *state = "No builds yet";
  uint32_t bg = kRaised, fg = kMuted, text = kMuted;
  if (b.state == "running") {
    icon = LV_SYMBOL_REFRESH, state = "Building", bg = 0x13202D, fg = kAccent, text = kAccent;
  } else if (b.state == "passed") {
    icon = LV_SYMBOL_OK, state = "Passed", bg = 0x143A2E, fg = kGreen, text = kGreen;
  } else if (b.state == "failed") {
    icon = LV_SYMBOL_CLOSE, state = "Failed", bg = 0x3A1D12, fg = kFailText, text = kFailText;
  }
  setBgColor(w.build_icon_bg, bg);
  setText(w.build_icon, icon);
  setTextColor(w.build_icon, fg);
  setText(w.build_state, state);
  setTextColor(w.build_state, text);

  char ago[24], took[24];
  const int32_t secs = model::agedSecs(b.secs, b.at_ms);
  if (b.state == "running") {
    formatTime(took, sizeof(took), float(secs < 0 ? 0 : secs));
    setTextf(w.build_when, "running · %s", took);
  } else if (b.state != "none") {
    formatAgo(ago, sizeof(ago), secs);
    formatDuration(took, sizeof(took), b.ms);
    setTextf(w.build_when, "%s · %s", ago, took);
  } else {
    setText(w.build_when, "Builds in VS Code show here");
  }
  setText(w.build_cmd, b.cmd.length() ? b.cmd.c_str() : "—");

  // Newest on the right, empty slots on the left.
  const int n = b.history.length();
  for (int i = 0; i < kHistory; i++) {
    const int h = i - (kHistory - n);
    const char r = h >= 0 ? b.history[h] : ' ';
    setBgColor(w.history[i], r == 'P' ? kGreen : r == 'F' ? kFailBox : kRaised);
    setText(lv_obj_get_child(w.history[i], 0), r == 'F' ? LV_SYMBOL_CLOSE : "");
  }
}

void updateTerm() {
  const model::Term &t = model::term;
  setTextf(w.term_title, "TERMINAL%s%s", t.repo.length() ? " · " : "", t.repo.c_str());
  if (!t.cmd.length()) {
    setText(w.term_lines[0], pc::connected() ? "No commands yet" : "PC offline");
    setTextColor(w.term_lines[0], kMuted);
    for (int i = 1; i < kOutputLines; i++) setText(w.term_lines[i], "");
    setText(w.term_status, "");
    return;
  }
  setTextf(w.term_lines[0], "$ %s", t.cmd.c_str());
  setTextColor(w.term_lines[0], kAccent);
  // The tail of the output, bottom-aligned under the command.
  const int shown = t.line_count < kOutputLines - 1 ? t.line_count : kOutputLines - 1;
  for (int i = 1; i < kOutputLines; i++) {
    const int src = t.line_count - shown + (i - 1);
    setText(w.term_lines[i], i - 1 < shown ? t.lines[src].c_str() : "");
  }

  char took[24];
  if (t.running) {
    const int32_t secs = model::agedSecs(t.secs, t.at_ms);
    formatTime(took, sizeof(took), float(secs < 0 ? 0 : secs));
    setTextf(w.term_status, LV_SYMBOL_REFRESH "  Running · %s", took);
    setTextColor(w.term_status, kAccent);
  } else if (t.has_exit && t.exit != 0) {
    formatDuration(took, sizeof(took), t.ms);
    setTextf(w.term_status, LV_SYMBOL_CLOSE "  Failed (exit %d) · %s", t.exit, took);
    setTextColor(w.term_status, kFailText);
  } else {
    formatDuration(took, sizeof(took), t.ms);
    setTextf(w.term_status, LV_SYMBOL_OK "  Done · %s", took);
    setTextColor(w.term_status, kGreen);
  }
}

void updateRecent() {
  const model::Recent &r = model::recent;
  const bool online = pc::connected();
  setText(w.recent_note, !online ? "PC offline"
                                 : "Commands you run in VS Code's terminal show here. Tap one to run it again.");
  setHidden(w.recent_note, online && r.count > 0);
  for (int i = 0; i < model::Recent::kMax; i++) {
    auto &row = w.recent[i];
    const bool used = online && i < r.count;
    setHidden(row.root, !used);
    if (!used) continue;
    const model::RecentCommand &c = r.list[i];
    const bool failed = !c.running && c.has_exit && c.exit != 0;
    setText(row.icon, c.running ? LV_SYMBOL_REFRESH : failed ? LV_SYMBOL_CLOSE : LV_SYMBOL_OK);
    setTextColor(row.icon, c.running ? kAccent : failed ? kFailText : kGreen);
    setText(row.cmd, c.cmd.c_str());
    char when[24];
    if (c.running) snprintf(when, sizeof(when), "running");
    else formatAgo(when, sizeof(when), model::agedSecs(c.secs, r.at_ms));
    setText(row.when, when);
  }
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  flexCol(page, kGap);
  lv_obj_set_scrollable(page, false);
  buildBanner(page);
  buildTerminal(page);
  lv_obj_t *row = box(page);
  lv_obj_set_size(row, LV_PCT(100), 82);
  flexRow(row, kGap);
  lv_obj_set_flex_align(row, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  buildGit(row);
  buildBuild(row);
  updateRecent();
}

void update() {
  const bool tick = millis() - tick_ms >= kTickMs;
  if (tick) tick_ms = millis();
  if (tick || model::claude.version != claude_seen) {
    claude_seen = model::claude.version;
    updateClaude();
  }
  if (tick || model::git.version != git_seen) {
    git_seen = model::git.version;
    updateGit();
  }
  if (tick || model::build.version != build_seen) {
    build_seen = model::build.version;
    updateBuild();
  }
  if ((tick && model::term.running) || model::term.version != term_seen) {
    term_seen = model::term.version;
    updateTerm();
  }
  if (tick || model::recent.version != recent_seen) {
    recent_seen = model::recent.version;
    updateRecent();
  }
}

}  // namespace ui::dev
