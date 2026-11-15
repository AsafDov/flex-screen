#include "call.h"

#include <Arduino.h>

#include "fonts/font_icons.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::call {
namespace {

constexpr uint32_t kLeave = 0xC93C3C;
constexpr uint32_t kTickMs = 1000;
constexpr uint32_t kClearHintMs = 2000;
constexpr int kControls = 7;
constexpr int kInnerW = 780 - 2 * 16 - 2 * 2;  // the panel's width inside its padding and border
constexpr int kColorsW = 560;
constexpr int kSwatchGap = 6;
constexpr int kMaxSwatchW = 64;
constexpr int kWidths = 4;
constexpr int kDrawActions = 7;

enum Control { Mic, Camera, Hand, Whiteboard, Annotate, Deafen, Leave };
enum DrawAction { Undo, Redo, Clear, Save, DrawMic, DrawCamera, Close };

struct ControlView {
  lv_obj_t *root, *icon, *name, *state;
};

struct ToolView {
  lv_obj_t *root, *icon, *name;
};

struct ActionView {
  lv_obj_t *root, *icon, *name;
};

struct {
  lv_obj_t *root, *badge, *badge_text, *title, *duration, *switch_view, *switch_text;
  // The call's controls
  lv_obj_t *body;
  ControlView controls[kControls];
  MicIcon mic_icon;
  lv_obj_t *reactions, *note, *volume_row, *volume, *volume_value;
  // Drawing (Zoom's whiteboard or annotation)
  lv_obj_t *draw, *tools_row, *kinds_row, *paint_row, *colors_box, *widths_box;
  lv_obj_t *weight_row, *weight, *weight_value;
  ToolView tools[model::Draw::kMaxTools];
  lv_obj_t *kinds[model::Draw::kMaxKinds], *kind_names[model::Draw::kMaxKinds];
  lv_obj_t *swatches[model::Draw::kMaxColors];
  lv_obj_t *widths[kWidths];
  ActionView actions[kDrawActions];
  lv_obj_t *pill, *pill_text;
} w;

const char *const kActions[kControls] = {"mic", "camera", "hand", "whiteboard", "annotate", "deafen", "leave"};
const char *const kReactions[][2] = {{"Like", "like"}, {"Love", "love"}, {"Applause", "applause"}, {"Wow", "wow"}, {"Laugh", "laugh"}};
const int kDotSizes[kWidths] = {4, 7, 11, 16};

String app_seen;
String mode_seen;
uint32_t call_seen = UINT32_MAX;
uint32_t tick_ms = 0;
uint32_t volume_sent_ms = 0;
uint32_t volume_touched_ms = 0;
uint32_t clear_hint_ms = 0;
uint32_t weight_touched_ms = 0;
bool hidden_by_user = false;
bool controls_while_drawing = false;  // the call's controls up while a drawing tool is open

uint32_t appColor(const String &app) {
  if (app == "teams") return 0x5B5FC7;
  if (app == "zoom") return 0x2D8CFF;
  if (app == "discord") return 0x5865F2;
  if (app == "webex") return 0x07C160;
  return 0x2E7D5B;
}

// A tool id's icon and its name on the deck.
struct ToolLook {
  const char *id, *glyph, *name;
};

// Annotation's Spotlight is its laser pointer, so both modes call it Laser.
const ToolLook kToolLooks[] = {
    {"laser", "target", "Laser"},   {"spotlight", "target", "Laser"}, {"pen", "pen", "Pen"},
    {"highlighter", "highlighter", "Marker"}, {"vanish", "magic", "Fading"}, {"eraser", "eraser", "Eraser"},
    {"grab", "hand", "Pan"},        {"select", "select", "Select"},
};

const ToolLook &lookOf(const String &id) {
  for (const ToolLook &l : kToolLooks) {
    if (id == l.id) return l;
  }
  static const ToolLook unknown = {"", "", ""};
  return unknown;
}

bool drawing() { return model::call.draw.mode.length() > 0; }

void showDrawing();

void onControl(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  // The board is open already: back to its tools (asking again would start a new board).
  if (i == Whiteboard && model::call.draw.mode == "whiteboard") {
    controls_while_drawing = false;
    showDrawing();
    return;
  }
  char action[20];
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

void onSwitchView(lv_event_t *) {
  controls_while_drawing = !controls_while_drawing;
  showDrawing();
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

void updateDraw();

// Drawing taps show at once; the companion's next state confirms them.
void onTool(lv_event_t *e) {
  model::Draw &d = model::call.draw;
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i >= d.tool_count) return;
  pc::runAction("draw.tool", d.tools[i].c_str());
  if (d.tool != d.tools[i]) d.kind_count = 0;  // the new tool's kinds come with the next state
  d.tool = d.tools[i];
  updateDraw();
}

void onKind(lv_event_t *e) {
  model::Draw &d = model::call.draw;
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i >= d.kind_count) return;
  pc::runAction("draw.kind", d.kind_ids[i].c_str());
  d.kind = d.kind_ids[i];
  updateDraw();
}

void onColor(lv_event_t *e) {
  model::Draw &d = model::call.draw;
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  char id[4];
  snprintf(id, sizeof(id), "%d", i);
  pc::runAction("draw.color", id);
  d.color = i;
  updateDraw();
}

void onWidth(lv_event_t *e) {
  model::Draw &d = model::call.draw;
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  char id[4];
  snprintf(id, sizeof(id), "%d", i);
  pc::runAction("draw.width", id);
  d.width = i;
  updateDraw();
}

// The value shows while dragging; Zoom gets it on release (each change opens the tool's panel there).
void onWeight(lv_event_t *e) {
  const int v = lv_slider_get_value(w.weight);
  setTextf(w.weight_value, "%d", v);
  weight_touched_ms = millis();
  if (lv_event_get_code(e) != LV_EVENT_RELEASED) return;
  char id[6];
  snprintf(id, sizeof(id), "%d", v);
  pc::runAction("draw.weight", id);
  model::call.draw.weight = v;
}

void onDrawAction(lv_event_t *e) {
  static const char *const actions[kDrawActions] = {"draw.undo", "draw.redo", "", "draw.save", "call.mic", "call.camera", "draw.close"};
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i == Clear) {  // a tap only says how; holding clears
    clear_hint_ms = millis();
    setText(w.actions[Clear].name, "Hold");
    return;
  }
  pc::runAction(actions[i], "");
}

void onClearHold(lv_event_t *) {
  clear_hint_ms = 0;
  setText(w.actions[Clear].name, "Clear");
  pc::runAction("draw.clear", "");
}

// ---- building

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
    static const char *const symbols[] = {"", LV_SYMBOL_VIDEO, LV_SYMBOL_UP, LV_SYMBOL_IMAGE, LV_SYMBOL_EDIT, LV_SYMBOL_MUTE, LV_SYMBOL_CALL};
    v.icon = label(v.root, symbols[c], &lv_font_montserrat_28, kText);
  }
  static const char *const names[] = {"Mic", "Camera", "Hand", "Whiteboard", "Annotate", "Deafen", "Leave"};
  v.name = label(v.root, names[c], &body_font, kText);
  v.state = label(v.root, "", &small_font, kSoftText);
  return v;
}

// A kind button's width when `n` share a row.
int kindWidth(int n) { return (kInnerW - (n - 1) * 6) / n; }

lv_obj_t *row(lv_obj_t *parent, int h, int gap) {
  lv_obj_t *r = box(parent);
  lv_obj_set_size(r, LV_PCT(100), h);
  flexRow(r, gap);
  return r;
}

void buildDraw() {
  w.draw = box(w.root);
  lv_obj_set_size(w.draw, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(w.draw, 8);

  w.tools_row = row(w.draw, 58, 6);
  for (int i = 0; i < model::Draw::kMaxTools; i++) {
    ToolView &t = w.tools[i];
    t.root = button(w.tools_row, 0, 58, kRaised, onTool, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_flex_grow(t.root, 1);
    lv_obj_set_style_radius(t.root, 12, 0);
    lv_obj_set_style_border_color(t.root, hex(kAccent), 0);
    flexCol(t.root, 4);
    lv_obj_set_flex_align(t.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    t.icon = label(t.root, "", &font_icons_15, kText);
    t.name = label(t.root, "", &small_font, kSoftText);
  }

  // The laser's and the eraser's kinds, sharing the row's width.
  w.kinds_row = box(w.draw);
  lv_obj_set_size(w.kinds_row, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(w.kinds_row, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_style_pad_column(w.kinds_row, 6, 0);
  lv_obj_set_style_pad_row(w.kinds_row, 6, 0);
  const int kind_w = kindWidth(model::Draw::kMaxKinds);
  for (int i = 0; i < model::Draw::kMaxKinds; i++) {
    w.kinds[i] = button(w.kinds_row, kind_w, 36, kRaised, onKind, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(w.kinds[i], 10, 0);
    lv_obj_set_style_pad_hor(w.kinds[i], 2, 0);
    lv_obj_set_style_border_color(w.kinds[i], hex(kAccent), 0);
    w.kind_names[i] = label(w.kinds[i], "", &small_font, kText);
    ellipsize(w.kind_names[i]);
    lv_obj_set_style_text_align(w.kind_names[i], LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_center(w.kind_names[i]);
  }

  w.paint_row = row(w.draw, LV_SIZE_CONTENT, 12);
  lv_obj_t *paint = w.paint_row;
  w.colors_box = box(paint);
  lv_obj_set_size(w.colors_box, kColorsW, LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(w.colors_box, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_style_pad_column(w.colors_box, kSwatchGap, 0);
  lv_obj_set_style_pad_row(w.colors_box, kSwatchGap, 0);
  for (int i = 0; i < model::Draw::kMaxColors; i++) {
    w.swatches[i] = button(w.colors_box, 40, 36, 0x000000, onColor, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(w.swatches[i], 8, 0);
    lv_obj_set_style_border_width(w.swatches[i], 1, 0);
    lv_obj_set_style_border_color(w.swatches[i], hex(kBorder), 0);
  }
  w.widths_box = box(paint);
  lv_obj_set_height(w.widths_box, 78);
  lv_obj_set_flex_grow(w.widths_box, 1);
  flexRow(w.widths_box, 6);
  for (int i = 0; i < kWidths; i++) {
    w.widths[i] = button(w.widths_box, 0, 78, kRaised, onWidth, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_flex_grow(w.widths[i], 1);
    lv_obj_set_style_radius(w.widths[i], 12, 0);
    lv_obj_set_style_border_color(w.widths[i], hex(kAccent), 0);
    lv_obj_t *dot = box(w.widths[i]);
    lv_obj_set_size(dot, kDotSizes[i], kDotSizes[i]);
    lv_obj_set_style_radius(dot, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(dot, hex(kText), 0);
    lv_obj_set_style_bg_opa(dot, LV_OPA_COVER, 0);
    lv_obj_center(dot);
  }

  // A whiteboard tool's weight, instead of the four widths.
  w.weight_row = row(w.draw, 40, 12);
  lv_obj_t *weight_name = label(w.weight_row, "Weight", &small_font, kSoftText);
  lv_obj_set_width(weight_name, 56);
  w.weight = lv_slider_create(w.weight_row);
  lv_obj_set_flex_grow(w.weight, 1);
  lv_obj_set_height(w.weight, 10);
  lv_obj_set_style_bg_color(w.weight, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(w.weight, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(w.weight, hex(kText), LV_PART_KNOB);
  lv_obj_set_style_pad_all(w.weight, 8, LV_PART_KNOB);
  lv_obj_set_ext_click_area(w.weight, 16);
  lv_obj_add_event_cb(w.weight, onWeight, LV_EVENT_VALUE_CHANGED, nullptr);
  lv_obj_add_event_cb(w.weight, onWeight, LV_EVENT_RELEASED, nullptr);
  w.weight_value = label(w.weight_row, "", &body_font, kText);
  lv_obj_set_width(w.weight_value, 32);
  lv_obj_set_style_text_align(w.weight_value, LV_TEXT_ALIGN_RIGHT, 0);

  lv_obj_t *actions = row(w.draw, 48, 6);
  static const char *const glyphs[kDrawActions] = {"undo", "redo", "broom", "save", "mic", "video", "close"};
  static const char *const names[kDrawActions] = {"Undo", "Redo", "Clear", "Save", "Mic", "Camera", "Close"};
  for (int i = 0; i < kDrawActions; i++) {
    ActionView &a = w.actions[i];
    a.root = button(actions, 0, 48, i == Close ? 0x5A2424 : kRaised, nullptr);
    lv_obj_add_event_cb(a.root, onDrawAction, LV_EVENT_SHORT_CLICKED, reinterpret_cast<void *>(intptr_t(i)));
    if (i == Clear) lv_obj_add_event_cb(a.root, onClearHold, LV_EVENT_LONG_PRESSED, nullptr);
    lv_obj_set_flex_grow(a.root, 1);
    lv_obj_set_style_radius(a.root, 12, 0);
    flexRow(a.root, 6);
    lv_obj_set_flex_align(a.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    a.icon = label(a.root, glyphOf(glyphs[i]), &font_icons_15, kText);
    a.name = label(a.root, names[i], &small_font, kText);
  }
}

// ---- updating

void showControl(Control c, bool can, bool on, const char *state, uint32_t on_bg) {
  ControlView &v = w.controls[c];
  setHidden(v.root, !can);
  if (!can) return;
  if (c != Leave) setBgColor(v.root, on ? on_bg : kRaised);
  setText(v.state, state);
}

void setOpa(lv_obj_t *o, lv_opa_t opa) {
  if (lv_obj_get_style_opa(o, LV_PART_MAIN) != opa) lv_obj_set_style_opa(o, opa, 0);
}

void setBorderWidth(lv_obj_t *o, int width) {
  if (lv_obj_get_style_border_width(o, LV_PART_MAIN) != width) lv_obj_set_style_border_width(o, width, 0);
}

void updateDraw() {
  const model::Call &k = model::call;
  const model::Draw &d = k.draw;
  for (int i = 0; i < model::Draw::kMaxTools; i++) {
    ToolView &t = w.tools[i];
    setHidden(t.root, i >= d.tool_count);
    if (i >= d.tool_count) continue;
    const ToolLook &look = lookOf(d.tools[i]);
    setText(t.icon, glyphOf(look.glyph));
    setText(t.name, look.name);
    const bool on = d.tools[i] == d.tool;
    setBgColor(t.root, on ? kSelected : kRaised);
    setBorderWidth(t.root, on ? 2 : 0);
    setTextColor(t.icon, on ? kText : kSoftText);
    setTextColor(t.name, on ? kText : kSoftText);
  }

  setHidden(w.kinds_row, d.kind_count == 0);
  const int kind_w = kindWidth(d.kind_count > 0 ? d.kind_count : 1);
  for (int i = 0; i < model::Draw::kMaxKinds; i++) {
    setHidden(w.kinds[i], i >= d.kind_count);
    if (i >= d.kind_count) continue;
    if (lv_obj_get_width(w.kinds[i]) != kind_w) lv_obj_set_width(w.kinds[i], kind_w);
    setText(w.kind_names[i], d.kind_names[i].c_str());
    setBgColor(w.kinds[i], d.kind_ids[i] == d.kind ? kSelected : kRaised);
    setBorderWidth(w.kinds[i], d.kind_ids[i] == d.kind ? 2 : 0);
  }

  // Colors only for a tool that takes them; beside the four widths, or the whole row when the
  // tool has a weight slider instead. Two rows of swatches, as wide as the palette allows.
  const bool slider = d.weight_max > 0;
  const bool colors = d.can_color && d.color_count > 0;
  const bool widths = d.can_width && !slider;
  setHidden(w.paint_row, !colors && !widths);
  setHidden(w.colors_box, !colors);
  setHidden(w.widths_box, !widths);
  const int colors_w = widths ? kColorsW : kInnerW;
  if (lv_obj_get_width(w.colors_box) != colors_w) lv_obj_set_width(w.colors_box, colors_w);
  // Up to 12 in one row, more in two; never wider than kMaxSwatchW.
  const int cols = d.color_count <= 12 ? d.color_count : (d.color_count + 1) / 2;
  const int fit = cols > 0 ? (colors_w - (cols - 1) * kSwatchGap) / cols : kMaxSwatchW;
  const int swatch_w = fit < kMaxSwatchW ? fit : kMaxSwatchW;
  for (int i = 0; i < model::Draw::kMaxColors; i++) {
    lv_obj_t *s = w.swatches[i];
    setHidden(s, i >= d.color_count);
    if (i >= d.color_count) continue;
    if (lv_obj_get_width(s) != swatch_w) lv_obj_set_width(s, swatch_w);
    setBgColor(s, d.colors[i]);
    const bool on = i == d.color;
    setBorderWidth(s, on ? 4 : 1);
    setBorderColor(s, on ? kText : kBorder);
  }
  setHidden(w.weight_row, !slider);
  if (slider) {
    if (lv_slider_get_min_value(w.weight) != d.weight_min || lv_slider_get_max_value(w.weight) != d.weight_max)
      lv_slider_set_range(w.weight, d.weight_min, d.weight_max);
    if (d.weight >= 0 && !lv_obj_has_state(w.weight, LV_STATE_PRESSED) && millis() - weight_touched_ms > 800) {
      lv_slider_set_value(w.weight, d.weight, LV_ANIM_OFF);
      setTextf(w.weight_value, "%d", d.weight);
    }
    if (d.weight < 0) setText(w.weight_value, "");
  }
  for (int i = 0; i < kWidths; i++) {
    setBgColor(w.widths[i], i == d.width ? kSelected : kRaised);
    setBorderWidth(w.widths[i], i == d.width ? 2 : 0);
  }

  setOpa(w.actions[Undo].root, d.undo ? LV_OPA_COVER : LV_OPA_40);
  setOpa(w.actions[Redo].root, d.redo ? LV_OPA_COVER : LV_OPA_40);
  setHidden(w.actions[Save].root, !d.save);
  setText(w.actions[DrawMic].icon, glyphOf(k.mic_muted ? "mic-off" : "mic"));
  setText(w.actions[DrawMic].name, k.mic_muted ? "Muted" : "Mic");
  setBgColor(w.actions[DrawMic].root, k.mic_muted ? kAmberBg : kRaised);
  setTextColor(w.actions[DrawMic].name, k.mic_muted ? kAmber : kText);
  setTextColor(w.actions[DrawMic].icon, k.mic_muted ? kAmber : kText);
  setHidden(w.actions[DrawCamera].root, !k.can_camera);
  setBgColor(w.actions[DrawCamera].root, k.camera == 1 ? kSelected : kRaised);
  setText(w.actions[DrawCamera].name, k.camera == 0 ? "Cam off" : "Camera");
  setText(w.actions[Close].name, d.mode == "whiteboard" ? "Close board" : "Stop");
}

// The drawing view or the call's controls, and the head's switch between them.
void showDrawing() {
  const bool tools = drawing() && !controls_while_drawing;
  setHidden(w.draw, !tools);
  setHidden(w.body, tools);
  setHidden(w.switch_view, !drawing());
  setText(w.switch_text, tools ? "Call  " LV_SYMBOL_RIGHT : "Draw  " LV_SYMBOL_RIGHT);
  const model::Call &k = model::call;
  if (tools) setTextf(w.title, "%s %s", k.name.c_str(), k.draw.mode == "whiteboard" ? "whiteboard" : "annotation");
  else setTextf(w.title, "%s call", k.name.c_str());
}

void updatePanel() {
  const model::Call &k = model::call;
  const uint32_t color = appColor(k.app);
  setBgColor(w.badge, color);
  char initial[2] = {k.name.length() ? k.name[0] : '?', '\0'};
  setText(w.badge_text, initial);

  showControl(Mic, k.can_mic, k.mic_muted, k.mic_muted ? "Muted" : "Live", kAmberBg);
  setMicIcon(w.mic_icon, k.mic_muted ? kAmber : kText, k.mic_muted);
  setTextColor(w.controls[Mic].name, k.mic_muted ? kAmber : kText);
  setTextColor(w.controls[Mic].state, k.mic_muted ? kAmber : kSoftText);
  showControl(Camera, k.can_camera, k.camera == 1, k.camera == 1 ? "On" : k.camera == 0 ? "Off" : "", kSelected);
  showControl(Hand, k.can_hand, k.hand == 1, k.hand == 1 ? "Raised" : "", kAmberBg);
  const bool board = k.draw.mode == "whiteboard", annotating = k.draw.mode == "annotate";
  showControl(Whiteboard, k.can_whiteboard || board, board, board ? "Open" : "", kSelected);
  showControl(Annotate, k.can_annotate || annotating, annotating, annotating ? "On" : "", kSelected);
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

  if (k.draw.mode != mode_seen) {  // drawing started or ended: its tools come up
    mode_seen = k.draw.mode;
    controls_while_drawing = false;
  }
  if (drawing()) updateDraw();
  showDrawing();
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
  lv_obj_set_size(gap, 1, 1);  // an object's default height is 130 px, which made the head that tall
  lv_obj_set_flex_grow(gap, 1);
  w.switch_view = button(head, LV_SIZE_CONTENT, 38, kRaised, onSwitchView);
  lv_obj_set_style_radius(w.switch_view, 10, 0);
  lv_obj_set_style_pad_hor(w.switch_view, 14, 0);
  w.switch_text = label(w.switch_view, "", &small_font, kText);
  lv_obj_center(w.switch_text);
  lv_obj_t *open = button(head, LV_SIZE_CONTENT, 38, kRaised, onOpen);
  lv_obj_set_style_radius(open, 10, 0);
  lv_obj_set_style_pad_hor(open, 14, 0);
  lv_obj_center(label(open, "Open app", &small_font, kText));
  lv_obj_t *hide = button(head, LV_SIZE_CONTENT, 38, kRaised, onHide);
  lv_obj_set_style_radius(hide, 10, 0);
  lv_obj_set_style_pad_hor(hide, 14, 0);
  lv_obj_center(label(hide, "Hide  " LV_SYMBOL_DOWN, &small_font, kText));

  w.body = box(w.root);
  lv_obj_set_size(w.body, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(w.body, 14);

  lv_obj_t *controls = box(w.body);
  lv_obj_set_size(controls, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(controls, 12);
  for (int c = 0; c < kControls; c++) w.controls[c] = buildControl(controls, Control(c));

  w.reactions = box(w.body);
  lv_obj_set_size(w.reactions, LV_PCT(100), 44);
  flexRow(w.reactions, 8);
  for (const auto &r : kReactions) {
    lv_obj_t *b = button(w.reactions, 0, 44, kRaised, onReact, const_cast<char *>(r[1]));
    lv_obj_set_flex_grow(b, 1);
    lv_obj_set_style_radius(b, 12, 0);
    lv_obj_center(label(b, r[0], &small_font, kText));
  }

  w.volume_row = box(w.body);
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

  w.note = label(w.body, "", &small_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);

  buildDraw();
  lv_obj_set_hidden(w.draw, true);
  lv_obj_set_hidden(w.switch_view, true);
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
  if (clear_hint_ms && millis() - clear_hint_ms > kClearHintMs) {
    clear_hint_ms = 0;
    setText(w.actions[Clear].name, "Clear");
  }
}

}  // namespace ui::call
