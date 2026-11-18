#include "glow.h"

#include <Arduino.h>
#include <lvgl.h>

#include "board.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::glow {
namespace {

// Four strips along the edges rather than one bordered object the size of the screen: a
// change then redraws only the strips, not every pixel (a full redraw takes 64-177 ms).
constexpr int kThick = 10;      // px: solid at the very edge, fading to nothing inward
constexpr uint8_t kSolid = 80;  // of 255 across a strip: the solid part, about 3 px
constexpr uint32_t kFailedForMs = 10 * 60 * 1000;
// Fixed, not the palette's: ember's accent is orange, and would pass for Claude's amber.
constexpr uint32_t kRed = 0xFF4D4D, kBlue = 0x4C8DFF;

enum class Why { None, Call, Build, Claude };

lv_obj_t *strips[4];
Why shown = Why::None;

// One strip; `edge_first` when the screen's edge is at its start (the top or left one).
lv_obj_t *strip(int x, int y, int w, int h, lv_grad_dir_t dir, bool edge_first) {
  lv_obj_t *o = box(lv_layer_sys());  // a box takes no taps: they go to what's under it
  lv_obj_set_pos(o, x, y);
  lv_obj_set_size(o, w, h);
  lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
  lv_obj_set_style_bg_grad_dir(o, dir, 0);
  lv_obj_set_style_bg_main_opa(o, edge_first ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
  lv_obj_set_style_bg_grad_opa(o, edge_first ? LV_OPA_TRANSP : LV_OPA_COVER, 0);
  lv_obj_set_style_bg_main_stop(o, edge_first ? kSolid : 0, 0);
  lv_obj_set_style_bg_grad_stop(o, edge_first ? 255 : 255 - kSolid, 0);
  lv_obj_set_hidden(o, true);
  return o;
}

bool claudeNeedsYou() {
  const model::Claude &c = model::claude;
  for (int i = 0; i < c.count; i++) {
    if (c.sessions[i].state == "input") return true;
  }
  return model::approval.id.length() || (model::question.id.length() && model::question.kind == "ask");
}

bool buildJustFailed() {
  const model::Build &b = model::build;
  if (b.state != "failed") return false;
  const uint32_t age_ms = (b.secs > 0 ? uint32_t(b.secs) * 1000 : 0) + (millis() - b.at_ms);
  return age_ms < kFailedForMs;
}

Why why() {
  if (!pc::connected()) return Why::None;  // what it last said may no longer hold
  if (claudeNeedsYou()) return Why::Claude;
  if (buildJustFailed()) return Why::Build;
  if (model::call.app.length()) return Why::Call;
  return Why::None;
}

}  // namespace

void build() {
  constexpr int W = board::kWidth, H = board::kHeight;
  strips[0] = strip(0, 0, W, kThick, LV_GRAD_DIR_VER, true);           // top
  strips[1] = strip(0, H - kThick, W, kThick, LV_GRAD_DIR_VER, false);  // bottom
  strips[2] = strip(0, 0, kThick, H, LV_GRAD_DIR_HOR, true);           // left
  strips[3] = strip(W - kThick, 0, kThick, H, LV_GRAD_DIR_HOR, false);  // right
}

void update() {
  const Why now = why();
  if (now == shown) return;
  if (now != Why::None) {
    const uint32_t color = now == Why::Claude ? kAmber : now == Why::Build ? kRed : kBlue;
    pc::log("glow: %s", now == Why::Claude ? "amber, Claude needs you" : now == Why::Build ? "red, the build failed" : "blue, in a call");
    for (lv_obj_t *s : strips) {
      lv_obj_set_style_bg_color(s, hex(color), 0);
      lv_obj_set_style_bg_grad_color(s, hex(color), 0);
    }
  } else {
    pc::log("glow: off");
  }
  shown = now;
  for (lv_obj_t *s : strips) setHidden(s, now == Why::None);
}

}  // namespace ui::glow
