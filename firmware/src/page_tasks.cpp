#include "page_tasks.h"

#include <stdlib.h>
#include <time.h>

#include "fonts/font_icons.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::tasks {
namespace {

constexpr int kFilters = model::Tasks::kMaxLists + 2;  // All tasks, Scheduled, then each list shown on the deck
constexpr uint32_t kBox = 0x7D8794;
constexpr uint32_t kUrgent = 0xF5C36B;
constexpr uint32_t kTickMs = 30000;  // due days move on at midnight
constexpr int kDues = 3;              // the form's: no date, today, tomorrow

struct {
  lv_obj_t *chips, *filters[kFilters], *filter_name[kFilters];
  lv_obj_t *add;
  lv_obj_t *title, *badge, *note, *rows;
} w;

// The New task form, over everything.
struct {
  lv_obj_t *root, *text, *chips, *list_chips[model::Tasks::kMaxLists], *due_chips[kDues], *save, *keyboard;
} form;

// What the card shows: every task of the lists on the deck (where it opens), every dated task
// from any list, or one list (by id, so it stays put as lists come and go).
enum View { kAllTasks, kScheduled, kOneList };
View view = kAllTasks;
String filter_src, filter_id;  // the list, for kOneList
int form_list = 0, form_due = 0;

// The open task, over everything: its title, list, due day, status, what its source says
// about it (project, key, priority…), its notes, and a button that opens it where it lives.
struct {
  lv_obj_t *scrim, *panel, *body, *title, *meta, *source, *notes, *open, *open_text, *done, *done_text;
} pop;
String pop_src, pop_list, pop_id;  // pop_id "" when none is open
uint32_t pop_tasks_seen = UINT32_MAX, pop_detail_seen = UINT32_MAX;

uint32_t tasks_seen = UINT32_MAX;
int connected_seen = -1;
int32_t height_seen = -1;
int64_t day_seen = -1;
uint32_t tick_ms = 0;
bool dirty = true;  // the filter changed
bool to_top = false;  // another chip was tapped: its list starts at the top

int indexOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

// The lists with a chip (chosen in the companion's tray menu), and the one on show.
bool listShown(const model::Tasks &k, const String &src, const String &id) {
  for (int i = 0; i < k.list_count; i++) {
    if (k.lists[i].src == src && k.lists[i].id == id) return k.lists[i].show;
  }
  return false;
}

int shownLists(const model::Tasks &k) {
  int n = 0;
  for (int i = 0; i < k.list_count; i++) n += k.lists[i].show;
  return n;
}

// All tasks: a list's on the deck (the companion's order puts the dated ones first, soonest
// due first). Scheduled: dated, from any list, also one kept off the deck.
bool shown(const model::Task &t) {
  switch (view) {
    case kAllTasks: return listShown(model::tasks, t.src, t.list);
    case kScheduled: return t.due.length() > 0;
    default: return t.src == filter_src && t.list == filter_id;
  }
}

// The lists a new task can go into: on the deck, and not a tracker's (Jira, GitHub).
bool addable(const model::TaskList &l) { return l.show && l.add; }

int addableLists(const model::Tasks &k) {
  int n = 0;
  for (int i = 0; i < k.list_count; i++) n += addable(k.lists[i]);
  return n;
}

// Days since 1970-01-01 for "yyyy-mm-dd" (no time zones involved), or INT64_MIN for "".
int64_t dayNumber(const String &ymd) {
  if (ymd.length() < 10) return INT64_MIN;
  int y = atoi(ymd.c_str());
  const int m = atoi(ymd.c_str() + 5), d = atoi(ymd.c_str() + 8);
  y -= m <= 2;
  const int64_t era = (y >= 0 ? y : y - 399) / 400;
  const unsigned yoe = unsigned(y - era * 400);
  const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
  const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
  return era * 146097 + int64_t(doe) - 719468;
}

int64_t dayToday() {
  const int64_t now = model::localNow();
  return now ? now / 86400 : INT64_MIN;
}

// "yyyy-mm-dd" for today plus `days`, or "" before the companion sends the time.
void dateOf(int days, char *out, size_t n) {
  out[0] = '\0';
  const int64_t now = model::localNow();
  if (!now) return;
  const time_t t = time_t(now + int64_t(days) * 86400);
  struct tm tm;
  gmtime_r(&t, &tm);  // already shifted to local time
  strftime(out, n, "%Y-%m-%d", &tm);
}

enum Group { kOverdue, kToday, kTomorrow, kThisWeek, kLater, kNoDate, kDone };
const char *const kGroupNames[] = {"OVERDUE", "TODAY", "TOMORROW", "THIS WEEK", "LATER", "NO DATE", "DONE"};

Group groupOf(const model::Task &t, int64_t now_day) {
  if (t.done) return kDone;
  const int64_t due = dayNumber(t.due);
  if (due == INT64_MIN || now_day == INT64_MIN) return kNoDate;
  const int64_t days = due - now_day;
  return days < 0 ? kOverdue : days == 0 ? kToday : days == 1 ? kTomorrow : days < 7 ? kThisWeek : kLater;
}

// "Tue 17 Nov" for "2026-11-17", "" for no date.
void dueText(const String &ymd, char *out, size_t n) {
  out[0] = '\0';
  const int64_t day = dayNumber(ymd);
  if (day == INT64_MIN) return;
  const time_t at = time_t(day * 86400);
  struct tm d;
  gmtime_r(&at, &d);
  char weekday[8], month[8];
  strftime(weekday, sizeof(weekday), "%a", &d);
  strftime(month, sizeof(month), "%b", &d);
  snprintf(out, n, "%s %d %s", weekday, d.tm_mday, month);
}

// "Done", "3 days overdue" or "To do"; for an issue that's only opened here (Jira, GitHub:
// `tickable` false), "" instead of "To do": its own status says more, and comes with it.
void statusText(const model::Task &t, bool tickable, int64_t now_day, char *out, size_t n) {
  const int64_t due = dayNumber(t.due);
  const int64_t late = due == INT64_MIN || now_day == INT64_MIN ? 0 : now_day - due;
  if (t.done) snprintf(out, n, "Done");
  else if (late > 0) snprintf(out, n, "%d day%s overdue", int(late), late == 1 ? "" : "s");
  else snprintf(out, n, "%s", tickable ? "To do" : "");
}

// ---- actions

void tick(int i) {
  model::Tasks &k = model::tasks;
  if (i >= k.count || !pc::connected()) return;
  model::Task &t = k.list[i];
  if (!k.canTick(t)) return;  // a tracker's issue: changed there, not here
  t.done = !t.done;  // tick it now; the companion confirms
  k.open += t.done ? -1 : 1;
  if (t.due.length()) k.sched += t.done ? -1 : 1;
  k.version++;
  pc::setTaskDone(t.src.c_str(), t.list.c_str(), t.id.c_str(), t.done);
}

void onTick(lv_event_t *e) { tick(indexOf(e)); }

const model::Task *popTask() {
  const model::Tasks &k = model::tasks;
  for (int i = 0; i < k.count; i++) {
    const model::Task &t = k.list[i];
    if (t.id == pop_id && t.list == pop_list && t.src == pop_src) return &t;
  }
  return nullptr;
}

void closePopup() {
  pop_id = "";
  lv_obj_set_hidden(pop.scrim, true);
  lv_obj_set_hidden(pop.panel, true);
}

void showPopup() {
  const model::Task *t = popTask();
  if (!t) {  // gone: done or deleted somewhere else
    closePopup();
    return;
  }
  const model::TaskDetail &d = model::task_detail;
  const bool loaded = d.id == pop_id && d.list == pop_list && d.src == pop_src;
  setText(pop.title, loaded && d.title.length() ? d.title.c_str() : t->title.c_str());

  char day[24], status[32], meta[200];
  dueText(t->due, day, sizeof(day));
  const bool tickable = model::tasks.canTick(*t);
  statusText(*t, tickable, dayToday(), status, sizeof(status));
  const char *list = model::tasks.listName(t->src, t->list);
  snprintf(meta, sizeof(meta), "%s%s%s%s%s%s", list, list[0] ? "  ·  " : "", day[0] ? "Due " : "No due date", day,
           status[0] ? "  ·  " : "", status);
  setText(pop.meta, meta);
  setTextColor(pop.meta, strstr(status, "overdue") ? kUrgent : kMuted);

  // What the source says (a Jira issue's project, key, priority and status), once it's come.
  const bool source = loaded && d.meta.length() > 0;
  setHidden(pop.source, !source);
  if (source) setText(pop.source, d.meta.c_str());

  const bool notes = loaded && d.notes.length() > 0;
  setText(pop.notes, notes ? d.notes.c_str() : loaded ? "No notes." : "Loading…");
  setTextColor(pop.notes, notes ? kSoftText : kMuted);

  const bool link = loaded && d.found && d.link.length() > 0;
  setHidden(pop.open, !link);
  if (link) setText(pop.open_text, d.link.c_str());

  setHidden(pop.done, !tickable);
  setText(pop.done_text, t->done ? "Mark not done" : LV_SYMBOL_OK "  Mark done");
  setBgColor(pop.done, t->done ? kRaised : kAccent);
  setTextColor(pop.done_text, t->done ? kText : kOnAccent);
}

void onOpen(lv_event_t *e) {
  const int i = indexOf(e);
  const model::Tasks &k = model::tasks;
  if (i >= k.count || !pc::connected()) return;
  const model::Task &t = k.list[i];
  pop_src = t.src;
  pop_list = t.list;
  pop_id = t.id;
  pc::openTask(t.src.c_str(), t.list.c_str(), t.id.c_str());  // its notes come back as model::task_detail
  lv_obj_set_hidden(pop.scrim, false);
  lv_obj_set_hidden(pop.panel, false);
  lv_obj_move_foreground(pop.scrim);
  lv_obj_move_foreground(pop.panel);
  lv_obj_scroll_to_y(pop.body, 0, LV_ANIM_OFF);
  showPopup();
}

void onPopClose(lv_event_t *) { closePopup(); }

// Opens it on the PC (the issue's page, the note in Obsidian); the popup closes, as the PC's
// screen is where it continues.
void onPopOpen(lv_event_t *) {
  if (!popTask() || !pc::connected()) return;
  pc::openTaskLink(pop_src.c_str(), pop_list.c_str(), pop_id.c_str());
  closePopup();
}

void onPopDone(lv_event_t *) {
  const model::Task *t = popTask();
  if (t) tick(int(t - model::tasks.list));
  closePopup();
}

// Chip 0 is All tasks, 1 Scheduled, then the lists (chip i is list i - 2).
void onFilter(lv_event_t *e) {
  const int i = indexOf(e);
  const model::Tasks &k = model::tasks;
  filter_src = filter_id = "";
  if (i == 1) {
    view = kScheduled;
  } else if (i < 2 || i - 2 >= k.list_count || !k.lists[i - 2].show) {
    view = kAllTasks;
  } else {
    view = kOneList;
    filter_src = k.lists[i - 2].src;
    filter_id = k.lists[i - 2].id;
  }
  dirty = true;
  to_top = true;
}

void onRefresh(lv_event_t *) { pc::refreshTasks(); }

void showChoice(lv_obj_t *chip, bool on) {
  setBgColor(chip, on ? kAccent : kRaised);
  setTextColor(lv_obj_get_child(chip, 0), on ? kOnAccent : kText);
}

void showForm() {
  const model::Tasks &k = model::tasks;
  for (int i = 0; i < model::Tasks::kMaxLists; i++) {
    const bool used = i < k.list_count && addable(k.lists[i]);
    setHidden(form.list_chips[i], !used);
    if (used) setText(lv_obj_get_child(form.list_chips[i], 0), k.lists[i].name.c_str());
    showChoice(form.list_chips[i], i == form_list);
  }
  setHidden(form.chips, addableLists(k) < 2);  // one list: nothing to choose
  char date[12];
  dateOf(0, date, sizeof(date));
  for (int i = 0; i < kDues; i++) {
    showChoice(form.due_chips[i], i == form_due);
    if (i > 0) setHidden(form.due_chips[i], date[0] == '\0');  // no clock yet: no dates
  }
}

void onAdd(lv_event_t *) {
  const model::Tasks &k = model::tasks;
  if (!pc::connected() || k.status != "ok" || addableLists(k) == 0) return;
  // The list on show; from All tasks and Scheduled (or a tracker's list, which takes none) the
  // first list with a chip, due today from Scheduled so it shows there.
  form_list = 0;
  for (int i = k.list_count - 1; i >= 0; i--) {
    if (addable(k.lists[i])) form_list = i;
  }
  for (int i = 0; i < k.list_count; i++) {
    if (view == kOneList && k.lists[i].src == filter_src && k.lists[i].id == filter_id && addable(k.lists[i])) form_list = i;
  }
  char today[12];
  dateOf(0, today, sizeof(today));
  form_due = view == kScheduled && today[0] ? 1 : 0;
  lv_textarea_set_text(form.text, "");
  showForm();
  lv_obj_set_hidden(form.root, false);
  lv_obj_move_foreground(form.root);
}

void closeForm() { lv_obj_set_hidden(form.root, true); }

void onCancel(lv_event_t *) { closeForm(); }

void onListChip(lv_event_t *e) {
  form_list = indexOf(e);
  showForm();
}

void onDueChip(lv_event_t *e) {
  form_due = indexOf(e);
  showForm();
}

void onSave(lv_event_t *) {
  if (lv_obj_is_hidden(form.root)) return;
  String title = lv_textarea_get_text(form.text);
  title.trim();
  if (title.length() == 0) return;  // nothing typed: the form stays
  const model::Tasks &k = model::tasks;
  const model::TaskList *list = form_list < k.list_count ? &k.lists[form_list] : nullptr;
  char due[12] = "";
  if (form_due > 0) dateOf(form_due - 1, due, sizeof(due));
  pc::addTask(list ? list->src.c_str() : "", list ? list->id.c_str() : "", title.c_str(), due);
  closeForm();
}

// ---- building

lv_obj_t *chip(lv_obj_t *parent, const char *text, lv_event_cb_t cb, int i) {
  lv_obj_t *c = button(parent, LV_SIZE_CONTENT, 36, kRaised, cb, reinterpret_cast<void *>(intptr_t(i)));
  lv_obj_set_style_radius(c, 18, 0);
  lv_obj_set_style_pad_hor(c, 14, 0);
  lv_obj_t *l = label(c, text, &small_font, kText);
  lv_obj_set_style_max_width(l, 220, 0);
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_height(l, lv_font_get_line_height(&small_font));
  lv_obj_center(l);
  return c;
}

void buildForm() {
  lv_obj_t *o = form.root = box(lv_layer_top());
  lv_obj_set_size(o, LV_PCT(100), LV_PCT(100));
  lv_obj_set_style_bg_color(o, hex(kBg), 0);
  lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
  lv_obj_set_style_pad_all(o, kGap, 0);
  lv_obj_set_clickable(o, true);  // nothing under it takes taps
  flexCol(o, 8);

  lv_obj_t *head = box(o);
  lv_obj_set_size(head, LV_PCT(100), 40);
  flexRow(head, 10);
  label(head, LV_SYMBOL_PLUS "  New task", &title_font, kText);
  lv_obj_t *gap = box(head);
  lv_obj_set_size(gap, 1, 1);
  lv_obj_set_flex_grow(gap, 1);
  lv_obj_t *cancel = button(head, 110, 40, kRaised, onCancel);
  lv_obj_set_style_radius(cancel, 12, 0);
  lv_obj_center(label(cancel, "Cancel", &body_font, kText));
  form.save = button(head, 110, 40, kAccent, onSave);
  lv_obj_set_style_radius(form.save, 12, 0);
  lv_obj_center(label(form.save, LV_SYMBOL_OK "  Add", &body_font, kOnAccent));

  lv_obj_t *ta = form.text = lv_textarea_create(o);
  lv_obj_set_size(ta, LV_PCT(100), 50);
  lv_textarea_set_one_line(ta, true);
  lv_textarea_set_max_length(ta, 200);
  lv_textarea_set_placeholder_text(ta, "What needs doing?");
  lv_obj_set_style_text_font(ta, &body_font, 0);
  lv_obj_set_style_text_color(ta, hex(kText), 0);
  lv_obj_set_style_text_color(ta, hex(kMuted), LV_PART_TEXTAREA_PLACEHOLDER);
  lv_obj_set_style_bg_color(ta, hex(kSurface), 0);
  lv_obj_set_style_border_color(ta, hex(kAccent), 0);
  lv_obj_set_style_border_width(ta, 2, 0);
  lv_obj_set_style_radius(ta, 12, 0);
  lv_obj_set_style_pad_hor(ta, 14, 0);
  lv_obj_set_style_pad_ver(ta, 12, 0);
  lv_obj_set_style_bg_color(ta, hex(kAccent), LV_PART_CURSOR);
  lv_obj_set_style_border_color(ta, hex(kAccent), LV_PART_CURSOR);
  lv_obj_add_state(ta, LV_STATE_FOCUSED);  // shows the cursor

  // Which list (only with more than one on the deck), and when it's due.
  lv_obj_t *lists = form.chips = box(o);
  lv_obj_set_size(lists, LV_PCT(100), 36);
  flexRow(lists, 8);
  lv_obj_set_scrollable(lists, true);
  lv_obj_set_scroll_dir(lists, LV_DIR_HOR);
  lv_obj_set_scrollbar_mode(lists, LV_SCROLLBAR_MODE_OFF);
  label(lists, "List", &small_font, kMuted);
  for (int i = 0; i < model::Tasks::kMaxLists; i++) form.list_chips[i] = chip(lists, "", onListChip, i);

  lv_obj_t *dues = box(o);
  lv_obj_set_size(dues, LV_PCT(100), 36);
  flexRow(dues, 8);
  label(dues, "Due", &small_font, kMuted);
  const char *names[kDues] = {"No date", "Today", "Tomorrow"};
  for (int i = 0; i < kDues; i++) form.due_chips[i] = chip(dues, names[i], onDueChip, i);

  lv_obj_t *kb = form.keyboard = lv_keyboard_create(o);
  lv_obj_set_width(kb, LV_PCT(100));
  lv_obj_set_flex_grow(kb, 1);
  lv_keyboard_set_textarea(kb, ta);
  lv_obj_set_style_bg_color(kb, hex(kBg), 0);
  lv_obj_set_style_pad_all(kb, 0, 0);
  lv_obj_set_style_pad_gap(kb, 6, 0);
  lv_obj_set_style_border_width(kb, 0, 0);
  lv_obj_set_style_bg_color(kb, hex(kRaised), LV_PART_ITEMS);
  lv_obj_set_style_bg_color(kb, hex(kSelected), LV_PART_ITEMS | LV_STATE_CHECKED);
  lv_obj_set_style_bg_color(kb, hex(kAccent), LV_PART_ITEMS | LV_STATE_PRESSED);
  lv_obj_set_style_text_color(kb, hex(kText), LV_PART_ITEMS);
  lv_obj_set_style_text_font(kb, &lv_font_montserrat_20, LV_PART_ITEMS);
  lv_obj_set_style_border_width(kb, 0, LV_PART_ITEMS);
  lv_obj_set_style_shadow_width(kb, 0, LV_PART_ITEMS);
  lv_obj_set_style_radius(kb, 8, LV_PART_ITEMS);
  // Its ✓ and Enter keys both end up as the text area's READY; its keyboard key cancels.
  lv_obj_add_event_cb(ta, onSave, LV_EVENT_READY, nullptr);
  lv_obj_add_event_cb(kb, onCancel, LV_EVENT_CANCEL, nullptr);

  lv_obj_set_hidden(o, true);
}

// The open task: a panel in the middle, over a dimmed screen; a tap beside it closes it.
void buildPopup() {
  pop.scrim = box(lv_layer_top());
  lv_obj_set_size(pop.scrim, 800, 480);
  lv_obj_set_style_bg_color(pop.scrim, hex(0x000000), 0);
  lv_obj_set_style_bg_opa(pop.scrim, LV_OPA_50, 0);
  lv_obj_set_clickable(pop.scrim, true);
  lv_obj_add_event_cb(pop.scrim, onPopClose, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_hidden(pop.scrim, true);

  lv_obj_t *p = pop.panel = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_size(p, 600, LV_SIZE_CONTENT);
  lv_obj_set_style_max_height(p, 420, 0);
  lv_obj_align(p, LV_ALIGN_CENTER, kRailW / 2, 0);  // in the middle of the pages, not the screen
  lv_obj_set_style_radius(p, 18, 0);
  lv_obj_set_style_pad_all(p, 18, 0);
  lv_obj_set_style_shadow_width(p, 30, 0);
  lv_obj_set_style_shadow_color(p, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(p, LV_OPA_60, 0);
  lv_obj_set_clickable(p, true);  // a tap on it stays on it
  lv_obj_set_scrollable(p, false);
  flexCol(p, 10);

  // Everything but the buttons, scrolling as one when it's long (a long title, the source's
  // line, long notes), so the buttons are always on the panel: 420 high at most, less the
  // padding, a gap and the buttons' row.
  lv_obj_t *body = pop.body = box(p);
  lv_obj_set_size(body, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_max_height(body, 420 - 2 * 18 - 10 - 44, 0);
  lv_obj_set_clickable(body, true);  // so a drag on it scrolls it
  lv_obj_set_scrollable(body, true);
  lv_obj_set_scroll_dir(body, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(body, LV_SCROLLBAR_MODE_ACTIVE);
  flexCol(body, 10);

  pop.title = label(body, "", &title_font, kText);
  lv_obj_set_width(pop.title, LV_PCT(100));
  lv_label_set_long_mode(pop.title, LV_LABEL_LONG_MODE_WRAP);
  pop.meta = label(body, "", &small_font, kMuted);
  lv_obj_set_width(pop.meta, LV_PCT(100));
  lv_label_set_long_mode(pop.meta, LV_LABEL_LONG_MODE_WRAP);
  pop.source = label(body, "", &small_font, kSoftText);
  lv_obj_set_width(pop.source, LV_PCT(100));
  lv_label_set_long_mode(pop.source, LV_LABEL_LONG_MODE_WRAP);
  lv_obj_set_hidden(pop.source, true);
  pop.notes = label(body, "", &body_font, kSoftText);
  lv_obj_set_width(pop.notes, LV_PCT(100));
  lv_obj_set_style_pad_ver(pop.notes, 4, 0);
  lv_label_set_long_mode(pop.notes, LV_LABEL_LONG_MODE_WRAP);

  lv_obj_t *row = box(p);
  lv_obj_set_size(row, LV_PCT(100), 44);
  flexRow(row, 10);
  // "Open in Jira": an icon and the companion's words, as wide as they are.
  pop.open = button(row, LV_SIZE_CONTENT, 44, kRaised, onPopOpen);
  lv_obj_set_style_radius(pop.open, 12, 0);
  lv_obj_set_style_pad_hor(pop.open, 16, 0);
  flexRow(pop.open, 8);
  label(pop.open, glyphOf("external"), &font_icons_15, kAccent);
  pop.open_text = label(pop.open, "", &body_font, kText);
  lv_obj_set_style_max_width(pop.open_text, 220, 0);
  lv_label_set_long_mode(pop.open_text, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_height(pop.open_text, lv_font_get_line_height(&body_font));
  lv_obj_set_hidden(pop.open, true);
  lv_obj_t *gap = box(row);
  lv_obj_set_size(gap, 1, 1);
  lv_obj_set_flex_grow(gap, 1);
  lv_obj_t *close = button(row, 120, 44, kRaised, onPopClose);
  lv_obj_set_style_radius(close, 12, 0);
  lv_obj_center(label(close, "Close", &body_font, kText));
  pop.done = button(row, 190, 44, kAccent, onPopDone);
  lv_obj_set_style_radius(pop.done, 12, 0);
  pop.done_text = label(pop.done, "", &body_font, kOnAccent);
  lv_obj_center(pop.done_text);
  lv_obj_set_hidden(p, true);
}

// The card: a header (All tasks, Scheduled or the list on show, open count, refresh, +), All
// tasks, Scheduled and the chosen lists as chips, the tasks.
void buildCard(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_height(c, LV_PCT(100));
  lv_obj_set_flex_grow(c, 1);
  lv_obj_set_style_pad_all(c, 10, 0);
  lv_obj_set_style_pad_bottom(c, 4, 0);
  flexCol(c, 4);
  lv_obj_t *h = box(c);
  lv_obj_set_size(h, LV_PCT(100), 32);
  flexRow(h, 6);
  w.title = label(h, "ALL TASKS", &small_font, kMuted);
  fillRow(w.title);
  w.badge = card(h, kAmberBg, kAmberBg);
  lv_obj_set_size(w.badge, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  lv_obj_set_style_pad_hor(w.badge, 8, 0);
  lv_obj_set_style_pad_ver(w.badge, 2, 0);
  lv_obj_set_style_radius(w.badge, 10, 0);
  label(w.badge, "", &small_font, kUrgent);
  lv_obj_t *refresh = lv_obj_get_parent(iconButton(h, 36, 32, kRaised, LV_SYMBOL_REFRESH, kMuted, onRefresh));
  lv_obj_set_style_radius(refresh, 10, 0);
  w.add = lv_obj_get_parent(iconButton(h, 44, 32, kAccent, LV_SYMBOL_PLUS, kOnAccent, onAdd));
  lv_obj_set_style_radius(w.add, 10, 0);

  // All tasks, Scheduled and the lists, as chips that scroll sideways.
  lv_obj_t *chips = w.chips = box(c);
  lv_obj_set_size(chips, LV_PCT(100), 30);
  flexRow(chips, 6);
  lv_obj_set_clickable(chips, true);  // a swipe between chips scrolls the row too
  lv_obj_set_scrollable(chips, true);
  lv_obj_set_scroll_dir(chips, LV_DIR_HOR);
  lv_obj_set_scrollbar_mode(chips, LV_SCROLLBAR_MODE_OFF);
  for (int i = 0; i < kFilters; i++) {
    lv_obj_t *b = w.filters[i] = button(chips, LV_SIZE_CONTENT, 30, kRaised, onFilter, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(b, 15, 0);
    lv_obj_set_style_pad_hor(b, 12, 0);
    w.filter_name[i] = label(b, i == 0 ? "All tasks" : i == 1 ? "Scheduled" : "", &small_font, kText);
    lv_obj_set_style_max_width(w.filter_name[i], 160, 0);
    lv_label_set_long_mode(w.filter_name[i], LV_LABEL_LONG_MODE_DOTS);
    // One line: with its height free a long list name wrapped instead of ending in "…".
    lv_obj_set_height(w.filter_name[i], lv_font_get_line_height(&small_font));
    lv_obj_center(w.filter_name[i]);
  }

  w.note = label(c, "", &body_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);

  w.rows = box(c);
  lv_obj_set_width(w.rows, LV_PCT(100));
  lv_obj_set_flex_grow(w.rows, 1);
  lv_obj_set_clickable(w.rows, true);  // so a drag that starts on a heading or between rows scrolls it too
  flexCol(w.rows, 0);
  lv_obj_set_scrollable(w.rows, true);
  lv_obj_set_scroll_dir(w.rows, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(w.rows, LV_SCROLLBAR_MODE_ACTIVE);
}

// ---- updating

void addHeading(const char *text, uint32_t color) {
  lv_obj_t *l = label(w.rows, text, &small_font, color);
  lv_obj_set_style_pad_top(l, 8, 0);
  lv_obj_set_style_pad_bottom(l, 2, 0);
}

// `list_name`: the second line starts with the task's list (All tasks, with several lists on the deck).
void addRow(int i, Group group, int64_t now_day, bool list_name) {
  const model::Tasks &k = model::tasks;
  const model::Task &t = k.list[i];
  // The row opens the task; the box at its start (a 44 px target) ticks it.
  lv_obj_t *r = button(w.rows, LV_PCT(100), LV_SIZE_CONTENT, kSurface, onOpen, reinterpret_cast<void *>(intptr_t(i)));
  lv_obj_set_style_radius(r, 10, 0);
  lv_obj_set_style_min_height(r, 50, 0);
  lv_obj_set_style_pad_ver(r, 3, 0);
  lv_obj_set_style_pad_left(r, t.child ? 26 : 0, 0);
  lv_obj_set_style_pad_right(r, 6, 0);
  flexRow(r, 4);

  const bool tickable = k.canTick(t);
  if (tickable) {
    lv_obj_t *tick_button = button(r, 44, 44, kSurface, onTick, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(tick_button, 10, 0);
    lv_obj_t *mark = card(tick_button, t.done ? 0x4A525C : kSurface, t.done ? 0x4A525C : kBox);
    lv_obj_set_clickable(mark, false);  // taps go to the button
    lv_obj_set_size(mark, 24, 24);
    lv_obj_set_style_pad_all(mark, 0, 0);
    lv_obj_set_style_radius(mark, t.child ? 12 : 6, 0);
    lv_obj_set_style_border_width(mark, 2, 0);
    lv_obj_center(mark);
    if (t.done) lv_obj_center(label(mark, LV_SYMBOL_OK, &small_font, kText));
  } else {
    // A tracker's issue (Jira, GitHub): no box, since it's moved on there; a tap anywhere opens it.
    lv_obj_t *slot = box(r);
    lv_obj_set_size(slot, 44, 44);
    lv_obj_center(label(slot, glyphOf("external"), &font_icons_15, kMuted));
  }

  lv_obj_t *text = box(r);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  fillRow(text);
  flexCol(text, 0);
  lv_obj_t *title = label(text, t.title.c_str(), &body_font, t.done ? kMuted : kText);
  ellipsize(title);
  if (t.done) lv_obj_set_style_text_decor(title, LV_TEXT_DECOR_STRIKETHROUGH, 0);

  // Scheduled: the due day and where it stands, nothing else (an issue's own key, priority
  // and status, unless it's late). All tasks and one list: the list (All tasks, when there
  // are several), the day (where the heading doesn't say it) and the source's line (the
  // notes' first line; an issue's key, priority and status).
  char sub[200];
  size_t n = 0;
  sub[0] = '\0';
  auto add = [&](const char *part) {
    if (!part || !part[0] || n >= sizeof(sub) - 1) return;
    n += snprintf(sub + n, sizeof(sub) - n, "%s%s", n ? "  ·  " : "", part);
  };
  char day[24] = "";
  if (list_name) add(k.listName(t.src, t.list));
  if (view == kScheduled) {
    char status[32];
    dueText(t.due, day, sizeof(day));
    statusText(t, tickable, now_day, status, sizeof(status));
    add(day);
    add(status);
    if (!tickable && !status[0]) add(t.sub.c_str());
  } else {
    if (group == kOverdue || group == kThisWeek || group == kLater) {
      const time_t at = time_t(dayNumber(t.due) * 86400);
      struct tm d;
      gmtime_r(&at, &d);
      strftime(day, sizeof(day), group == kThisWeek ? "%a" : "%e %b", &d);
    }
    add(day[0] == ' ' ? day + 1 : day);
    add(t.sub.c_str());
  }
  if (n) {
    lv_obj_t *s = label(text, sub, &small_font, group == kOverdue ? kUrgent : kMuted);
    ellipsize(s);
  }
}

void updateFilters() {
  const model::Tasks &k = model::tasks;
  const bool ok = pc::connected() && k.status == "ok";
  setHidden(w.chips, !ok);
  for (int i = 0; i < kFilters; i++) {
    const model::TaskList *l = i >= 2 && i - 2 < k.list_count ? &k.lists[i - 2] : nullptr;
    const bool used = ok && (i < 2 || (l && l->show));
    setHidden(w.filters[i], !used);
    if (!used) continue;
    const bool selected = i == 0 ? view == kAllTasks
                        : i == 1 ? view == kScheduled
                                 : view == kOneList && l->src == filter_src && l->id == filter_id;
    setBgColor(w.filters[i], selected ? kAccent : kRaised);
    setTextColor(w.filter_name[i], selected ? kOnAccent : kText);
    int open = 0;
    if (i == 1) {
      open = k.sched;
    } else {
      for (int j = 0; j < k.count; j++) {
        const model::Task &t = k.list[j];
        if (t.done) continue;
        if (i == 0 ? listShown(k, t.src, t.list) : t.src == l->src && t.list == l->id) open++;
      }
    }
    const char *name = i == 0 ? "All tasks" : i == 1 ? "Scheduled" : l->name.c_str();
    if (open) setTextf(w.filter_name[i], "%s  %d", name, open);
    else setText(w.filter_name[i], name);
  }
  lv_obj_set_style_opa(w.add, ok && addableLists(k) > 0 ? LV_OPA_COVER : LV_OPA_40, 0);
}

void updateList() {
  const model::Tasks &k = model::tasks;
  const bool online = pc::connected();
  const bool ok = online && k.status == "ok";
  // A list that's gone (deleted in Google Tasks, or taken off the deck): back to All tasks.
  if (view == kOneList && ok && !listShown(k, filter_src, filter_id)) {
    view = kAllTasks;
    filter_src = filter_id = "";
  }
  if (view == kAllTasks) {
    setText(w.title, "ALL TASKS");
  } else if (view == kScheduled) {
    setText(w.title, "SCHEDULED");
  } else {
    String name = k.listName(filter_src, filter_id);
    name.toUpperCase();
    setText(w.title, name.c_str());
  }
  int open = 0;
  bool any = false;
  for (int i = 0; i < k.count; i++) {
    if (!shown(k.list[i])) continue;
    any = true;
    if (!k.list[i].done) open++;
  }
  if (view == kScheduled) open = k.sched;  // also the ones past what the deck holds
  setHidden(w.badge, !ok || open == 0);
  setTextf(lv_obj_get_child(w.badge, 0), "%d open", open);

  // Which source it's about, when the companion says ("Google Tasks").
  const char *from = k.from.length() ? k.from.c_str() : "your task source";
  const model::TaskList *list = view == kOneList ? k.listOf(filter_src, filter_id) : nullptr;
  char why[160];
  if (!online) snprintf(why, sizeof(why), "PC offline");
  else if (k.status == "setup") snprintf(why, sizeof(why), "Connect your tasks in the companion: tray icon, \"Task sources\".");
  else if (k.status == "signin") snprintf(why, sizeof(why), "Sign in to %s from the companion's tray menu, \"Task sources\".", from);
  else if (k.status == "error" && k.count == 0) snprintf(why, sizeof(why), "%s can't be reached right now.", k.from.length() ? from : "Your tasks");
  else if (k.status.length() == 0) snprintf(why, sizeof(why), "Reading your tasks…");
  else if (!any && view == kScheduled) snprintf(why, sizeof(why), "Nothing scheduled. Tap + to add one.");
  else if (!any && list && !list->add) snprintf(why, sizeof(why), "Nothing assigned to you.");
  else if (!any) snprintf(why, sizeof(why), "Nothing to do. Tap + to add one.");
  else why[0] = '\0';
  // The others read, but one source needs signing in again: said above their tasks, since
  // its own would otherwise just be missing.
  const bool warn = online && k.status == "ok" && k.warn.length() > 0;
  if (warn) snprintf(why, sizeof(why), "Sign in to %s again (tray menu).", k.warn.c_str());  // short: the card is narrow
  setTextColor(w.note, warn ? kUrgent : kMuted);
  setText(w.note, why);
  setHidden(w.note, why[0] == '\0');

  // Rebuilt whole: a few dozen rows, and only when something changed. Where it was scrolled
  // to stays, unless it's another chip's list now.
  const int32_t scroll = to_top ? 0 : lv_obj_get_scroll_y(w.rows);
  to_top = false;
  lv_obj_clean(w.rows);
  if (online && (k.status == "ok" || k.status == "error")) {
    const int64_t now_day = dayToday();
    const bool list_names = view == kAllTasks && shownLists(k) > 1;
    int last = -1;
    for (int i = 0; i < k.count; i++) {
      const model::Task &t = k.list[i];
      if (!shown(t)) continue;
      const Group g = groupOf(t, now_day);
      if (g != last) {
        addHeading(kGroupNames[g], g == kOverdue ? kUrgent : kMuted);
        last = g;
      }
      addRow(i, g, now_day, list_names);
    }
  }
  lv_obj_update_layout(w.rows);
  lv_obj_scroll_to_y(w.rows, scroll, LV_ANIM_OFF);
}

}  // namespace

void build(lv_obj_t *row) {
  buildCard(row);
  buildForm();
  buildPopup();
}

void update() {
  const int connected = pc::connected() ? 1 : 0;
  int64_t day = -1;
  if (millis() - tick_ms >= kTickMs) {
    tick_ms = millis();
    day = dayToday();
  }
  const bool new_day = day != -1 && day != day_seen;
  if (new_day) day_seen = day;
  if (pop_id.length() && (model::task_detail.version != pop_detail_seen || model::tasks.version != pop_tasks_seen)) {
    pop_detail_seen = model::task_detail.version;
    pop_tasks_seen = model::tasks.version;
    showPopup();
  }
  const int32_t height = lv_obj_get_height(lv_obj_get_parent(w.rows));
  if (!dirty && !new_day && connected == connected_seen && height == height_seen && model::tasks.version == tasks_seen) return;
  dirty = false;
  connected_seen = connected;
  height_seen = height;
  tasks_seen = model::tasks.version;
  updateFilters();
  updateList();
  if (!connected) {
    closeForm();
    closePopup();
  }
}

}  // namespace ui::tasks
