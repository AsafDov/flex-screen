#include "call.h"

#include <Arduino.h>

#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::call {
namespace {

constexpr uint32_t kLeave = 0xC93C3C;
constexpr uint32_t kTickMs = 1000;
constexpr int kControls = 5;

enum Control { Mic, Camera, Hand, Deafen, Leave };

struct ControlView {
  lv_obj_t *root, *icon, *name, *state;
};

struct {
  lv_obj_t *root, *badge, *badge_text, *title, *duration;
  ControlView controls[kControls];
  MicIcon mic_icon;
  lv_obj_t *reactions, *note, *volume_row, *volume, *volume_value;
  lv_obj_t *pill, *pill_text;
} w;

const char *const kActions[kControls] = {"mic", "camera", "hand", "deafen", "leave"};
const char *const kReactions[][2] = {{"Like", "like"}, {"Love", "love"}, {"Applause", "applause"}, {"Wow", "wow"}, {"Laugh", "laugh"}};

String app_seen;
uint32_t call_seen = UINT32_MAX;
uint32_t tick_ms = 0;
uint32_t volume_sent_ms = 0;
uint32_t volume_touched_ms = 0;
bool hidden_by_user = false;

uint32_t appColor(const String &app) {
  if (app == "teams") return 0x5B5FC7;
  if (app == "zoom") return 0x2D8CFF;
  if (app == "discord") return 0x5865F2;
  if (app == "webex") return 0x07C160;
  return 0x2E7D5B;
}

void onControl(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  char action[16];
  snprintf(action, sizeof(action), "call.%s", kActions[i]);
  pc::runAction(action, "");
}

void onReact(lv_event_t *e) { pc::callReact(static_cast<const char *>(lv_event_get_user_data(e))); }
void onOpen(lv_event_t *) { pc::runAction("call.focus", ""); }

void onHide(lv_event_t *) {
  hidden_by_user = true;
  lv_obj_set_hidden(w.root, true);
}

void onPill(lv_event_t *) {
  hidden_by_user = false;
  lv_obj_set_hidden(w.root, false);
}

void onVolume(lv_event_t *e) {
  const int v = lv_slider_get_value(w.volume);
  setTextf(w.volume_value, "%d", v);
  volume_touched_ms = millis();
  if (lv_event_get_code(e) == LV_EVENT_RELEASED || millis() - volume_sent_ms > 80) {
    pc::callVolume(v);
    volume_sent_ms = millis();
  }
}

ControlView buildControl(lv_obj_t *row, Control c) {
  ControlView v;
  v.root = button(row, 0, 120, c == Leave ? kLeave : kRaised, onControl, reinterpret_cast<void *>(intptr_t(c)));
  lv_obj_set_flex_grow(v.root, 1);
  lv_obj_set_style_radius(v.root, 18, 0);
  flexCol(v.root, 6);
  lv_obj_set_flex_align(v.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  if (c == Mic) {
    w.mic_icon = micIcon(v.root);
    v.icon = w.mic_icon.root;
  } else {
    static const char *const symbols[] = {"", LV_SYMBOL_VIDEO, LV_SYMBOL_UP, LV_SYMBOL_MUTE, LV_SYMBOL_CALL};
    v.icon = label(v.root, symbols[c], &lv_font_montserrat_28, kText);
  }
  static const char *const names[] = {"Mic", "Camera", "Hand", "Deafen", "Leave"};
  v.name = label(v.root, names[c], &body_font, kText);
  v.state = label(v.root, "", &small_font, kSoftText);
  return v;
}

// ---- updating

void showControl(Control c, bool can, bool on, const char *state, uint32_t on_bg) {
  ControlView &v = w.controls[c];
  setHidden(v.root, !can);
  if (!can) return;
  if (c != Leave) setBgColor(v.root, on ? on_bg : kRaised);
  setText(v.state, state);
}

void updatePanel() {
  const model::Call &k = model::call;
  const uint32_t color = appColor(k.app);
  setBgColor(w.badge, color);
  char initial[2] = {k.name.length() ? k.name[0] : '?', '\0'};
  setText(w.badge_text, initial);
  setTextf(w.title, "%s call", k.name.c_str());

  showControl(Mic, k.can_mic, k.mic_muted, k.mic_muted ? "Muted" : "Live", kAmberBg);
  setMicIcon(w.mic_icon, k.mic_muted ? kAmber : kText, k.mic_muted);
  setTextColor(w.controls[Mic].state, k.mic_muted ? kAmber : kSoftText);
  showControl(Camera, k.can_camera, k.camera == 1, k.camera == 1 ? "On" : k.camera == 0 ? "Off" : "", kSelected);
  showControl(Hand, k.can_hand, k.hand == 1, k.hand == 1 ? "Raised" : "", kAmberBg);
  showControl(Deafen, k.can_deafen, k.deafened, k.deafened ? "Deafened" : "", kAmberBg);
  showControl(Leave, k.can_leave, false, "", kLeave);

  setHidden(w.reactions, !k.can_react);
  setText(w.note, k.note.c_str());
  setHidden(w.note, k.note.length() == 0);
  setHidden(w.volume_row, k.volume < 0);
  if (k.volume >= 0 && !lv_obj_has_state(w.volume, LV_STATE_PRESSED) && millis() - volume_touched_ms > 800) {
    lv_slider_set_value(w.volume, k.volume, LV_ANIM_OFF);
    setTextf(w.volume_value, "%d", k.volume);
  }
}

void updateDuration() {
  const model::Call &k = model::call;
  char took[16];
  formatTime(took, sizeof(took), float(model::agedSecs(k.secs, k.at_ms)));
  setText(w.duration, took);
  setTextf(w.pill_text, "%s %s", k.name.c_str(), took);
}

}  // namespace

void build() {
  w.root = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_size(w.root, 780, 480 - 2 * kBarH - 12);
  lv_obj_set_pos(w.root, 10, kBarH + 6);
  lv_obj_set_style_border_width(w.root, 2, 0);
  lv_obj_set_style_radius(w.root, 18, 0);
  lv_obj_set_style_pad_all(w.root, 16, 0);
  lv_obj_set_style_shadow_width(w.root, 30, 0);
  lv_obj_set_style_shadow_color(w.root, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.root, LV_OPA_60, 0);
  lv_obj_set_clickable(w.root, true);  // taps between buttons don't fall through to the page
  flexCol(w.root, 14);

  lv_obj_t *head = box(w.root);
  lv_obj_set_size(head, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(head, 12);
  w.badge = card(head, kAccent, kAccent);
  lv_obj_set_size(w.badge, 40, 40);
  lv_obj_set_style_pad_all(w.badge, 0, 0);
  lv_obj_set_style_radius(w.badge, LV_RADIUS_CIRCLE, 0);
  w.badge_text = label(w.badge, "", &title_font, 0xFFFFFF);
  lv_obj_center(w.badge_text);
  w.title = label(head, "", &title_font, kText);
  w.duration = label(head, "", &body_font, kMuted);
  lv_obj_t *gap = box(head);
  lv_obj_set_flex_grow(gap, 1);
  lv_obj_t *open = button(head, LV_SIZE_CONTENT, 38, kRaised, onOpen);
  lv_obj_set_style_radius(open, 10, 0);
  lv_obj_set_style_pad_hor(open, 14, 0);
  lv_obj_center(label(open, "Open app", &small_font, kText));
  lv_obj_t *hide = button(head, LV_SIZE_CONTENT, 38, kRaised, onHide);
  lv_obj_set_style_radius(hide, 10, 0);
  lv_obj_set_style_pad_hor(hide, 14, 0);
  lv_obj_center(label(hide, "Hide  " LV_SYMBOL_DOWN, &small_font, kText));

  lv_obj_t *controls = box(w.root);
  lv_obj_set_size(controls, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(controls, 12);
  for (int c = 0; c < kControls; c++) w.controls[c] = buildControl(controls, Control(c));

  w.reactions = box(w.root);
  lv_obj_set_size(w.reactions, LV_PCT(100), 44);
  flexRow(w.reactions, 8);
  for (const auto &r : kReactions) {
    lv_obj_t *b = button(w.reactions, 0, 44, kRaised, onReact, const_cast<char *>(r[1]));
    lv_obj_set_flex_grow(b, 1);
    lv_obj_set_style_radius(b, 12, 0);
    lv_obj_center(label(b, r[0], &small_font, kText));
  }

  w.volume_row = box(w.root);
  lv_obj_set_size(w.volume_row, LV_PCT(100), 36);
  flexRow(w.volume_row, 12);
  label(w.volume_row, LV_SYMBOL_VOLUME_MAX, &body_font, kMuted);
  w.volume = lv_slider_create(w.volume_row);
  lv_obj_set_flex_grow(w.volume, 1);
  lv_obj_set_height(w.volume, 8);
  lv_slider_set_range(w.volume, 0, 100);
  lv_obj_set_style_bg_color(w.volume, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(w.volume, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(w.volume, hex(kText), LV_PART_KNOB);
  lv_obj_set_style_pad_all(w.volume, 6, LV_PART_KNOB);
  lv_obj_set_ext_click_area(w.volume, 14);
  lv_obj_add_event_cb(w.volume, onVolume, LV_EVENT_VALUE_CHANGED, nullptr);
  lv_obj_add_event_cb(w.volume, onVolume, LV_EVENT_RELEASED, nullptr);
  w.volume_value = label(w.volume_row, "", &body_font, kText);
  lv_obj_set_width(w.volume_value, 36);
  lv_obj_set_style_text_align(w.volume_value, LV_TEXT_ALIGN_RIGHT, 0);

  w.note = label(w.root, "", &small_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);
  lv_obj_set_hidden(w.root, true);
}

void buildPill(lv_obj_t *bar) {
  w.pill = pill(bar);
  lv_obj_set_clickable(w.pill, true);
  lv_obj_add_event_cb(w.pill, onPill, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_style_bg_color(w.pill, hex(0x3A1616), 0);
  lv_obj_set_style_border_color(w.pill, hex(0x5A2424), 0);
  lv_obj_t *dot = box(w.pill);
  lv_obj_set_size(dot, 8, 8);
  lv_obj_set_style_radius(dot, 4, 0);
  lv_obj_set_style_bg_color(dot, hex(0xFF5A5A), 0);
  lv_obj_set_style_bg_opa(dot, LV_OPA_COVER, 0);
  w.pill_text = label(w.pill, "", &body_font, 0xFFB4B4);
  lv_obj_set_hidden(w.pill, true);
}

void update() {
  const model::Call &k = model::call;
  const bool active = pc::connected() && k.app.length() > 0;
  if (k.app != app_seen) {  // a call started, ended or moved to another app: show it afresh
    app_seen = k.app;
    hidden_by_user = false;
  }
  if (model::call.version != call_seen) {
    call_seen = model::call.version;
    if (active) updatePanel();
  }
  setHidden(w.root, !active || hidden_by_user);
  setHidden(w.pill, !active || !hidden_by_user);
  if (active && millis() - tick_ms >= kTickMs) {
    tick_ms = millis();
    updateDuration();
  }
}

}  // namespace ui::call
