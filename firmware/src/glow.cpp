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
// change then redraws only the strips, not every pixel (a full redraw takes 64-177 ms),
// which matters while it blinks and fades.
constexpr int kThick = 10;      // px: solid at the very edge, fading to nothing inward
constexpr uint8_t kSolid = 80;  // of 255 across a strip: the solid part, about 3 px
constexpr uint32_t kFailedForMs = 10 * 60 * 1000;
// Fixed, not the palette's: ember's accent is orange, and would pass for Claude's amber.
constexpr uint32_t kRed = 0xFF4D4D, kBlue = 0x4C8DFF;

// Blink, blink, then a last flash that fades away. ms from the start of each step.
constexpr uint32_t kOn1 = 300, kOff1 = 500, kOn2 = 800, kOff2 = 1000, kHold = 1300, kEnd = 2800;

enum class Why { None, Call, Build, Claude };

struct Strip {
  lv_obj_t *obj;
  bool edge_first;  // the screen's edge is at its start (the top or left one)
};
Strip strips[4];

// What held as of the last look, so a glow starts only when something new does.
bool was_claude = false, was_build = false, was_call = false;
uint32_t started_ms = 0;
bool running = false;
lv_opa_t shown_opa = 0;

lv_obj_t *strip(int x, int y, int w, int h, lv_grad_dir_t dir, bool edge_first) {
  lv_obj_t *o = box(lv_layer_sys());  // a box takes no taps: they go to what's under it
  lv_obj_set_pos(o, x, y);
  lv_obj_set_size(o, w, h);
  lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
  lv_obj_set_style_bg_grad_dir(o, dir, 0);
  lv_obj_set_style_bg_main_opa(o, LV_OPA_TRANSP, 0);
  lv_obj_set_style_bg_grad_opa(o, LV_OPA_TRANSP, 0);
  lv_obj_set_style_bg_main_stop(o, edge_first ? kSolid : 0, 0);
  lv_obj_set_style_bg_grad_stop(o, edge_first ? 255 : 255 - kSolid, 0);
  lv_obj_set_hidden(o, true);
  return o;
}

// The strips at this strength: the edge's end of the gradient carries it, the inner end
// stays clear. (Rather than the object's opacity, which LVGL draws through a layer.)
void setOpa(lv_opa_t opa) {
  if (opa == shown_opa) return;
  shown_opa = opa;
  for (const Strip &s : strips) {
    if (s.edge_first) lv_obj_set_style_bg_main_opa(s.obj, opa, 0);
    else lv_obj_set_style_bg_grad_opa(s.obj, opa, 0);
    setHidden(s.obj, opa == LV_OPA_TRANSP);
  }
}

bool claudeNeedsYou() {
  const model::Claude &c = model::claude;
  for (int i = 0; i < c.count; i++) {
    if (c.sessions[i].state == "input") return true;
  }
  return model::approval.id.length() || (model::question.id.length() && model::question.kind == "ask");
}

// A failed build stays the latest result until the next one, so only a fresh failure counts:
// coming back online to one from an hour ago shouldn't blink.
bool buildJustFailed() {
  const model::Build &b = model::build;
  if (b.state != "failed") return false;
  const uint32_t age_ms = (b.secs > 0 ? uint32_t(b.secs) * 1000 : 0) + (millis() - b.at_ms);
  return age_ms < kFailedForMs;
}

void start(Why why) {
  const uint32_t color = why == Why::Claude ? kAmber : why == Why::Build ? kRed : kBlue;
  for (const Strip &s : strips) {
    lv_obj_set_style_bg_color(s.obj, hex(color), 0);
    lv_obj_set_style_bg_grad_color(s.obj, hex(color), 0);
  }
  started_ms = millis();
  running = true;
  pc::log("glow: %s", why == Why::Claude ? "amber, Claude needs you" : why == Why::Build ? "red, the build failed" : "blue, a call");
}

// Where the blink is: full on, off, or fading out (ease out: quick at first, then slower).
lv_opa_t opaAt(uint32_t t) {
  if (t < kOn1 || (t >= kOff1 && t < kOn2) || (t >= kOff2 && t < kHold)) return LV_OPA_COVER;
  if (t < kOff2) return LV_OPA_TRANSP;
  if (t >= kEnd) return LV_OPA_TRANSP;
  const uint32_t left = kEnd - t, span = kEnd - kHold;
  return lv_opa_t(uint32_t(LV_OPA_COVER) * left * left / (span * span));
}

}  // namespace

void build() {
  constexpr int W = board::kWidth, H = board::kHeight;
  strips[0] = {strip(0, 0, W, kThick, LV_GRAD_DIR_VER, true), true};             // top
  strips[1] = {strip(0, H - kThick, W, kThick, LV_GRAD_DIR_VER, false), false};  // bottom
  strips[2] = {strip(0, 0, kThick, H, LV_GRAD_DIR_HOR, true), true};             // left
  strips[3] = {strip(W - kThick, 0, kThick, H, LV_GRAD_DIR_HOR, false), false};  // right
}

void update() {
  // Offline nothing new comes, and what the PC last said may no longer hold: the edges are
  // left as they were, so its coming back doesn't blink again for what's still the same.
  if (pc::connected()) {
    const bool claude = claudeNeedsYou(), build = buildJustFailed(), call = model::call.app.length() > 0;
    Why now = Why::None;
    if (claude && !was_claude) now = Why::Claude;
    else if (build && !was_build) now = Why::Build;
    else if (call && !was_call) now = Why::Call;
    was_claude = claude, was_build = build, was_call = call;
    if (now != Why::None) start(now);
  }
  if (!running) return;
  const uint32_t t = millis() - started_ms;
  setOpa(opaAt(t));
  if (t >= kEnd) running = false;
}

}  // namespace ui::glow
