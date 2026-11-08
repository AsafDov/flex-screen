#include "page_today.h"

#include <time.h>

#include "icons.h"
#include "model.h"
#include "pc.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::today {
namespace {

// After the user lets go of the slider, ignore the PC's volume reports for this long so
// a report sent before the last drag step doesn't snap the knob back.
constexpr uint32_t kHoldAfterTouchMs = 800;
constexpr int kCapturePage = 4;
constexpr uint32_t kCalendarTickMs = 15000;  // "in 18 min"
constexpr uint32_t kIconRetryMs = 3000;      // asking again for a window icon the deck lacks

// A Recent tile: an icon (a window's own, else a glyph on a tinted square), what it is,
// and which one.
struct ActionTile {
  lv_obj_t *root, *badge, *image, *glyph, *name, *title;
  uint32_t asked_ms;
};

struct {
  lv_obj_t *clock;
  lv_obj_t *clock_secs;
  lv_obj_t *date;
  lv_obj_t *art_icon;
  lv_obj_t *art;
  lv_obj_t *title;
  lv_obj_t *artist;
  lv_obj_t *source;
  lv_obj_t *play_icon;
  lv_obj_t *vol_slider;
  lv_obj_t *vol_value;
  lv_obj_t *next_title, *next_time, *join;
  lv_obj_t *actions_row, *actions_note;
  ActionTile tiles[model::Actions::kMax];
  lv_obj_t *mail_text;
} w;

uint32_t google_seen = UINT32_MAX;
uint32_t actions_seen = UINT32_MAX, actions_icons_seen = UINT32_MAX, actions_tick_ms = 0;
int actions_link_seen = -1;
uint32_t calendar_tick_ms = 0;
int calendar_link_seen = -1;

uint32_t media_seen = UINT32_MAX;
uint32_t volume_seen = UINT32_MAX;
int connected_seen = -1;
int64_t clock_seen = -1;
uint32_t vol_sent_ms = 0;
uint32_t vol_touched_ms = 0;

void onPrev(lv_event_t *) { pc::media(pc::MediaCmd::Prev); }
void onToggle(lv_event_t *) { pc::media(pc::MediaCmd::Toggle); }
void onNext(lv_event_t *) { pc::media(pc::MediaCmd::Next); }

void onVolume(lv_event_t *e) {
  const int v = lv_slider_get_value(w.vol_slider);
  lv_label_set_text_fmt(w.vol_value, "%d", v);
  if (pc::connected()) previewVolume(v);
  vol_touched_ms = millis();
  volume_seen = UINT32_MAX;  // after the hold, the PC's level again, even if it didn't change
  // Rate-limit while dragging; always send where the finger lifted.
  const bool released = lv_event_get_code(e) == LV_EVENT_RELEASED;
  if (released || millis() - vol_sent_ms > 60) {
    pc::setVolume(v);
    vol_sent_ms = millis();
  }
}

void buildClock(lv_obj_t *parent) {
  lv_obj_t *c = card(parent);
  lv_obj_set_size(c, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(c, 2);
  lv_obj_t *hm = box(c);
  lv_obj_set_size(hm, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(hm, 4);
  lv_obj_set_flex_align(hm, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_END);
  w.clock = label(hm, "--:--", &lv_font_montserrat_48, kText);
  w.clock_secs = label(hm, "", &lv_font_montserrat_20, kMuted);
  lv_obj_set_style_pad_bottom(w.clock_secs, 6, 0);
  w.date = label(c, "Waiting for the PC", &lv_font_montserrat_16, kMuted);
}

void buildNowPlaying(lv_obj_t *parent) {
  lv_obj_t *np = card(parent);
  lv_obj_set_size(np, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(np, 8);

  lv_obj_t *info = box(np);
  lv_obj_set_size(info, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(info, 10);
  lv_obj_t *holder = card(info, kArtBg, kArtBg);
  lv_obj_set_size(holder, 48, 48);
  lv_obj_set_style_pad_all(holder, 0, 0);
  lv_obj_set_style_radius(holder, 8, 0);
  lv_obj_set_style_clip_corner(holder, true, 0);
  w.art_icon = label(holder, LV_SYMBOL_AUDIO, &lv_font_montserrat_20, 0x8CCBFF);
  lv_obj_center(w.art_icon);
  w.art = lv_image_create(holder);
  lv_obj_set_size(w.art, 48, 48);
  lv_image_set_inner_align(w.art, LV_IMAGE_ALIGN_STRETCH);
  lv_obj_set_hidden(w.art, true);

  lv_obj_t *text = box(info);
  lv_obj_set_flex_grow(text, 1);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  flexCol(text, 1);
  w.title = label(text, "Nothing playing", &title_font, kText);
  w.artist = label(text, "", &body_font, kMuted);
  w.source = label(text, "", &body_font, kMuted);
  for (lv_obj_t *l : {w.title, w.artist, w.source}) ellipsize(l);

  lv_obj_t *transport = box(np);
  lv_obj_set_size(transport, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(transport, 16);
  lv_obj_set_flex_align(transport, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  iconButton(transport, 48, 48, kSurface, LV_SYMBOL_PREV, kText, onPrev);
  lv_obj_t *play = button(transport, 56, 56, kText, onToggle);
  lv_obj_set_style_bg_color(play, hex(0xC9CED4), LV_STATE_PRESSED);
  w.play_icon = label(play, LV_SYMBOL_PLAY, &lv_font_montserrat_20, kBg);
  lv_obj_center(w.play_icon);
  iconButton(transport, 48, 48, kSurface, LV_SYMBOL_NEXT, kText, onNext);

  lv_obj_t *vol = box(np);
  lv_obj_set_size(vol, LV_PCT(100), 36);
  flexRow(vol, 10);
  label(vol, LV_SYMBOL_VOLUME_MAX, &lv_font_montserrat_16, kMuted);
  w.vol_slider = lv_slider_create(vol);
  lv_obj_set_flex_grow(w.vol_slider, 1);
  lv_obj_set_height(w.vol_slider, 8);
  lv_slider_set_range(w.vol_slider, 0, 100);
  lv_obj_set_style_bg_color(w.vol_slider, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(w.vol_slider, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(w.vol_slider, hex(kText), LV_PART_KNOB);
  lv_obj_set_style_pad_all(w.vol_slider, 6, LV_PART_KNOB);
  lv_obj_set_ext_click_area(w.vol_slider, 14);  // fingers are bigger than the 8 px track
  lv_obj_set_gesture_bubble(w.vol_slider, false);  // dragging it isn't a page swipe
  lv_obj_add_event_cb(w.vol_slider, onVolume, LV_EVENT_VALUE_CHANGED, nullptr);
  lv_obj_add_event_cb(w.vol_slider, onVolume, LV_EVENT_RELEASED, nullptr);
  w.vol_value = label(vol, "--", &lv_font_montserrat_16, kText);
  lv_obj_set_width(w.vol_value, 32);
  lv_obj_set_style_text_align(w.vol_value, LV_TEXT_ALIGN_RIGHT, 0);
}

void onJoin(lv_event_t *) {
  if (const model::Event *e = model::google.next(model::utcNow())) pc::joinMeeting(e->id.c_str());
}

void onMail(lv_event_t *) { showPage(kCapturePage); }

void onAction(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::actions.count) pc::runRecent(model::actions.list[i].id.c_str());
}

// The Recent card's tiles, five across, each the same width whether used or not.
void buildActions(lv_obj_t *right) {
  lv_obj_t *c = card(right);
  lv_obj_set_width(c, LV_PCT(100));
  lv_obj_set_flex_grow(c, 1);
  flexCol(c, 8);
  label(c, "RECENT", &lv_font_montserrat_16, kMuted);
  w.actions_note = label(c, "", &body_font, kMuted);
  lv_obj_set_width(w.actions_note, LV_PCT(100));
  lv_label_set_long_mode(w.actions_note, LV_LABEL_LONG_MODE_WRAP);

  w.actions_row = box(c);
  lv_obj_set_width(w.actions_row, LV_PCT(100));
  lv_obj_set_flex_grow(w.actions_row, 1);
  flexRow(w.actions_row, 6);
  lv_obj_set_flex_align(w.actions_row, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  const int line = lv_font_get_line_height(&small_font);
  for (int i = 0; i < model::Actions::kMax; i++) {
    ActionTile &t = w.tiles[i];
    t.asked_ms = 0;
    t.root = button(w.actions_row, 1, LV_PCT(100), kRaised, onAction, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_flex_grow(t.root, 1);
    lv_obj_set_style_radius(t.root, 12, 0);
    lv_obj_set_style_pad_all(t.root, 6, 0);
    lv_obj_set_style_pad_top(t.root, 10, 0);
    flexCol(t.root, 4);
    lv_obj_set_flex_align(t.root, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);

    t.badge = card(t.root, kSurface, kSurface);
    lv_obj_set_size(t.badge, icons::kSize + 4, icons::kSize + 4);
    lv_obj_set_style_pad_all(t.badge, 0, 0);
    lv_obj_set_style_radius(t.badge, 9, 0);
    t.image = lv_image_create(t.badge);
    lv_obj_set_size(t.image, icons::kSize, icons::kSize);
    lv_obj_center(t.image);
    t.glyph = label(t.badge, "", &lv_font_montserrat_16, kText);
    lv_obj_center(t.glyph);

    t.name = label(t.root, "", &small_font, kText);
    lv_obj_set_width(t.name, LV_PCT(100));
    lv_obj_set_style_text_align(t.name, LV_TEXT_ALIGN_CENTER, 0);
    lv_label_set_long_mode(t.name, LV_LABEL_LONG_MODE_DOTS);
    lv_obj_set_height(t.name, line);
    t.title = label(t.root, "", &small_font, kMuted);
    lv_obj_set_width(t.title, LV_PCT(100));
    lv_obj_set_style_text_align(t.title, LV_TEXT_ALIGN_CENTER, 0);
    lv_label_set_long_mode(t.title, LV_LABEL_LONG_MODE_DOTS);
    lv_obj_set_height(t.title, line * 3);  // up to three lines, then "…"
  }
}

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

void updateActions() {
  const model::Actions &a = model::actions;
  const bool online = pc::connected();
  const int count = online ? a.count : 0;
  setText(w.actions_note, !online ? "PC offline" : "What you do from the deck shows here: windows, notes, commands, meetings. Tap one to do it again.");
  setHidden(w.actions_note, count > 0);
  setHidden(w.actions_row, count == 0);
  for (int i = 0; i < model::Actions::kMax; i++) {
    ActionTile &t = w.tiles[i];
    const bool used = i < count;
    // An unused slot keeps its width, so the others stay where they are.
    if (lv_obj_is_clickable(t.root) != used) {
      lv_obj_set_style_opa(t.root, used ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
      lv_obj_set_clickable(t.root, used);
    }
    if (!used) continue;
    const model::RecentAction &x = a.list[i];
    setText(t.name, x.name.c_str());
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

void buildRight(lv_obj_t *parent) {
  lv_obj_t *right = box(parent);
  lv_obj_set_flex_grow(right, 1);
  lv_obj_set_height(right, LV_PCT(100));
  flexCol(right, kGap);

  lv_obj_t *next = card(right, kHighlight, kHighlightLine);
  lv_obj_set_size(next, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(next, 10);
  lv_obj_t *text = box(next);
  lv_obj_set_flex_grow(text, 1);
  lv_obj_set_height(text, LV_SIZE_CONTENT);
  flexCol(text, 4);
  label(text, "UP NEXT", &lv_font_montserrat_16, kAccent);
  w.next_title = label(text, "", &title_font, kText);
  ellipsize(w.next_title);
  w.next_time = label(text, "", &body_font, kHighlightText);
  ellipsize(w.next_time);
  w.join = button(next, 88, 44, kAccent, onJoin);
  lv_obj_set_style_radius(w.join, 12, 0);
  lv_obj_center(label(w.join, LV_SYMBOL_CALL "  Join", &body_font, kOnAccent));
  lv_obj_set_hidden(w.join, true);

  buildActions(right);

  lv_obj_t *mail = button(right, LV_PCT(100), 44, kSurface, onMail);
  lv_obj_set_style_bg_opa(mail, LV_OPA_COVER, 0);
  lv_obj_set_style_radius(mail, 12, 0);
  lv_obj_set_style_border_color(mail, hex(kBorder), 0);
  lv_obj_set_style_border_width(mail, 1, 0);
  lv_obj_set_style_pad_hor(mail, 12, 0);
  flexRow(mail, 10);
  label(mail, LV_SYMBOL_ENVELOPE, &lv_font_montserrat_16, kAmber);
  w.mail_text = label(mail, "", &body_font, kSoftText);
  lv_obj_set_flex_grow(w.mail_text, 1);
  label(mail, LV_SYMBOL_RIGHT, &lv_font_montserrat_16, kMuted);
}

// "14:05" in the PC's time zone.
void clockText(int64_t utc, char *out, size_t n) {
  const time_t t = time_t(utc + int64_t(model::clock.tz_offset_min) * 60);
  struct tm tm;
  gmtime_r(&t, &tm);
  strftime(out, n, "%H:%M", &tm);
}

int64_t localDay(int64_t utc) { return (utc + int64_t(model::clock.tz_offset_min) * 60) / 86400; }

void updateCalendar() {
  const model::Google &g = model::google;
  const bool online = pc::connected();
  const int64_t now = model::utcNow();
  const model::Event *next = online && g.status == "ok" && now ? g.next(now) : nullptr;
  setHidden(w.join, next == nullptr || !next->join);

  if (!online) {
    setText(w.next_title, "PC offline");
    setText(w.next_time, "");
  } else if (g.status == "setup" || g.status == "signin") {
    setText(w.next_title, "Connect Google Calendar");
    setText(w.next_time, "From the companion's tray icon menu");
  } else if (!next) {
    // No status yet: the companion hasn't reported (it said "Calendar unavailable" then).
    setText(w.next_title, g.status == "ok"   ? "Nothing else today"
                          : g.status.length() ? "Calendar unavailable"
                                              : "Checking the calendar…");
    setText(w.next_time, "");
  } else {
    setText(w.next_title, next->title.c_str());
    char from[8], to[8];
    clockText(next->start, from, sizeof(from));
    clockText(next->end, to, sizeof(to));
    const int64_t mins = (next->start - now + 59) / 60;
    const char *day = localDay(next->start) > localDay(now) ? "Tomorrow " : "";
    if (next->start <= now) setTextf(w.next_time, "Now · until %s", to);
    else if (mins < 60) setTextf(w.next_time, "%s-%s · in %d min", from, to, int(mins));
    else setTextf(w.next_time, "%s%s-%s", day, from, to);
  }

  if (!online) setText(w.mail_text, "Action items from mail");
  else if (g.status != "ok") setText(w.mail_text, "Action items from mail need Google");
  else if (g.open == 0) setText(w.mail_text, "No action items from mail");
  else setTextf(w.mail_text, "%d action item%s from mail", g.open, g.open == 1 ? "" : "s");
}

void updateClock() {
  const int64_t now = model::localNow();
  if (now == 0 || now == clock_seen) return;
  clock_seen = now;
  const time_t t = time_t(now);
  struct tm tm;
  gmtime_r(&t, &tm);  // already shifted to local time
  char buf[32];
  strftime(buf, sizeof(buf), "%H:%M", &tm);
  lv_label_set_text(w.clock, buf);
  strftime(buf, sizeof(buf), ":%S", &tm);
  lv_label_set_text(w.clock_secs, buf);
  strftime(buf, sizeof(buf), "%A, %e %B", &tm);
  lv_label_set_text(w.date, buf);
}

void updateMedia() {
  const int up = pc::connected() ? 1 : 0;
  if (up != connected_seen) {
    connected_seen = up;
    media_seen = UINT32_MAX;  // redraw from the model, or show the offline note
  }
  const model::Media &m = model::media;
  if (m.version == media_seen) return;
  media_seen = m.version;
  if (!up) {
    // Without the companion the buttons still work, as plain media keys.
    lv_label_set_text(w.title, "PC offline");
    lv_label_set_text(w.artist, "Buttons send media keys");
    lv_label_set_text(w.source, "");
    lv_label_set_text(w.play_icon, LV_SYMBOL_PLAY);
    showArt();  // the placeholder, as on Audio: the last track's art stayed beside "PC offline"
    return;
  }
  if (!m.present) {
    lv_label_set_text(w.title, "Nothing playing");
    lv_label_set_text(w.artist, "");
    lv_label_set_text(w.source, "");
  } else {
    lv_label_set_text(w.title, m.title.length() ? m.title.c_str() : "Unknown track");
    lv_label_set_text(w.artist, m.artist.c_str());
    lv_label_set_text(w.source, m.app.c_str());
  }
  lv_label_set_text(w.play_icon, m.playing ? LV_SYMBOL_PAUSE : LV_SYMBOL_PLAY);
  showArt();
}

void updateVolume() {
  const model::Volume &v = model::volume;
  if (v.version == volume_seen) return;
  if (lv_obj_has_state(w.vol_slider, LV_STATE_PRESSED) || millis() - vol_touched_ms < kHoldAfterTouchMs) {
    return;  // try again next loop
  }
  volume_seen = v.version;
  if (v.level < 0) {  // no playback device: not the last level it had
    lv_slider_set_value(w.vol_slider, 0, LV_ANIM_OFF);
    lv_label_set_text(w.vol_value, "--");
    return;
  }
  lv_slider_set_value(w.vol_slider, v.level, LV_ANIM_OFF);
  lv_label_set_text_fmt(w.vol_value, "%d", v.level);
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_flex_flow(page, LV_FLEX_FLOW_ROW);
  lv_obj_set_style_pad_column(page, kGap, 0);
  lv_obj_set_scrollable(page, false);

  lv_obj_t *left = box(page);
  lv_obj_set_size(left, 290, LV_PCT(100));
  flexCol(left, kGap);
  buildClock(left);
  buildNowPlaying(left);
  buildRight(page);
}

void update() {
  updateClock();
  updateMedia();
  updateVolume();
  const int link = pc::connected() ? 1 : 0;
  if (model::google.version != google_seen || link != calendar_link_seen || millis() - calendar_tick_ms >= kCalendarTickMs) {
    google_seen = model::google.version;
    calendar_link_seen = link;
    calendar_tick_ms = millis();
    updateCalendar();
  }
  // Icons arrive after the list, and a missing one is asked for again now and then.
  const bool icon_due = millis() - actions_tick_ms >= kIconRetryMs;
  if (model::actions.version != actions_seen || link != actions_link_seen || icons::version() != actions_icons_seen || icon_due) {
    actions_seen = model::actions.version;
    actions_link_seen = link;
    actions_icons_seen = icons::version();
    actions_tick_ms = millis();
    updateActions();
  }
}

void showArt() {
  const lv_image_dsc_t *art = pc::connected() && model::media.present ? artFor(model::media.art) : nullptr;
  if (art) lv_image_set_src(w.art, art);
  lv_obj_set_hidden(w.art, art == nullptr);
  lv_obj_set_hidden(w.art_icon, art != nullptr);
}

}  // namespace ui::today
