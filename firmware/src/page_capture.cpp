#include "page_capture.h"

#include <stdlib.h>
#include <time.h>

#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::capture {
namespace {

constexpr int kObsidianW = 218;
constexpr int kTaskRows = 5;
constexpr uint32_t kObsidian = 0xB48CFF;  // Obsidian's purple, from the design canvas
constexpr uint32_t kOnObsidian = 0x1A1030;
constexpr uint32_t kTickMs = 30000;  // ages ("4m") and due days
constexpr uint32_t kBox = 0x7D8794;

struct TaskRow {
  lv_obj_t *root, *check, *mark, *title, *sub;
};

struct ClipRow {
  lv_obj_t *root, *icon, *swatch, *text, *age;
};

struct {
  lv_obj_t *open_badge, *mail_note;
  TaskRow tasks[kTaskRows];
  ClipRow clips[model::Clips::kMax];
  lv_obj_t *clip_note, *clip_hint;
  lv_obj_t *vault_title, *obsidian_note;
  lv_obj_t *today;
  lv_obj_t *recent[model::Obsidian::kMaxRecent], *recent_text[model::Obsidian::kMaxRecent];
} w;

uint32_t google_seen = UINT32_MAX, clips_seen = UINT32_MAX, obsidian_seen = UINT32_MAX;
int connected_seen = -1;
uint32_t tick_ms = 0;

int indexOf(lv_event_t *e) { return int(intptr_t(lv_event_get_user_data(e))); }

// ---- actions

void onTask(lv_event_t *e) {
  const int i = indexOf(e);
  model::Google &g = model::google;
  if (i >= g.task_count) return;
  model::MailTask &t = g.tasks[i];
  t.done = !t.done;  // tick it now; Google confirms on the next refresh
  g.version++;
  pc::setTaskDone(t.list.c_str(), t.id.c_str(), t.done);
}

void onOpenMail(lv_event_t *) { pc::openMail(); }

void onClipTap(lv_event_t *e) {
  const int i = indexOf(e);
  if (i < model::clips.count) pc::pasteClip(model::clips.list[i].id.c_str());
}

void onClipHold(lv_event_t *e) {
  const int i = indexOf(e);
  if (i < model::clips.count) pc::pinClip(model::clips.list[i].id.c_str());
}

void onClear(lv_event_t *) { pc::clearClips(); }

void onObsidian(lv_event_t *e) { pc::obsidian(static_cast<const char *>(lv_event_get_user_data(e))); }

void onRecent(lv_event_t *e) {
  const int i = indexOf(e);
  if (i < model::obsidian.recent_count) pc::openNote(model::obsidian.recent[i].path.c_str());
}

// ---- building

lv_obj_t *header(lv_obj_t *parent, const char *text) {
  lv_obj_t *h = box(parent);
  lv_obj_set_size(h, LV_PCT(100), 26);
  flexRow(h, 6);
  lv_obj_set_flex_align(h, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  label(h, text, &small_font, kMuted);
  return h;
}

lv_obj_t *note(lv_obj_t *parent) {
  lv_obj_t *l = label(parent, "", &small_font, kMuted);
  lv_obj_set_width(l, LV_PCT(100));
  lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_WRAP);
  return l;
}

void buildMail(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_flex_grow(c, 1);
  lv_obj_set_height(c, LV_PCT(100));
  flexCol(c, 4);
  lv_obj_t *h = header(c, "FROM MAIL");
  w.open_badge = card(h, kAmberBg, kAmberBg);
  lv_obj_set_size(w.open_badge, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  lv_obj_set_style_pad_hor(w.open_badge, 8, 0);
  lv_obj_set_style_pad_ver(w.open_badge, 2, 0);
  lv_obj_set_style_radius(w.open_badge, 10, 0);
  label(w.open_badge, "", &small_font, 0xF5C36B);
  w.mail_note = note(c);

  for (int i = 0; i < kTaskRows; i++) {
    TaskRow &r = w.tasks[i];
    r.root = box(c);
    lv_obj_set_size(r.root, LV_PCT(100), 52);
    lv_obj_set_style_border_side(r.root, LV_BORDER_SIDE_TOP, 0);
    lv_obj_set_style_border_color(r.root, hex(kBorder), 0);
    lv_obj_set_style_border_width(r.root, 1, 0);
    flexRow(r.root, 6);
    r.check = button(r.root, 40, 40, kSurface, onTask, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(r.check, 10, 0);
    r.mark = card(r.check, kSurface, kBox);
    lv_obj_set_size(r.mark, 22, 22);
    lv_obj_set_style_pad_all(r.mark, 0, 0);
    lv_obj_set_style_radius(r.mark, 6, 0);
    lv_obj_set_style_border_width(r.mark, 2, 0);
    lv_obj_center(r.mark);
    lv_obj_center(label(r.mark, "", &small_font, kText));
    lv_obj_t *text = box(r.root);
    lv_obj_set_flex_grow(text, 1);
    lv_obj_set_height(text, LV_SIZE_CONTENT);
    flexCol(text, 0);
    r.title = label(text, "", &body_font, kText);
    ellipsize(r.title);
    r.sub = label(text, "", &small_font, kMuted);
    ellipsize(r.sub);
  }

  lv_obj_t *gap = box(c);
  lv_obj_set_flex_grow(gap, 1);
  lv_obj_t *open = button(c, LV_PCT(100), 40, kRaised, onOpenMail);
  lv_obj_set_style_radius(open, 12, 0);
  lv_obj_center(label(open, LV_SYMBOL_ENVELOPE "  Open Gmail", &small_font, kText));
}

void buildClipboard(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_flex_grow(c, 1);
  lv_obj_set_height(c, LV_PCT(100));
  flexCol(c, 6);
  // The hint sits at the bottom, away from Clear: tapping "TAP TO PASTE" up there hit Clear.
  lv_obj_t *h = header(c, "CLIPBOARD");
  lv_obj_t *clear = button(h, LV_SIZE_CONTENT, 26, kRaised, onClear);
  lv_obj_set_style_radius(clear, 8, 0);
  lv_obj_set_style_pad_hor(clear, 12, 0);
  lv_obj_center(label(clear, "Clear", &small_font, kMuted));
  w.clip_note = note(c);

  for (int i = 0; i < model::Clips::kMax; i++) {
    ClipRow &r = w.clips[i];
    r.root = button(c, LV_PCT(100), 40, kRaised, nullptr);
    lv_obj_set_style_radius(r.root, 10, 0);
    lv_obj_set_style_pad_hor(r.root, 10, 0);
    lv_obj_add_event_cb(r.root, onClipTap, LV_EVENT_SHORT_CLICKED, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_add_event_cb(r.root, onClipHold, LV_EVENT_LONG_PRESSED, reinterpret_cast<void *>(intptr_t(i)));
    flexRow(r.root, 8);
    r.icon = label(r.root, "", &small_font, kSoftText);
    r.swatch = box(r.root);
    lv_obj_set_size(r.swatch, 16, 16);
    lv_obj_set_style_radius(r.swatch, 4, 0);
    lv_obj_set_style_bg_opa(r.swatch, LV_OPA_COVER, 0);
    r.text = label(r.root, "", &small_font, kText);
    fillRow(r.text);
    r.age = label(r.root, "", &small_font, kMuted);
  }
  lv_obj_t *gap = box(c);
  lv_obj_set_flex_grow(gap, 1);
  w.clip_hint = label(c, "Tap to paste  ·  hold to pin", &small_font, kMuted);
  lv_obj_set_width(w.clip_hint, LV_PCT(100));
  lv_obj_set_style_text_align(w.clip_hint, LV_TEXT_ALIGN_CENTER, 0);
}

lv_obj_t *obsidianButton(lv_obj_t *parent, const char *text, const char *what, bool main) {
  lv_obj_t *b = button(parent, LV_PCT(100), main ? 56 : 42, main ? kObsidian : kRaised, onObsidian, const_cast<char *>(what));
  lv_obj_set_style_radius(b, 12, 0);
  lv_obj_center(label(b, text, main ? &body_font : &small_font, main ? kOnObsidian : kText));
  return b;
}

void buildObsidian(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_size(c, kObsidianW, LV_PCT(100));
  flexCol(c, 8);
  w.vault_title = label(header(c, ""), "", &small_font, kMuted);
  fillRow(w.vault_title);
  w.today = obsidianButton(c, LV_SYMBOL_FILE "  Today's note", "today", true);
  w.obsidian_note = note(c);
  label(c, "RECENT", &small_font, kMuted);
  for (int i = 0; i < model::Obsidian::kMaxRecent; i++) {
    lv_obj_t *b = button(c, LV_PCT(100), 34, kSurface, onRecent, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(b, 8, 0);
    lv_obj_set_style_pad_hor(b, 6, 0);
    flexRow(b, 6);
    label(b, LV_SYMBOL_FILE, &small_font, kObsidian);
    w.recent_text[i] = label(b, "", &small_font, kText);
    fillRow(w.recent_text[i]);
    w.recent[i] = b;
  }
}

// ---- updating

// "today", "tomorrow", "Thu", or "8 Nov" for a due date.
void dueText(const String &due, char *out, size_t n) {
  out[0] = '\0';
  const int64_t now = model::localNow();
  if (due.length() < 10 || now == 0) return;
  struct tm d = {};
  d.tm_year = atoi(due.c_str()) - 1900;
  d.tm_mon = atoi(due.c_str() + 5) - 1;
  d.tm_mday = atoi(due.c_str() + 8);
  const int64_t due_day = int64_t(mktime(&d)) / 86400;  // the deck's own zone is UTC: these are plain day numbers
  const int64_t today = now / 86400;
  const int64_t days = due_day - today;
  if (days < 0) snprintf(out, n, "overdue");
  else if (days == 0) snprintf(out, n, "due today");
  else if (days == 1) snprintf(out, n, "due tomorrow");
  else if (days < 7) strftime(out, n, "due %a", &d);
  else strftime(out, n, "due %e %b", &d);
}

void updateMail() {
  const model::Google &g = model::google;
  const bool online = pc::connected();
  const bool ok = online && g.status == "ok";
  setHidden(w.open_badge, !ok || g.open == 0);
  setTextf(lv_obj_get_child(w.open_badge, 0), "%d open", g.open);
  const char *why = !online                 ? "PC offline"
                    : g.status == "setup"   ? "Connect Google in the companion: tray icon, \"Connect Google Calendar and Tasks\"."
                    : g.status == "signin"  ? "Sign in to Google from the companion's tray menu."
                    : g.status == "error" && g.task_count == 0 ? "Google can't be reached right now."
                    : ok && g.task_count == 0 ? "Nothing from mail to do."
                                              : "";
  setText(w.mail_note, why);
  setHidden(w.mail_note, why[0] == '\0');
  for (int i = 0; i < kTaskRows; i++) {
    TaskRow &r = w.tasks[i];
    const bool used = online && i < g.task_count;
    setHidden(r.root, !used);
    if (!used) continue;
    const model::MailTask &t = g.tasks[i];
    setText(r.title, t.title.c_str());
    setTextColor(r.title, t.done ? kMuted : kText);
    char due[24];
    dueText(t.due, due, sizeof(due));
    const bool urgent = !t.done && (!strcmp(due, "overdue") || !strcmp(due, "due today"));
    if (t.done) setText(r.sub, "Done");
    else if (t.from.length() && due[0]) setTextf(r.sub, "%s · %s", t.from.c_str(), due);
    else setText(r.sub, t.from.length() ? t.from.c_str() : due);
    setTextColor(r.sub, urgent ? 0xF5C36B : kMuted);
    setBgColor(r.mark, t.done ? 0x4A525C : kSurface);
    setBorderColor(r.mark, t.done ? 0x4A525C : kBox);
    setText(lv_obj_get_child(r.mark, 0), t.done ? LV_SYMBOL_OK : "");
  }
}

const char *clipIcon(const String &kind) {
  if (kind == "link") return LV_SYMBOL_UPLOAD;
  if (kind == "code") return LV_SYMBOL_FILE;
  if (kind == "command") return LV_SYMBOL_KEYBOARD;
  return LV_SYMBOL_LIST;
}

void updateClips() {
  const model::Clips &c = model::clips;
  const bool online = pc::connected();
  setText(w.clip_note, !online ? "PC offline" : c.count == 0 ? "Copy some text and it shows up here." : "");
  setHidden(w.clip_note, online && c.count > 0);
  setHidden(w.clip_hint, !online || c.count == 0);
  for (int i = 0; i < model::Clips::kMax; i++) {
    ClipRow &r = w.clips[i];
    const bool used = online && i < c.count;
    setHidden(r.root, !used);
    if (!used) continue;
    const model::Clip &clip = c.list[i];
    const bool color = clip.kind == "color";
    setHidden(r.swatch, !color);
    setHidden(r.icon, color);
    if (color) {
      // "#5AB4FF" (or #RGB): show the color itself.
      const char *digits = clip.text.c_str() + 1;
      uint32_t v = strtoul(digits, nullptr, 16);
      if (strlen(digits) == 3) v = ((v >> 8 & 0xF) * 17) << 16 | ((v >> 4 & 0xF) * 17) << 8 | (v & 0xF) * 17;
      if (strlen(digits) == 8) v >>= 8;  // #RRGGBBAA
      setBgColor(r.swatch, v);
    } else {
      setText(r.icon, clip.pinned ? LV_SYMBOL_BULLET : clipIcon(clip.kind));
      setTextColor(r.icon, clip.pinned ? kAmber : kSoftText);
    }
    setText(r.text, clip.text.c_str());
    char age[16];
    const int32_t secs = model::agedSecs(clip.secs, c.at_ms);
    if (clip.pinned) snprintf(age, sizeof(age), "pinned");
    else if (secs < 60) snprintf(age, sizeof(age), "now");
    else if (secs < 3600) snprintf(age, sizeof(age), "%dm", int(secs / 60));
    else if (secs < 86400) snprintf(age, sizeof(age), "%dh", int(secs / 3600));
    else snprintf(age, sizeof(age), "%dd", int(secs / 86400));
    setText(r.age, age);
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
  setText(w.obsidian_note, !online ? "PC offline" : !o.found ? "No Obsidian vault found on the PC." : "");
  setHidden(w.obsidian_note, ok);
  lv_obj_set_style_opa(w.today, ok ? LV_OPA_COVER : LV_OPA_40, 0);
  lv_obj_set_clickable(w.today, ok);
  for (int i = 0; i < model::Obsidian::kMaxRecent; i++) {
    const bool used = ok && i < o.recent_count;
    setHidden(w.recent[i], !used);
    if (used) setText(w.recent_text[i], o.recent[i].name.c_str());
  }
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  flexRow(page, kGap);
  lv_obj_set_flex_align(page, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  buildMail(page);
  buildClipboard(page);
  buildObsidian(page);
  updateMail();
  updateClips();
  updateObsidian();
}

void update() {
  const int connected = pc::connected() ? 1 : 0;
  const bool tick = millis() - tick_ms >= kTickMs;
  if (tick) tick_ms = millis();
  const bool link = connected != connected_seen;
  connected_seen = connected;
  if (link || tick || model::google.version != google_seen) {
    google_seen = model::google.version;
    updateMail();
  }
  if (link || tick || model::clips.version != clips_seen) {
    clips_seen = model::clips.version;
    updateClips();
  }
  if (link || model::obsidian.version != obsidian_seen) {
    obsidian_seen = model::obsidian.version;
    updateObsidian();
  }
}

}  // namespace ui::capture
