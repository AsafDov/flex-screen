#include "approval.h"

#include <Arduino.h>

#include "fonts/font_icons.h"
#include "idle.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::approval {
namespace {

constexpr int kX = 10, kY = kBarH + 6, kW = 800 - 2 * kX, kH = 70;
constexpr int kButtonH = 46;

struct {
  lv_obj_t *root, *title, *detail, *deny, *always, *approve;
} w;

uint32_t seen = UINT32_MAX;
int link_seen = -1;
String answered;  // the request just answered here: hidden until the companion's next word

void answer(bool allow, bool always) {
  const model::Approval &a = model::approval;
  if (!a.id.length()) return;
  if (allow) pc::approveClaude(a.id.c_str(), always);
  else pc::denyClaude(a.id.c_str());
  answered = a.id;
  lv_obj_set_hidden(w.root, true);  // at once; the companion sends the next request, if any
}

void onApprove(lv_event_t *) { answer(true, false); }
void onAlways(lv_event_t *) { answer(true, true); }
void onDeny(lv_event_t *) { answer(false, false); }

lv_obj_t *action(lv_obj_t *parent, const char *icon, const char *text, uint32_t bg, uint32_t fg, lv_event_cb_t cb) {
  lv_obj_t *b = button(parent, LV_SIZE_CONTENT, kButtonH, bg, cb);
  lv_obj_set_style_radius(b, 12, 0);
  lv_obj_set_style_pad_hor(b, 16, 0);
  flexRow(b, 8);
  label(b, icon, &font_icons_15, fg);
  label(b, text, &body_font, fg);
  return b;
}

}  // namespace

void build() {
  w.root = card(lv_layer_top(), kSurface, kAmber);
  lv_obj_set_pos(w.root, kX, kY);
  lv_obj_set_size(w.root, kW, kH);
  lv_obj_set_style_bg_color(w.root, lv_color_mix(hex(kAmber), hex(kSurface), 40), 0);
  lv_obj_set_style_border_width(w.root, 2, 0);
  lv_obj_set_style_radius(w.root, 14, 0);
  lv_obj_set_style_pad_hor(w.root, 14, 0);
  lv_obj_set_style_pad_ver(w.root, 0, 0);
  lv_obj_set_style_shadow_width(w.root, 24, 0);
  lv_obj_set_style_shadow_color(w.root, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.root, LV_OPA_50, 0);
  lv_obj_set_clickable(w.root, true);  // a tap beside the buttons stays on the banner
  flexRow(w.root, 10);

  label(w.root, LV_SYMBOL_BELL, &lv_font_montserrat_20, kAmber);
  lv_obj_t *text = box(w.root);
  lv_obj_set_size(text, 1, LV_SIZE_CONTENT);
  lv_obj_set_flex_grow(text, 1);
  flexCol(text, 2);
  w.title = label(text, "", &small_font, kAmber);
  ellipsize(w.title);
  w.detail = label(text, "", &body_font, kText);
  ellipsize(w.detail);

  w.deny = action(w.root, glyphOf("deny"), "Deny", kRaised, kText, onDeny);
  w.always = action(w.root, glyphOf("history"), "Always", kRaised, kText, onAlways);
  w.approve = action(w.root, glyphOf("approve"), "Approve", kGreen, kBg, onApprove);
  lv_obj_set_hidden(w.root, true);
}

void update() {
  const model::Approval &a = model::approval;
  const int online = pc::connected();
  if (a.version == seen && online == link_seen) return;
  seen = a.version;
  link_seen = online;
  const bool show = online && a.id.length() && a.id != answered;
  if (show) {
    setTextf(w.title, "Claude · %s wants to%s", a.project.c_str(), a.count > 1 ? "  (more waiting)" : "");
    setText(w.detail, a.detail.c_str());
    setHidden(w.always, !a.always);
    idle::wake();  // worth seeing even from the clock screen
  }
  setHidden(w.root, !show);
}

}  // namespace ui::approval
