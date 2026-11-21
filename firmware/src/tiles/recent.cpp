// The Recent tile: what was last done from the deck (windows, notes, commands, meetings),
// a row each, tap to do it again; and the tasks as a pill in its heading, which opens
// Capture's Tasks. (A line of its own under the card took the room the context bar needs.)
#include <time.h>

#include "icons.h"
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kIconRetryMs = 3000;  // asking again for a window icon the deck lacks
constexpr uint32_t kTasksTickMs = 15000;  // what's due changes at midnight

// Each kind's glyph and colors, for everything but a window with its icon.
struct ActionLook {
  const char *glyph;
  uint32_t bg, fg;
};

ActionLook actionLook(const String &kind) {
  if (kind == "note") return {LV_SYMBOL_FILE, 0x2B2142, 0xB49CFF};
  if (kind == "command") return {">_", 0x0A0C0F, kAccent};
  if (kind == "meeting") return {LV_SYMBOL_CALL, 0x143A2E, kGreen};
  if (kind == "mail") return {LV_SYMBOL_ENVELOPE, 0x3A2A10, kAmber};
  if (kind == "claude") return {LV_SYMBOL_EDIT, 0x3A2318, 0xE8936A};
  if (kind == "device") return {LV_SYMBOL_AUDIO, kHighlight, kAccent};
  return {"", 0x2D5C8A, kText};  // a window without its icon: its app's first letter
}

class Recent : public Tile {
 public:
  void build(lv_obj_t *card, Span) override {
    flexCol(card, 8);
    lv_obj_t *head = box(card);
    lv_obj_set_size(head, LV_PCT(100), 26);
    flexRow(head, 8);
    label(head, "RECENT", &lv_font_montserrat_16, kMuted);
    lv_obj_t *gap = box(head);
    lv_obj_set_size(gap, 1, 1);
    lv_obj_set_flex_grow(gap, 1);
    lv_obj_t *tasks = button(head, LV_SIZE_CONTENT, 26, kRaised, [](lv_event_t *) { showPage(kCapturePage); });
    lv_obj_set_style_pad_hor(tasks, 10, 0);
    flexRow(tasks, 6);
    tasks_icon_ = label(tasks, LV_SYMBOL_OK, &lv_font_montserrat_14, kAccent);
    tasks_text_ = label(tasks, "", &small_font, kSoftText);
    label(tasks, LV_SYMBOL_RIGHT, &lv_font_montserrat_12, kMuted);
    note_ = label(card, "", &body_font, kMuted);
    lv_obj_set_width(note_, LV_PCT(100));
    lv_label_set_long_mode(note_, LV_LABEL_LONG_MODE_WRAP);

    // A row each, sharing the card's height: five tiles across the narrower page cut their
    // names to "VS C…" and "Obsid…".
    list_ = box(card);
    lv_obj_set_width(list_, LV_PCT(100));
    lv_obj_set_flex_grow(list_, 1);
    flexCol(list_, 5);
    for (int i = 0; i < model::Actions::kMax; i++) {
      Row &t = rows_[i];
      t.root = button(list_, LV_PCT(100), 1, kRaised, onRow, reinterpret_cast<void *>(intptr_t(i)));
      lv_obj_set_flex_grow(t.root, 1);
      lv_obj_set_style_radius(t.root, 10, 0);
      lv_obj_set_style_pad_hor(t.root, 6, 0);
      flexRow(t.root, 10);

      t.badge = ui::card(t.root, kSurface, kSurface);
      lv_obj_set_size(t.badge, icons::kSize + 4, icons::kSize + 4);
      lv_obj_set_style_pad_all(t.badge, 0, 0);
      lv_obj_set_style_radius(t.badge, 8, 0);
      t.image = lv_image_create(t.badge);
      lv_obj_set_size(t.image, icons::kSize, icons::kSize);
      lv_obj_center(t.image);
      t.glyph = label(t.badge, "", &lv_font_montserrat_16, kText);
      lv_obj_center(t.glyph);

      t.name = label(t.root, "", &body_font, kText);
      lv_obj_set_height(t.name, lv_font_get_line_height(&body_font));
      t.title = label(t.root, "", &small_font, kMuted);
      fillRow(t.title);
    }
  }

  void update() override {
    const int link = pc::connected() ? 1 : 0;
    if (model::tasks.version != tasks_seen_ || link != tasks_link_seen_ || millis() - tasks_tick_ms_ >= kTasksTickMs) {
      tasks_seen_ = model::tasks.version;
      tasks_link_seen_ = link;
      tasks_tick_ms_ = millis();
      updateTasks();
    }
    // Icons arrive after the list, and a missing one is asked for again now and then.
    const bool icon_due = millis() - actions_tick_ms_ >= kIconRetryMs;
    if (model::actions.version != actions_seen_ || link != actions_link_seen_ || icons::version() != icons_seen_ || icon_due) {
      actions_seen_ = model::actions.version;
      actions_link_seen_ = link;
      icons_seen_ = icons::version();
      actions_tick_ms_ = millis();
      updateRows();
    }
  }

 private:
  // A row: an icon (a window's own, else a glyph on a tinted square), what it is, and which one.
  struct Row {
    lv_obj_t *root, *badge, *image, *glyph, *name, *title;
    uint32_t asked_ms = 0;
  };

  static void onRow(lv_event_t *e) {
    const int i = int(intptr_t(lv_event_get_user_data(e)));
    if (i < model::actions.count) pc::runRecent(model::actions.list[i].id.c_str());
  }

  // How many tasks are open, and how many of them are due today or overdue.
  void updateTasks() {
    const model::Tasks &k = model::tasks;
    int urgent = 0;
    const int64_t now = model::localNow();
    if (now) {
      char today[12];
      const time_t t = time_t(now);
      struct tm tm;
      gmtime_r(&t, &tm);  // already shifted to local time
      strftime(today, sizeof(today), "%Y-%m-%d", &tm);
      for (int i = 0; i < k.count; i++) {
        // "yyyy-mm-dd" sorts as text.
        if (!k.list[i].done && k.list[i].due.length() && strcmp(k.list[i].due.c_str(), today) <= 0) urgent++;
      }
    }
    if (!pc::connected() || k.status.length() == 0) setText(tasks_text_, "Tasks");  // not read yet
    else if (k.status == "setup") setText(tasks_text_, "Connect your tasks");
    else if (k.status == "signin") setText(tasks_text_, "Tasks need signing in");
    else if (k.open == 0) setText(tasks_text_, "Nothing to do");
    else if (urgent) setTextf(tasks_text_, "%d to do · %d due", k.open, urgent);
    else setTextf(tasks_text_, "%d to do", k.open);
    setTextColor(tasks_icon_, urgent ? kAmber : kAccent);
  }

  void updateRows() {
    const model::Actions &a = model::actions;
    const bool online = pc::connected();
    const int count = online ? a.count : 0;
    setText(note_, !online ? "PC offline" : "What you do from the deck shows here: windows, notes, commands, meetings. Tap one to do it again.");
    setHidden(note_, count > 0);
    setHidden(list_, count == 0);
    for (int i = 0; i < model::Actions::kMax; i++) {
      Row &t = rows_[i];
      const bool used = i < count;
      // An unused row keeps its height, so the others stay where they are.
      if (lv_obj_is_clickable(t.root) != used) {
        lv_obj_set_style_opa(t.root, used ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
        lv_obj_set_clickable(t.root, used);
      }
      if (!used) continue;
      const model::RecentAction &x = a.list[i];
      setTextUpTo(t.name, x.name.c_str(), 150);
      setText(t.title, x.title.c_str());
      const lv_image_dsc_t *icon = x.kind == "window" ? icons::get(x.icon) : nullptr;
      if (!icon && x.icon.length() && millis() - t.asked_ms >= kIconRetryMs) {
        t.asked_ms = millis();
        pc::requestIcon(x.icon.c_str());
      }
      setHidden(t.image, icon == nullptr);
      setHidden(t.glyph, icon != nullptr);
      if (icon) {
        if (lv_image_get_src(t.image) != icon) lv_image_set_src(t.image, icon);
        setBgColor(t.badge, kRaised);
        continue;
      }
      const ActionLook look = actionLook(x.kind);
      char letter[2] = {x.name.length() ? char(toupper(x.name[0])) : '?', 0};
      setText(t.glyph, look.glyph[0] ? look.glyph : letter);
      setTextColor(t.glyph, look.fg);
      setBgColor(t.badge, look.bg);
    }
  }

  lv_obj_t *tasks_icon_ = nullptr, *tasks_text_ = nullptr, *note_ = nullptr, *list_ = nullptr;
  Row rows_[model::Actions::kMax];
  uint32_t tasks_seen_ = UINT32_MAX, tasks_tick_ms_ = 0;
  int tasks_link_seen_ = -1;
  uint32_t actions_seen_ = UINT32_MAX, icons_seen_ = UINT32_MAX, actions_tick_ms_ = 0;
  int actions_link_seen_ = -1;
};

}  // namespace

// extern: tiles.cpp's list of kinds refers to it.
extern const Kind kRecent = {"recent", {8, 8}, {}, []() -> Tile * { return new Recent(); }};

}  // namespace ui::tiles
