#include "ui.h"

#include <time.h>

#include "display.h"
#include <Preferences.h>

#include "esp_heap_caps.h"
#include "call.h"
#include "cheat.h"
#include "diag.h"
#include "glow.h"
#include "idle.h"
#include "approval.h"
#include "ask.h"
#include "sessions.h"
#include "ctxbar.h"
#include "launcher.h"

#include "model.h"
#include "page_audio.h"
#include "page_capture.h"
#include "page_dev.h"
#include "page_numpad.h"
#include "page_today.h"
#include "dayview.h"
#include "page_windows.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui {
namespace {

struct {
  lv_obj_t *time;
  lv_obj_t *date;
  lv_obj_t *pc_dot;
  lv_obj_t *pc_text;
  lv_obj_t *vol_button;
  lv_obj_t *vol;
  lv_obj_t *mic;
  lv_obj_t *mic_text;
  MicIcon mic_icon;
  lv_obj_t *claude, *claude_dot, *claude_text;
  lv_obj_t *build, *build_text;
  lv_obj_t *layer, *layer_text;
  lv_obj_t *pc;
  lv_obj_t *meeting, *meeting_text;
  lv_obj_t *pills;  // everything between the date and the volume button
} bar;

// The bar's pills share its width with the volume and mic buttons, which must stay on
// screen: in the simulator a long meeting, Claude needing approval, a failed build and the
// layer pushed both off it. Which pills want showing; fitBar() decides what fits.
bool want_build = false, want_layer = false;
String meeting_label;
bool bar_changed = true;

struct {
  lv_obj_t *root, *icon, *text;
} toast;

// A firmware update in progress, over everything.
struct {
  lv_obj_t *root, *title, *bar, *detail, *note;
} flashing;
uint32_t flashing_seen = 0;
int flashing_percent = -1;

lv_obj_t *tabview;
constexpr uint32_t kDevTab = kDevPage;
constexpr uint32_t kToastMs = 4000;

constexpr uint32_t kPreviewHoldMs = 800;
// A second tap this soon on a toggle is a bounce, not a change of mind: two quick
// toggles cancel out and look like the button did nothing.
constexpr uint32_t kToggleGuardMs = 300;

uint32_t volume_seen = UINT32_MAX;
uint32_t mic_seen = UINT32_MAX;
uint32_t preview_ms = 0;
uint32_t art_seen = 0;
int connected_seen = -1;
int64_t clock_seen = -1;
uint32_t mute_tap_ms = 0;
uint32_t mic_tap_ms = 0;
// A tap or drag shows its result before the PC confirms it. If no report comes (the PC
// didn't change, or the message was lost), the bar went on showing the guess for good
// (seen in the simulator); this long after, it shows the PC's last word again.
constexpr uint32_t kRecheckMs = 1500;
uint32_t mic_recheck_ms = 0;
uint32_t claude_seen = UINT32_MAX;
uint32_t build_seen = UINT32_MAX;
uint32_t layer_seen = UINT32_MAX;
uint32_t meeting_tick_ms = 0;
uint32_t meeting_seen = UINT32_MAX;
constexpr uint32_t kMeetingTickMs = 15000;
constexpr int64_t kMeetingAheadSecs = 12 * 3600;  // the pill shows meetings starting within this
uint32_t toast_seen = 0;
uint32_t toast_ms = 0;
uint32_t bench_seen = 0;

bool acceptTap(uint32_t &last_ms) {
  if (millis() - last_ms < kToggleGuardMs) return false;
  last_ms = millis();
  return true;
}

void showMic(bool muted);

void onMute(lv_event_t *) {
  if (acceptTap(mute_tap_ms)) pc::toggleMute();
}

void onMic(lv_event_t *) {
  if (!acceptTap(mic_tap_ms)) return;
  showMic(!model::mic.muted);  // flip now; the PC's report follows within milliseconds
  mic_recheck_ms = millis() + kRecheckMs;  // and if none does, show what it last said
  pc::toggleMic();
}

void onShowDev(lv_event_t *) { lv_tabview_set_active(tabview, kDevTab, LV_ANIM_OFF); }

// A swipe changes page at once: sliding redrew the whole page every frame, which this
// panel can't do smoothly.
void onSwipe(lv_event_t *) {
  lv_indev_t *indev = lv_indev_active();
  if (!indev) return;
  const lv_dir_t dir = lv_indev_get_gesture_dir(indev);
  const uint32_t tab = lv_tabview_get_tab_active(tabview);
  if (dir == LV_DIR_LEFT && tab + 1 < lv_tabview_get_tab_count(tabview)) {
    lv_tabview_set_active(tabview, tab + 1, LV_ANIM_OFF);
  } else if (dir == LV_DIR_RIGHT && tab > 0) {
    lv_tabview_set_active(tabview, tab - 1, LV_ANIM_OFF);
  } else {
    return;
  }
  lv_indev_wait_release(indev);  // the swipe isn't also a tap on what it started on
  pc::releaseAllKeys();          // nor a numpad key left held down
}

// A toast with an action (Claude waiting for approval) runs it; any toast goes away.
void onToast(lv_event_t *) {
  if (model::toast.action.length()) pc::runAction(model::toast.action.c_str(), model::toast.id.c_str());
  lv_obj_set_hidden(toast.root, true);
}

// The Claude pill brings up the most urgent session's window.
void onClaudePill(lv_event_t *) { sessions::open(); }

// Volume and mic are buttons, not pills: raised so they read as tappable, and with a few
// px of extra hit area since the bar's edge is easy to miss.
lv_obj_t *barButton(lv_obj_t *parent, lv_event_cb_t cb) {
  lv_obj_t *b = button(parent, LV_SIZE_CONTENT, 34, kRaised, cb);
  lv_obj_set_style_radius(b, 10, 0);
  lv_obj_set_style_pad_hor(b, 10, 0);
  lv_obj_set_ext_click_area(b, 5);
  flexRow(b, 6);
  return b;
}

void buildStatusBar(lv_obj_t *parent) {
  lv_obj_t *b = box(parent);
  lv_obj_set_size(b, LV_PCT(100), kBarH);
  lv_obj_set_style_pad_hor(b, 12, 0);
  lv_obj_set_style_border_side(b, LV_BORDER_SIDE_BOTTOM, 0);
  lv_obj_set_style_border_color(b, hex(0x22272E), 0);
  lv_obj_set_style_border_width(b, 1, 0);
  flexRow(b, 8);

  bar.time = label(b, "--:--", &lv_font_montserrat_20, kText);
  bar.date = label(b, "", &lv_font_montserrat_16, kMuted);
  lv_obj_set_style_margin_right(bar.date, 4, 0);
  // Holding the clock (or the date) opens the deck's diagnostics; a tap does nothing.
  for (lv_obj_t *clock : {bar.time, bar.date}) {
    lv_obj_set_clickable(clock, true);
    lv_obj_set_ext_click_area(clock, 6);
    lv_obj_add_event_cb(clock, [](lv_event_t *) { diag::open(); }, LV_EVENT_LONG_PRESSED, nullptr);
  }

  // The pills take what's left and are cut off at its edge, never the buttons.
  b = bar.pills = box(b);
  lv_obj_set_size(b, 1, LV_SIZE_CONTENT);
  lv_obj_set_flex_grow(b, 1);
  lv_obj_set_scrollable(b, false);
  flexRow(b, 6);  // at 8 the layer pill missed by 5 px beside a meeting, Claude and a build

  lv_obj_t *pc = bar.pc = pill(b);
  bar.pc_dot = box(pc);
  lv_obj_set_size(bar.pc_dot, 8, 8);
  lv_obj_set_style_radius(bar.pc_dot, 4, 0);
  lv_obj_set_style_bg_opa(bar.pc_dot, LV_OPA_COVER, 0);
  bar.pc_text = label(pc, "PC offline", &lv_font_montserrat_16, kMuted);

  call::buildPill(b);

  // The next meeting today; tapping it opens Today.
  bar.meeting = pill(b);
  lv_obj_set_clickable(bar.meeting, true);
  lv_obj_add_event_cb(bar.meeting, [](lv_event_t *) { showPage(0); }, LV_EVENT_CLICKED, nullptr);
  label(bar.meeting, LV_SYMBOL_BELL, &lv_font_montserrat_16, kAccent);
  bar.meeting_text = label(bar.meeting, "", &body_font, kText);
  lv_obj_set_height(bar.meeting_text, lv_font_get_line_height(&body_font));
  lv_obj_set_hidden(bar.meeting, true);

  // Claude and build status: Claude brings up its window, the build opens the Dev page.
  bar.claude = pill(b);
  lv_obj_set_clickable(bar.claude, true);
  lv_obj_add_event_cb(bar.claude, onClaudePill, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_ext_click_area(bar.claude, 5);
  bar.claude_dot = box(bar.claude);
  lv_obj_set_size(bar.claude_dot, 8, 8);
  lv_obj_set_style_radius(bar.claude_dot, 4, 0);
  lv_obj_set_style_bg_opa(bar.claude_dot, LV_OPA_COVER, 0);
  bar.claude_text = label(bar.claude, "", &lv_font_montserrat_16, kText);
  lv_obj_set_hidden(bar.claude, true);

  bar.build = pill(b);
  lv_obj_set_clickable(bar.build, true);
  lv_obj_add_event_cb(bar.build, onShowDev, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_ext_click_area(bar.build, 5);
  bar.build_text = label(bar.build, "", &lv_font_montserrat_16, kText);
  lv_obj_set_hidden(bar.build, true);

  // The Corne's layer, from the companion; hidden without the keyboard.
  bar.layer = pill(b);
  label(bar.layer, LV_SYMBOL_KEYBOARD, &lv_font_montserrat_16, kMuted);
  bar.layer_text = label(bar.layer, "", &lv_font_montserrat_16, kText);
  lv_obj_set_hidden(bar.layer, true);
  lv_obj_set_clickable(bar.layer, true);  // opens the layer cheat sheet
  lv_obj_add_event_cb(bar.layer, [](lv_event_t *) { cheat::toggle(); }, LV_EVENT_CLICKED, nullptr);

  b = lv_obj_get_parent(bar.pills);
  bar.vol_button = barButton(b, onMute);
  bar.vol = label(bar.vol_button, LV_SYMBOL_VOLUME_MAX "  --", &lv_font_montserrat_16, kText);

  bar.mic = barButton(b, onMic);
  bar.mic_icon = micIcon(bar.mic);
  bar.mic_text = label(bar.mic, "Muted", &lv_font_montserrat_16, kAmber);
  lv_obj_set_hidden(bar.mic, true);  // until the companion reports a microphone
}

// Finished builds, git operations and Claude approvals pop up over every page for a few seconds.
void buildToast() {
  toast.root = card(lv_layer_top(), kRaised, 0x323A45);
  lv_obj_set_size(toast.root, LV_SIZE_CONTENT, 52);
  lv_obj_set_style_max_width(toast.root, 640, 0);
  lv_obj_set_style_radius(toast.root, 14, 0);
  lv_obj_set_style_pad_hor(toast.root, 16, 0);
  lv_obj_set_style_pad_ver(toast.root, 0, 0);
  lv_obj_set_style_shadow_width(toast.root, 24, 0);
  lv_obj_set_style_shadow_color(toast.root, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(toast.root, LV_OPA_50, 0);
  flexRow(toast.root, 10);
  lv_obj_align(toast.root, LV_ALIGN_BOTTOM_MID, 0, -(kBarH + kGap));
  lv_obj_set_clickable(toast.root, true);
  lv_obj_add_event_cb(toast.root, onToast, LV_EVENT_CLICKED, nullptr);
  toast.icon = label(toast.root, "", &lv_font_montserrat_20, kGreen);
  toast.text = label(toast.root, "", &body_font, kText);
  lv_obj_set_height(toast.text, lv_font_get_line_height(&body_font));
  lv_obj_set_hidden(toast.root, true);
}

// The screen stalls while flash is written and the deck restarts at the end, so an update
// covers everything (the idle clock too) and says what's going on. Taps stop here.
void buildFlashing() {
  flashing.root = box(lv_layer_top());
  lv_obj_set_size(flashing.root, LV_PCT(100), LV_PCT(100));
  lv_obj_set_style_bg_color(flashing.root, hex(kBg), 0);
  lv_obj_set_style_bg_opa(flashing.root, LV_OPA_COVER, 0);
  lv_obj_set_clickable(flashing.root, true);
  flexCol(flashing.root, 14);
  lv_obj_set_flex_align(flashing.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  label(flashing.root, LV_SYMBOL_DOWNLOAD, &lv_font_montserrat_28, kAccent);
  flashing.title = label(flashing.root, "Updating the deck", &title_font, kText);
  flashing.bar = lv_bar_create(flashing.root);
  lv_obj_set_clickable(flashing.bar, false);
  lv_obj_set_size(flashing.bar, 420, 10);
  lv_obj_set_style_bg_color(flashing.bar, hex(kTrack), 0);
  lv_obj_set_style_bg_opa(flashing.bar, LV_OPA_COVER, 0);  // the theme draws the track faint, too faint on the page background
  lv_obj_set_style_bg_color(flashing.bar, hex(kAccent), LV_PART_INDICATOR);
  lv_bar_set_range(flashing.bar, 0, 100);
  flashing.detail = label(flashing.root, "", &body_font, kSoftText);
  flashing.note = label(flashing.root, "Keep it plugged in. It restarts by itself when it's done.", &small_font, kMuted);
  lv_obj_set_hidden(flashing.root, true);
}

void updateFlashing() {
  const model::FirmwareUpdate &u = model::firmware_update;
  if (u.version == flashing_seen) return;
  flashing_seen = u.version;
  setHidden(flashing.root, !u.active);
  if (!u.active) {
    flashing_percent = -1;
    return;
  }
  const int percent = u.total ? int(uint64_t(u.done) * 100 / u.total) : 0;
  if (percent == flashing_percent && !u.restarting && !u.installing) return;  // the text only moves a percent at a time
  flashing_percent = percent;
  lv_bar_set_value(flashing.bar, u.restarting || u.installing ? 100 : percent, LV_ANIM_OFF);
  if (u.restarting) setText(flashing.detail, "Done. Restarting…");
  else if (u.installing) setText(flashing.detail, "Installing · the screen goes dark for a few seconds");
  else if (u.to.length()) setTextf(flashing.detail, "Firmware %s  ·  %d%%", u.to.c_str(), percent);
  else setTextf(flashing.detail, "New firmware  ·  %d%%", percent);
}

void styleTabs(lv_obj_t *tabview) {
  lv_obj_t *tabs = lv_tabview_get_tab_bar(tabview);
  lv_obj_set_style_bg_color(tabs, hex(kBg), 0);
  lv_obj_set_style_border_side(tabs, LV_BORDER_SIDE_TOP, 0);
  lv_obj_set_style_border_color(tabs, hex(0x22272E), 0);
  lv_obj_set_style_border_width(tabs, 1, 0);
  lv_obj_set_style_pad_hor(tabs, 12, 0);
  lv_obj_set_style_pad_ver(tabs, 3, 0);
  lv_obj_set_style_pad_column(tabs, 6, 0);
  const uint32_t n = lv_obj_get_child_count(tabs);
  for (uint32_t i = 0; i < n; i++) {
    lv_obj_t *t = lv_obj_get_child(tabs, i);
    lv_obj_set_style_radius(t, 10, 0);
    lv_obj_set_style_text_font(t, &lv_font_montserrat_16, 0);
    lv_obj_set_style_text_color(t, hex(kMuted), 0);
    lv_obj_set_style_bg_opa(t, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(t, 0, LV_STATE_CHECKED);
    lv_obj_set_style_text_color(t, hex(kText), LV_STATE_CHECKED);
    lv_obj_set_style_bg_color(t, hex(kSelected), LV_STATE_CHECKED);
    lv_obj_set_style_bg_opa(t, LV_OPA_COVER, LV_STATE_CHECKED);
  }
}

void updateClock() {
  const int64_t now = model::localNow();
  if (now == 0 || now / 60 == clock_seen) return;  // the bar shows minutes
  clock_seen = now / 60;
  const time_t t = time_t(now);
  struct tm tm;
  gmtime_r(&t, &tm);  // already shifted to local time
  char buf[32];
  strftime(buf, sizeof(buf), "%H:%M", &tm);
  lv_label_set_text(bar.time, buf);
  strftime(buf, sizeof(buf), "%a %e %b", &tm);
  lv_label_set_text(bar.date, buf);
}

void updateCompanion() {
  const int up = pc::connected() ? 1 : 0;
  if (up == connected_seen) return;
  connected_seen = up;
  lv_obj_set_hidden(bar.pc, up);  // only worth the room when the PC is gone
  bar_changed = true;
  lv_obj_set_style_bg_color(bar.pc_dot, hex(up ? kGreen : 0x5A626C), 0);
  lv_label_set_text(bar.pc_text, up ? "PC" : "PC offline");
  lv_obj_set_style_text_color(bar.pc_text, hex(up ? kText : kMuted), 0);
  mic_seen = UINT32_MAX;  // the mic button only makes sense with the companion
}

void updateVolume() {
  const model::Volume &v = model::volume;
  if (v.version == volume_seen) return;
  // Mid-drag, the PC's reports trail the finger; let the preview stand until it settles.
  if (millis() - preview_ms < kPreviewHoldMs) return;
  volume_seen = v.version;
  if (v.level < 0) {  // no playback device: neither a level nor muted (it said "Muted" before)
    lv_label_set_text(bar.vol, LV_SYMBOL_VOLUME_MAX "  --");
    lv_obj_set_style_text_color(bar.vol, hex(kText), 0);
    lv_obj_set_style_bg_color(bar.vol_button, hex(kRaised), 0);
    return;
  }
  if (v.muted) {
    lv_label_set_text(bar.vol, LV_SYMBOL_MUTE "  Muted");
  } else {
    lv_label_set_text_fmt(bar.vol, LV_SYMBOL_VOLUME_MAX "  %d%%", v.level);
  }
  lv_obj_set_style_text_color(bar.vol, hex(v.muted ? kAmber : kText), 0);
  lv_obj_set_style_bg_color(bar.vol_button, hex(v.muted ? kAmberBg : kRaised), 0);
}

void showMic(bool muted) {
  setMicIcon(bar.mic_icon, muted ? kAmber : kText, muted);
  lv_obj_set_hidden(bar.mic_text, !muted);
  lv_obj_set_style_bg_color(bar.mic, hex(muted ? kAmberBg : kRaised), 0);
}

void updateMic() {
  const model::Mic &m = model::mic;
  if (mic_recheck_ms && int32_t(millis() - mic_recheck_ms) >= 0) {
    mic_recheck_ms = 0;
    mic_seen = UINT32_MAX;
  }
  if (m.version == mic_seen) return;
  mic_seen = m.version;
  lv_obj_set_hidden(bar.mic, !(pc::connected() && m.available));
  showMic(m.muted);
}

void updateClaude() {
  const uint32_t v = model::claude.version + (pc::connected() ? 0 : 0x80000000u);
  if (v == claude_seen) return;
  claude_seen = v;
  const bool show = pc::connected() && model::claude.count > 0;
  lv_obj_set_hidden(bar.claude, !show);
  bar_changed = true;
  if (!show) return;
  const String &state = model::claude.sessions[0].state;
  uint32_t color = kMuted, bg = kSurface;
  const char *text = "Claude ready";
  if (state == "input") color = kAmber, bg = kAmberBg, text = "Claude needs you";
  else if (state == "working") color = kAccent, text = "Claude working";
  else if (state == "done") color = kGreen, text = "Claude done";
  // A long job's progress, while Claude works on it.
  static char counted[32];
  const model::ClaudeSession &top = model::claude.sessions[0];
  if (top.total > 0 && state == "working") {
    snprintf(counted, sizeof(counted), "Claude %d/%d", top.done, top.total);
    text = counted;
  }
  lv_obj_set_style_bg_color(bar.claude_dot, hex(color), 0);
  lv_obj_set_style_bg_color(bar.claude, hex(bg), 0);
  lv_label_set_text(bar.claude_text, text);
  lv_obj_set_style_text_color(bar.claude_text, hex(state == "input" ? kAmber : kText), 0);
}

void updateBuild() {
  const uint32_t v = model::build.version + (pc::connected() ? 0 : 0x80000000u);
  if (v == build_seen) return;
  build_seen = v;
  const String &state = model::build.state;
  const bool show = pc::connected() && state != "none";
  want_build = show;
  bar_changed = true;
  if (!show) return;
  if (state == "running") {
    lv_label_set_text(bar.build_text, LV_SYMBOL_REFRESH "  Building");
    lv_obj_set_style_text_color(bar.build_text, hex(kAccent), 0);
  } else if (state == "failed") {
    lv_label_set_text(bar.build_text, LV_SYMBOL_CLOSE "  Build failed");
    lv_obj_set_style_text_color(bar.build_text, hex(0xFF9A70), 0);
  } else {
    lv_label_set_text(bar.build_text, LV_SYMBOL_OK "  Build passed");
    lv_obj_set_style_text_color(bar.build_text, hex(kGreen), 0);
  }
}

void updateLayer() {
  const uint32_t v = model::keyboard.version + (pc::connected() ? 0 : 0x80000000u);
  if (v == layer_seen) return;
  layer_seen = v;
  const bool show = pc::connected() && model::keyboard.present;
  want_layer = show;
  bar_changed = true;
  if (!show) return;
  // Off the base layer it stands out, so a stuck toggle layer gets noticed.
  const bool base = model::keyboard.layer == 0;
  setText(bar.layer_text, model::keyboard.label.c_str());
  setTextColor(bar.layer_text, base ? kText : kOnAccent);
  setTextColor(lv_obj_get_child(bar.layer, 0), base ? kMuted : kOnAccent);
  setBgColor(bar.layer, base ? kSurface : kAccent);
}

void updateMeeting() {
  // The link going up or down, or a call starting or ending, counts as a change: the pill
  // stayed for up to 15 s offline, and beside the call's pill.
  const uint32_t v = model::google.version + (pc::connected() ? 0 : 0x80000000u) + (model::call.app.length() ? 0x40000000u : 0);
  if (v == meeting_seen && millis() - meeting_tick_ms < kMeetingTickMs) return;
  meeting_seen = v;
  meeting_tick_ms = millis();
  const int64_t now = model::utcNow();
  const model::Event *e = pc::connected() && model::google.status == "ok" && now ? model::google.next(now) : nullptr;
  // In a call the call pill says enough.
  const bool show = e && e->start - now < kMeetingAheadSecs && model::call.app.length() == 0;
  lv_obj_set_hidden(bar.meeting, !show);
  lv_obj_set_hidden(bar.date, show);  // room for it; the date is on the Today page anyway
  bar_changed = true;
  if (!show) return;
  const int64_t mins = (e->start - now + 59) / 60;
  char text[96];
  if (e->start <= now) snprintf(text, sizeof(text), "%s · now", e->title.c_str());
  else if (mins < 60) snprintf(text, sizeof(text), "%s · %dm", e->title.c_str(), int(mins));
  else snprintf(text, sizeof(text), "%s · %dh", e->title.c_str(), int(mins / 60));
  meeting_label = text;  // fitBar() sets it, as wide as there's room for
  // Five minutes out it turns amber, like the canvas's "needs you".
  const bool soon = mins <= 5;
  setBgColor(bar.meeting, soon ? kAmberBg : kSurface);
  setTextColor(bar.meeting_text, soon ? 0xF5C36B : kText);
}

// True if every shown pill ends inside the pills' area.
bool pillsFit() {
  lv_obj_update_layout(bar.pills);
  lv_area_t area;
  lv_obj_get_coords(bar.pills, &area);
  for (uint32_t i = 0; i < lv_obj_get_child_count(bar.pills); i++) {
    lv_obj_t *c = lv_obj_get_child(bar.pills, i);
    if (lv_obj_is_hidden(c)) continue;
    lv_area_t a;
    lv_obj_get_coords(c, &a);
    if (a.x2 > area.x2) return false;
  }
  return true;
}

// Shows every pill that wants showing, then, while they don't fit, lets the least needed
// go: the layer, then the build (both are on the Dev page and the cheat sheet anyway), and
// last the meeting's title gets shorter. Claude and the call stay.
void fitBar() {
  // Any pill coming or going (the call pill too, which call.cpp shows) means fitting again:
  // without this a call's pill clipped "Build passed" to "Build pass".
  static uint32_t shown_mask = 0;
  uint32_t mask = 0;
  for (uint32_t i = 0; i < lv_obj_get_child_count(bar.pills) && i < 32; i++) {
    if (!lv_obj_is_hidden(lv_obj_get_child(bar.pills, int32_t(i)))) mask |= 1u << i;
  }
  if (mask != shown_mask) bar_changed = true;
  if (!bar_changed) return;
  bar_changed = false;
  setHidden(bar.layer, !want_layer);
  setHidden(bar.build, !want_build);
  if (meeting_label.length()) setTextUpTo(bar.meeting_text, meeting_label.c_str(), 170);
  if (!pillsFit()) {
    setHidden(bar.layer, true);
    if (!pillsFit()) {
      setHidden(bar.build, true);
      if (!pillsFit() && meeting_label.length()) setTextUpTo(bar.meeting_text, meeting_label.c_str(), 90);
    }
  }
  shown_mask = 0;  // what's showing now, so only a later change fits again
  for (uint32_t i = 0; i < lv_obj_get_child_count(bar.pills) && i < 32; i++) {
    if (!lv_obj_is_hidden(lv_obj_get_child(bar.pills, int32_t(i)))) shown_mask |= 1u << i;
  }
}

void updateToast() {
  if (model::toast.version != toast_seen) {
    toast_seen = model::toast.version;
    const String &level = model::toast.level;
    const char *icon = LV_SYMBOL_OK;
    uint32_t color = kGreen, border = 0x1F4A3A;
    if (level == "error") icon = LV_SYMBOL_CLOSE, color = 0xFF9A70, border = 0x5A2E1E;
    else if (level == "attention") icon = LV_SYMBOL_WARNING, color = kAmber, border = 0x4A3A1C;
    lv_label_set_text(toast.icon, icon);
    lv_obj_set_style_text_color(toast.icon, hex(color), 0);
    setTextUpTo(toast.text, model::toast.text.c_str(), 580);
    lv_obj_set_style_border_color(toast.root, hex(border), 0);
    lv_obj_set_hidden(toast.root, false);
    lv_obj_align(toast.root, LV_ALIGN_BOTTOM_MID, 0, -(kBarH + kGap));
    toast_ms = millis();
  } else if (!lv_obj_is_hidden(toast.root) && millis() - toast_ms >= kToastMs) {
    lv_obj_set_hidden(toast.root, true);
  }
}

void updateArt() {
  if (model::art.version == art_seen) return;
  art_seen = model::art.version;
  if (!installPendingArt()) return;
  today::showArt();
  audio::showArt();
  releaseOldArt();  // nothing points at the previous image any more
}

// How long update() takes, a pass at a time, between benchmarks.
struct {
  uint32_t count = 0;
  uint64_t us = 0;
  uint32_t worst_us = 0;
} update_timing;

// Times a full redraw of every page (best of three; total, and without the wait for
// vsync) and PSRAM's raw speed, and logs them with memory and the time taken by lines and
// UI updates since the last one, for tuning. The companion asks for it; the pages flash
// past in about a second.
void bench() {
  const uint32_t tab = lv_tabview_get_tab_active(tabview);
  const uint32_t n = lv_tabview_get_tab_count(tabview);
  char line[200];
  int len = snprintf(line, sizeof(line), "bench @%u MHz: full redraw ms (without vsync wait):", unsigned(getCpuFrequencyMhz()));
  for (uint32_t i = 0; i < n && len < int(sizeof(line)); i++) {
    lv_tabview_set_active(tabview, i, LV_ANIM_OFF);
    uint32_t best = UINT32_MAX, best_draw = UINT32_MAX;
    for (int k = 0; k < 3; k++) {
      lv_obj_invalidate(lv_screen_active());
      const uint32_t wait0 = display::vsyncWaitUs();
      const uint32_t t0 = micros();
      lv_refr_now(nullptr);
      const uint32_t total = micros() - t0;
      const uint32_t draw = total - (display::vsyncWaitUs() - wait0);
      if (total / 1000 < best) best = total / 1000;
      if (draw / 1000 < best_draw) best_draw = draw / 1000;
    }
    len += snprintf(line + len, sizeof(line) - len, " %u (%u)", unsigned(best), unsigned(best_draw));
  }
  lv_tabview_set_active(tabview, tab, LV_ANIM_OFF);
  lv_obj_invalidate(lv_screen_active());

  // PSRAM: fill and copy 384 KB (half a frame).
  constexpr size_t kBytes = 384 * 1024;
  auto *a = static_cast<uint8_t *>(heap_caps_malloc(kBytes, MALLOC_CAP_SPIRAM));
  auto *b = static_cast<uint8_t *>(heap_caps_malloc(kBytes, MALLOC_CAP_SPIRAM));
  uint32_t fill_mbs = 0, copy_mbs = 0;
  if (a && b) {
    uint32_t t0 = micros();
    memset(a, 0x5A, kBytes);
    fill_mbs = kBytes / (micros() - t0);  // bytes per us = MB/s
    t0 = micros();
    memcpy(b, a, kBytes);
    copy_mbs = kBytes / (micros() - t0);
  }
  heap_caps_free(a);
  heap_caps_free(b);
  // Three lines: a log line holds 160 characters, and one line cut the memory figures off.
  pc::log("%s", line);
  pc::log("bench: PSRAM fill %u MB/s, copy %u MB/s; internal RAM free %u, lowest %u, largest block %u; PSRAM free %u, lowest %u",
          unsigned(fill_mbs), unsigned(copy_mbs), unsigned(heap_caps_get_free_size(MALLOC_CAP_INTERNAL)),
          unsigned(heap_caps_get_minimum_free_size(MALLOC_CAP_INTERNAL)),
          unsigned(heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL)), unsigned(heap_caps_get_free_size(MALLOC_CAP_SPIRAM)),
          unsigned(heap_caps_get_minimum_free_size(MALLOC_CAP_SPIRAM)));
  // Since the last benchmark (or the start): taking in lines, and the UI catching up with them.
  const pc::LineTiming lines = pc::takeLineTiming();
  pc::log("bench: %u lines, %u us avg, %u us worst; %u ui updates, %u us avg, %u us worst", unsigned(lines.lines),
          unsigned(lines.lines ? lines.us / lines.lines : 0), unsigned(lines.worst_us), unsigned(update_timing.count),
          unsigned(update_timing.count ? update_timing.us / update_timing.count : 0), unsigned(update_timing.worst_us));
  update_timing = {};
}

}  // namespace

// For the hello: how many LVGL objects the deck holds.
static uint32_t countObjects(lv_obj_t *o) {
  uint32_t n = 1;
  for (uint32_t i = 0; i < lv_obj_get_child_count(o); i++) n += countObjects(lv_obj_get_child(o, i));
  return n;
}

void begin() {
  Preferences prefs;
  prefs.begin("deck", true);
  usePalette(prefs.getString("palette", "base").c_str());
  prefs.end();
  initFonts();

  lv_obj_t *scr = lv_screen_active();
  lv_obj_set_style_bg_color(scr, hex(kBg), 0);
  lv_obj_set_style_text_color(scr, hex(kText), 0);
  lv_obj_set_flex_flow(scr, LV_FLEX_FLOW_COLUMN);
  lv_obj_set_scrollable(scr, false);

  buildStatusBar(scr);

  lv_obj_t *tv = tabview = lv_tabview_create(scr);
  lv_obj_set_width(tv, LV_PCT(100));
  lv_obj_set_flex_grow(tv, 1);
  lv_obj_set_style_bg_opa(tv, LV_OPA_TRANSP, 0);
  lv_tabview_set_tab_bar_position(tv, LV_DIR_BOTTOM);
  lv_tabview_set_tab_bar_size(tv, kBarH);
  lv_obj_t *content = lv_tabview_get_content(tv);
  lv_obj_set_scrollable(content, false);
  // LVGL hands a gesture to the nearest parent that doesn't pass gestures on, and every
  // object passes them on by default: without this, swipes went past to the screen.
  lv_obj_set_gesture_bubble(content, false);
  lv_obj_add_event_cb(content, onSwipe, LV_EVENT_GESTURE, nullptr);

  today::build(lv_tabview_add_tab(tv, LV_SYMBOL_HOME "  Today"));
  audio::build(lv_tabview_add_tab(tv, LV_SYMBOL_AUDIO "  Audio"));
  dev::build(lv_tabview_add_tab(tv, LV_SYMBOL_EDIT "  Dev"));
  windows::build(lv_tabview_add_tab(tv, LV_SYMBOL_LIST "  Windows"));
  capture::build(lv_tabview_add_tab(tv, LV_SYMBOL_COPY "  Capture"));
  numpad::build(lv_tabview_add_tab(tv, LV_SYMBOL_KEYBOARD "  Numpad"));
  pc::bootMark("ui.pages");
  styleTabs(tv);
  ctxbar::build(scr, content);  // over the pages' bottom, above the tab bar
  call::build();
  cheat::build();
  launcher::build(lv_tabview_get_tab_bar(tv));  // its button ends the tab bar; its panel goes over the pages
  day::build();  // under the sessions board, the approval banner and Claude's questions
  diag::build();  // the same: an approval or a question can come while it's open
  sessions::build();  // under the approval banner and Claude's questions, which can come while it's open
  approval::build();
  ask::build();
  buildToast();
  idle::build();
  buildFlashing();  // last: over the idle clock too
  glow::build();  // on the system layer, over all of it
  pc::bootMark("ui.overlays");
  pc::bootValue("objects", countObjects(lv_screen_active()) + countObjects(lv_layer_top()) + countObjects(lv_layer_sys()));

  update();
}

void showPage(int index) { lv_tabview_set_active(tabview, index, LV_ANIM_OFF); }

int currentPage() { return int(lv_tabview_get_tab_active(tabview)); }

void previewVolume(int level) {
  preview_ms = millis();
  volume_seen = UINT32_MAX;  // once the hold is over, the PC's level again, even if unchanged
  lv_label_set_text_fmt(bar.vol, LV_SYMBOL_VOLUME_MAX "  %d%%", level);
  lv_obj_set_style_text_color(bar.vol, hex(kText), 0);
  lv_obj_set_style_bg_color(bar.vol_button, hex(kRaised), 0);  // a drag up unmutes
}

void update() {
  const uint32_t t0 = micros();
  updateCompanion();
  updateClock();
  updateVolume();
  updateMic();
  updateClaude();
  updateBuild();
  updateLayer();
  updateMeeting();
  updateToast();
  updateArt();
  today::update();
  day::update();
  audio::update();
  dev::update();
  windows::update();
  numpad::update();
  capture::update();  // with its Tasks card
  ctxbar::update();
  call::update();
  fitBar();  // after the call pill too: it sits among the others
  cheat::update();
  launcher::update();
  sessions::update();
  approval::update();
  ask::update();
  diag::update();
  idle::update();
  glow::update();
  updateFlashing();
  const uint32_t us = micros() - t0;
  update_timing.count++;
  update_timing.us += us;
  if (us > update_timing.worst_us) update_timing.worst_us = us;
  if (model::bench_requests != bench_seen) {
    bench_seen = model::bench_requests;
    bench();
  }
}

}  // namespace ui
