#include "ask.h"

#include <Arduino.h>

#include "approval.h"
#include "fonts/font_icons.h"
#include "idle.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::ask {
namespace {

constexpr int kX = 10, kW = 800 - 2 * kX;
constexpr int kTop = kBarH + 6;
constexpr int kButtonH = 46, kButtonNoteH = 58;
constexpr int kMax = model::Question::kMax;

struct {
  lv_obj_t *root, *icon, *title, *close, *text, *row;
  lv_obj_t *option[kMax], *option_label[kMax], *option_note[kMax];
  lv_obj_t *done, *open, *ok;
} w;

uint32_t seen = UINT32_MAX;
int link_seen = -1;
bool approval_seen = false;
String answered;  // the question just answered here: hidden until the companion's next word
bool picked[kMax];

bool isNote() { return model::question.kind == "note"; }

void finish(const int *picks, int count) {
  const model::Question &q = model::question;
  if (!q.id.length()) return;
  pc::answerClaude(q.id.c_str(), picks, count);
  answered = q.id;
  lv_obj_set_hidden(w.root, true);  // at once; the companion sends what's next, if anything
}

void showPicked() {
  int count = 0;
  for (int i = 0; i < model::question.count; i++) {
    setBgColor(w.option[i], picked[i] ? kAccent : kRaised);
    setTextColor(w.option_label[i], picked[i] ? kOnAccent : kText);
    setTextColor(w.option_note[i], picked[i] ? kOnAccent : kMuted);
    count += picked[i];
  }
  // Done needs at least one.
  if (count) lv_obj_remove_state(w.done, LV_STATE_DISABLED);
  else lv_obj_add_state(w.done, LV_STATE_DISABLED);
}

void onOption(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < 0 || i >= model::question.count) return;
  if (!model::question.multi) {
    finish(&i, 1);
    return;
  }
  picked[i] = !picked[i];
  showPicked();
}

void onDone(lv_event_t *) {
  int picks[kMax], count = 0;
  for (int i = 0; i < model::question.count; i++)
    if (picked[i]) picks[count++] = i;
  if (count) finish(picks, count);
}

void onClose(lv_event_t *) { finish(nullptr, 0); }

void onOpen(lv_event_t *) {
  if (model::question.session.length()) pc::openClaude(model::question.session.c_str());
}

lv_obj_t *action(lv_obj_t *parent, const char *text, uint32_t bg, uint32_t fg, lv_event_cb_t cb) {
  lv_obj_t *b = button(parent, LV_SIZE_CONTENT, kButtonH, bg, cb);
  lv_obj_set_style_radius(b, 12, 0);
  lv_obj_set_style_pad_hor(b, 22, 0);
  lv_obj_t *l = label(b, text, &body_font, fg);
  lv_obj_center(l);
  return b;
}

// The head's icon and color for what's shown.
void look(const char *&icon, const lv_font_t *&font, uint32_t &color) {
  font = &lv_font_montserrat_20;
  const String &level = model::question.level;
  if (!isNote()) icon = glyphOf("comment"), font = &font_icons_15, color = kAccent;
  else if (level == "done") icon = LV_SYMBOL_OK, color = kGreen;
  else if (level == "warning") icon = LV_SYMBOL_WARNING, color = kAmber;
  else if (level == "error") icon = LV_SYMBOL_CLOSE, color = 0xFF9A70;
  else icon = LV_SYMBOL_BELL, color = kAccent;
}

void show() {
  const model::Question &q = model::question;
  const bool note = isNote();
  const char *icon;
  const lv_font_t *font;
  uint32_t color;
  look(icon, font, color);
  setText(w.icon, icon);
  lv_obj_set_style_text_font(w.icon, font, 0);
  setTextColor(w.icon, color);
  setTextColor(w.title, color);
  setBorderColor(w.root, color);
  lv_obj_set_style_bg_color(w.root, lv_color_mix(hex(color), hex(kSurface), 34), 0);

  String title = q.project.length() ? "Claude · " + q.project : String("Claude");
  if (!note) title += " asks";
  if (q.steps > 1) title += " · " + String(q.step) + " of " + String(q.steps);
  if (q.multi) title += " · pick any";
  if (q.waiting > 1) title += "  (more waiting)";
  setText(w.title, title.c_str());
  setText(w.text, q.text.c_str());

  bool notes = false;
  for (int i = 0; i < q.count; i++) notes |= q.options[i].note.length() > 0;
  for (int i = 0; i < kMax; i++) {
    const bool used = !note && i < q.count;
    setHidden(w.option[i], !used);
    if (!used) continue;
    picked[i] = false;
    lv_obj_set_height(w.option[i], notes ? kButtonNoteH : kButtonH);
    setText(w.option_label[i], q.options[i].label.c_str());
    setText(w.option_note[i], q.options[i].note.c_str());
    setHidden(w.option_note[i], !q.options[i].note.length());
  }
  setHidden(w.done, note || !q.multi);
  if (!note) showPicked();  // none picked yet
  setHidden(w.close, note);  // a note's OK does that
  setHidden(w.open, !note || !q.session.length());
  setHidden(w.ok, !note);
}

}  // namespace

void build() {
  w.root = card(lv_layer_top(), kSurface, kAccent);
  lv_obj_set_pos(w.root, kX, kTop);
  lv_obj_set_size(w.root, kW, LV_SIZE_CONTENT);
  lv_obj_set_style_border_width(w.root, 2, 0);
  lv_obj_set_style_radius(w.root, 14, 0);
  lv_obj_set_style_pad_hor(w.root, 14, 0);
  lv_obj_set_style_pad_top(w.root, 8, 0);
  lv_obj_set_style_pad_bottom(w.root, 12, 0);
  lv_obj_set_style_shadow_width(w.root, 24, 0);
  lv_obj_set_style_shadow_color(w.root, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.root, LV_OPA_50, 0);
  lv_obj_set_clickable(w.root, true);  // a tap beside the buttons stays on the card
  flexCol(w.root, 6);

  lv_obj_t *head = box(w.root);
  lv_obj_set_size(head, LV_PCT(100), 32);
  flexRow(head, 10);
  w.icon = label(head, "", &font_icons_15, kAccent);
  w.title = label(head, "", &small_font, kAccent);
  fillRow(w.title);
  w.close = iconButton(head, 44, 32, kSurface, LV_SYMBOL_CLOSE, kMuted, onClose);
  w.close = lv_obj_get_parent(w.close);
  lv_obj_set_ext_click_area(w.close, 6);

  w.text = label(w.root, "", &body_font, kText);
  lv_obj_set_width(w.text, LV_PCT(100));
  lv_label_set_long_mode(w.text, LV_LABEL_LONG_MODE_WRAP);

  w.row = box(w.root);
  lv_obj_set_size(w.row, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_pad_top(w.row, 4, 0);
  flexRow(w.row, 8);
  lv_obj_set_flex_align(w.row, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  for (int i = 0; i < kMax; i++) {
    lv_obj_t *b = button(w.row, 1, kButtonH, kRaised, onOption, (void *)intptr_t(i));
    lv_obj_set_flex_grow(b, 1);
    lv_obj_set_style_radius(b, 12, 0);
    lv_obj_set_style_pad_hor(b, 10, 0);
    flexCol(b, 2);
    lv_obj_set_flex_align(b, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    w.option[i] = b;
    w.option_label[i] = label(b, "", &body_font, kText);
    w.option_note[i] = label(b, "", &small_font, kMuted);
    for (lv_obj_t *l : {w.option_label[i], w.option_note[i]}) {
      lv_obj_set_style_max_width(l, LV_PCT(100), 0);
      lv_label_set_long_mode(l, LV_LABEL_LONG_MODE_DOTS);
      lv_obj_set_style_text_align(l, LV_TEXT_ALIGN_CENTER, 0);
    }
  }
  w.done = action(w.row, "Done", kGreen, kBg, onDone);
  lv_obj_set_style_bg_opa(w.done, LV_OPA_40, LV_STATE_DISABLED);
  w.open = action(w.row, "Open", kRaised, kText, onOpen);
  w.ok = action(w.row, "OK", kAccent, kOnAccent, onClose);
  lv_obj_set_hidden(w.root, true);
}

void update() {
  const model::Question &q = model::question;
  const int online = pc::connected();
  const bool under = approval::visible();
  if (q.version == seen && online == link_seen && under == approval_seen) return;
  const bool fresh = q.version != seen;
  seen = q.version;
  link_seen = online;
  approval_seen = under;
  const bool visible = online && q.id.length() && q.id != answered;
  if (visible && fresh) {
    show();
    idle::wake();  // worth seeing even from the clock screen
  }
  // Under the approval banner while both show.
  lv_obj_set_y(w.root, under ? approval::bottom() + 6 : kTop);
  setHidden(w.root, !visible);
}

}  // namespace ui::ask
