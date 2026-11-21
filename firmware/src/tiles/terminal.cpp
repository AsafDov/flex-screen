// The Terminal tile (Dev): the last few commands run in VS Code's terminal, whole (a long
// one wraps), with ✓/✕ and the time each started; tap one to run it again in the terminal
// it ran in. Nothing else, on request: no output, Ctrl+C or Focus. One of a kind (the
// manager places each kind once), so its state lives here.
#include <time.h>

#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr int kRecentH = 34;  // a command row's least height: one line (a long command wraps to two)
constexpr uint32_t kFailText = 0xFF9A70;

struct {
  lv_obj_t *card, *title, *list, *note;
  struct {
    lv_obj_t *root, *icon, *cmd, *when;
  } rows[model::Recent::kMax];
} w;

uint32_t term_seen = UINT32_MAX, recent_seen = UINT32_MAX;
int32_t height_seen = -1;  // the card's: the context bar coming or going changes what fits
int64_t recent_minute = -1;  // the rows' times only change with the date ("Yesterday")
bool recent_online = false;

void onRecent(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::recent.count) pc::replayCommand(model::recent.list[i].cmd.c_str());
}

void buildTerminal(lv_obj_t *c) {
  w.card = c;
  lv_obj_set_style_pad_all(c, 10, 0);
  flexCol(c, 6);
  lv_obj_t *h = box(c);
  lv_obj_set_size(h, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(h, 6);
  w.title = label(h, "TERMINAL", &small_font, kMuted);
  fillRow(w.title);

  w.list = box(c);
  lv_obj_set_width(w.list, LV_PCT(100));
  lv_obj_set_flex_grow(w.list, 1);
  flexCol(w.list, 3);
  w.note = label(w.list, "", &small_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);
  for (int i = 0; i < model::Recent::kMax; i++) {
    auto &r = w.rows[i];
    r.root = button(w.list, LV_PCT(100), LV_SIZE_CONTENT, kRaised, onRecent, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_min_height(r.root, kRecentH, 0);
    lv_obj_set_style_radius(r.root, 10, 0);
    lv_obj_set_style_pad_hor(r.root, 12, 0);
    lv_obj_set_style_pad_ver(r.root, 5, 0);
    flexRow(r.root, 10);
    r.icon = label(r.root, "", &small_font, kMuted);
    r.cmd = label(r.root, "", &small_font, kText);
    lv_obj_set_width(r.cmd, 1);  // the row's flex layout gives it the rest
    lv_obj_set_flex_grow(r.cmd, 1);
    lv_label_set_long_mode(r.cmd, LV_LABEL_LONG_MODE_WRAP);
    r.when = label(r.root, "", &small_font, kMuted);
    label(r.root, LV_SYMBOL_LOOP, &small_font, kAccent);
  }
}

// When a command started, `ago` seconds back: "14:05" today, "Yesterday 14:05", a
// weekday this week, else the date. Empty until the companion has sent the time.
void formatStarted(char *buf, size_t n, int32_t ago) {
  const int64_t now = model::localNow();
  buf[0] = 0;
  if (now == 0 || ago < 0) return;
  const int64_t then = now - ago;
  const time_t t = time_t(then);
  struct tm tm;
  gmtime_r(&t, &tm);  // already shifted to local time
  const int64_t days = now / 86400 - then / 86400;
  strftime(buf, n, days == 0 ? "%H:%M" : days == 1 ? "Yesterday %H:%M" : days < 7 ? "%a %H:%M" : "%e %b %H:%M", &tm);
}

void updateRecent() {
  const model::Recent &r = model::recent;
  const bool online = pc::connected();
  setText(w.note, !online ? "PC offline"
                          : "Commands you run in VS Code's terminal show here. Tap one to run it again.");
  setHidden(w.note, online && r.count > 0);
  for (int i = 0; i < model::Recent::kMax; i++) {
    auto &row = w.rows[i];
    const bool used = online && i < r.count;
    setHidden(row.root, !used);
    if (!used) continue;
    const model::RecentCommand &c = r.list[i];
    const bool failed = !c.running && c.has_exit && c.exit != 0;
    setText(row.icon, c.running ? LV_SYMBOL_REFRESH : failed ? LV_SYMBOL_CLOSE : LV_SYMBOL_OK);
    setTextColor(row.icon, c.running ? kAccent : failed ? kFailText : kGreen);
    setText(row.cmd, c.cmd.c_str());
    // `secs` counts from the end of a finished command (from its start while running).
    int32_t ago = model::agedSecs(c.secs, r.at_ms);
    if (ago >= 0 && !c.running) ago += int32_t(c.ms / 1000);
    char when[24];
    formatStarted(when, sizeof(when), ago);
    setText(row.when, when);
  }
  // Long commands take two lines: a row that would run past the card is left out.
  lv_obj_update_layout(w.list);
  const int32_t room = lv_obj_get_content_height(w.list);
  bool full = false;
  for (auto &row : w.rows) {
    if (lv_obj_is_hidden(row.root)) continue;
    full = full || lv_obj_get_y(row.root) + lv_obj_get_height(row.root) > room;
    if (full) setHidden(row.root, true);
  }
}

class Terminal : public Tile {
 public:
  void build(lv_obj_t *c, Span) override {
    buildTerminal(c);
    updateRecent();
  }
  void update() override {
    if (model::term.version != term_seen) {
      term_seen = model::term.version;
      const model::Term &t = model::term;
      setTextf(w.title, "TERMINAL%s%s", t.repo.length() ? " · " : "", t.repo.c_str());
    }
    const int32_t height = lv_obj_get_height(w.card);
    const bool online = pc::connected();
    const int64_t minute = model::localNow() / 60;
    if (model::recent.version != recent_seen || online != recent_online || minute != recent_minute ||
        height != height_seen) {
      recent_seen = model::recent.version;
      recent_online = online;
      recent_minute = minute;
      height_seen = height;
      updateRecent();
    }
  }
};

}  // namespace

extern const Kind kTerminal = {"terminal", {9, 10}, {}, []() -> Tile * { return new Terminal(); }};

}  // namespace ui::tiles
