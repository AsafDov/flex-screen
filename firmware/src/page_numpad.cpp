#include "page_numpad.h"

#include <math.h>
#include <stdlib.h>

#include "USBHIDKeyboard.h"
#include "model.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::numpad {
namespace {

constexpr int kPanelW = 312;
constexpr uint32_t kOpKey = 0x1A222C;  // operator keys, from the design canvas
constexpr uint32_t kOpText = 0x8CCBFF;
constexpr uint32_t kCodeBg = 0x0A0C0F;
constexpr uint32_t kTypedClearMs = 4000;  // the "typed" readout empties after a pause
constexpr size_t kMaxExpr = 40;

enum class Look { Digit, Op, Enter, Small };

// One key. In keypad mode it's held down on the PC for as long as it's pressed here, so
// Windows repeats it as usual; `text` keys are typed as characters by the companion
// instead (so "(" stays "(" whatever the keyboard layout). `calc` is what it does in
// calculator mode: a character to add, or one of '=' (evaluate), '\n' (evaluate, or type
// the result), '\b' (delete), 'C' (clear), 0 (nothing).
struct Key {
  const char *label;
  uint8_t hid;
  const char *text;
  char calc;
  Look look;
  uint8_t col, row, col_span, row_span;
};

const Key kPad[] = {
    {"Num", KEY_NUM_LOCK, nullptr, 0, Look::Op, 0, 0, 1, 1},
    {"/", KEY_KP_SLASH, nullptr, '/', Look::Op, 1, 0, 1, 1},
    {"*", KEY_KP_ASTERISK, nullptr, '*', Look::Op, 2, 0, 1, 1},
    {"-", KEY_KP_MINUS, nullptr, '-', Look::Op, 3, 0, 1, 1},
    {"7", KEY_KP_7, nullptr, '7', Look::Digit, 0, 1, 1, 1},
    {"8", KEY_KP_8, nullptr, '8', Look::Digit, 1, 1, 1, 1},
    {"9", KEY_KP_9, nullptr, '9', Look::Digit, 2, 1, 1, 1},
    {"+", KEY_KP_PLUS, nullptr, '+', Look::Op, 3, 1, 1, 2},
    {"4", KEY_KP_4, nullptr, '4', Look::Digit, 0, 2, 1, 1},
    {"5", KEY_KP_5, nullptr, '5', Look::Digit, 1, 2, 1, 1},
    {"6", KEY_KP_6, nullptr, '6', Look::Digit, 2, 2, 1, 1},
    {"1", KEY_KP_1, nullptr, '1', Look::Digit, 0, 3, 1, 1},
    {"2", KEY_KP_2, nullptr, '2', Look::Digit, 1, 3, 1, 1},
    {"3", KEY_KP_3, nullptr, '3', Look::Digit, 2, 3, 1, 1},
    {LV_SYMBOL_NEW_LINE "\nEnter", KEY_KP_ENTER, nullptr, '\n', Look::Enter, 3, 3, 1, 2},
    {"0", KEY_KP_0, nullptr, '0', Look::Digit, 0, 4, 2, 1},
    {".", KEY_KP_DOT, nullptr, '.', Look::Digit, 2, 4, 1, 1},
};

const Key kSide[] = {
    {LV_SYMBOL_BACKSPACE, KEY_BACKSPACE, nullptr, '\b', Look::Small, 0, 0, 1, 1},
    {"Tab", KEY_TAB, nullptr, 0, Look::Small, 1, 0, 1, 1},
    {"Esc", KEY_ESC, nullptr, 'C', Look::Small, 2, 0, 1, 1},
    {"Home", KEY_HOME, nullptr, 0, Look::Small, 0, 1, 1, 1},
    {LV_SYMBOL_UP, KEY_UP_ARROW, nullptr, 0, Look::Small, 1, 1, 1, 1},
    {"End", KEY_END, nullptr, 0, Look::Small, 2, 1, 1, 1},
    {LV_SYMBOL_LEFT, KEY_LEFT_ARROW, nullptr, 0, Look::Small, 0, 2, 1, 1},
    {LV_SYMBOL_DOWN, KEY_DOWN_ARROW, nullptr, 0, Look::Small, 1, 2, 1, 1},
    {LV_SYMBOL_RIGHT, KEY_RIGHT_ARROW, nullptr, 0, Look::Small, 2, 2, 1, 1},
};

const Key kSymbols[] = {
    {"(", 0, "(", '(', Look::Op, 0, 0, 1, 1},
    {")", 0, ")", ')', Look::Op, 1, 0, 1, 1},
    {",", 0, ",", 0, Look::Op, 2, 0, 1, 1},
    {"=", 0, "=", '=', Look::Op, 3, 0, 1, 1},
};

struct {
  lv_obj_t *mode_keys, *mode_calc;
  lv_obj_t *hint, *readout;
  lv_obj_t *num_key;
  lv_obj_t *dimmed[sizeof(kSide) / sizeof(kSide[0]) + 1];  // keys that do nothing in calculator mode
  int dimmed_count;
} w;

bool calculator = false;
String typed;  // what keypad mode sent lately, for the readout
uint32_t typed_ms = 0;
String expr;    // calculator mode
String result;  // set once evaluated; the next digit starts over
bool shown_calc = false;
int lock_seen = -1;

// ---- calculator

// Recursive descent over + - * / and parentheses, with unary minus.
struct Parser {
  const char *p;
  bool ok = true;

  double expression() {
    double v = term();
    while (*p == '+' || *p == '-') {
      const char op = *p++;
      const double r = term();
      v = op == '+' ? v + r : v - r;
    }
    return v;
  }

  double term() {
    double v = factor();
    while (*p == '*' || *p == '/') {
      const char op = *p++;
      const double r = factor();
      if (op == '/' && r == 0) ok = false;
      v = op == '*' ? v * r : v / r;
    }
    return v;
  }

  double factor() {
    if (*p == '-') return p++, -factor();
    if (*p == '+') return p++, factor();
    if (*p == '(') {
      p++;
      const double v = expression();
      if (*p == ')') p++;  // a missing ")" at the end is forgiven
      return v;
    }
    char *end;
    const double v = strtod(p, &end);
    if (end == p) ok = false;
    p = end;
    return v;
  }
};

bool evaluate(const String &text, String &out) {
  Parser parser{text.c_str()};
  const double v = parser.expression();
  if (!parser.ok || *parser.p != '\0' || !isfinite(v)) return false;
  char buf[32];
  snprintf(buf, sizeof(buf), "%.10g", v);
  out = buf;
  return true;
}

void calcKey(char c) {
  switch (c) {
    case 'C':
      expr = "";
      result = "";
      break;
    case '\b':
      if (result.length()) result = "";
      else if (expr.length()) expr.remove(expr.length() - 1);
      break;
    case '\n':
      if (result.length() && result != "Error") {  // a second Enter types the answer into the PC
        pc::typeText(result.c_str());
        break;
      }
      [[fallthrough]];
    case '=':
      if (expr.length()) {
        String value;
        result = evaluate(expr, value) ? value : String("Error");
      }
      break;
    default:
      if (result.length()) {  // after "=", an operator carries on from the answer, a digit starts over
        expr = strchr("+-*/", c) && result != "Error" ? result : String();
        result = "";
      }
      if (expr.length() < kMaxExpr) expr += c;
  }
}

// ---- keypad

const Key *keyOf(lv_event_t *e) { return static_cast<const Key *>(lv_event_get_user_data(e)); }

void onKey(lv_event_t *e) {
  const Key &k = *keyOf(e);
  const lv_event_code_t code = lv_event_get_code(e);
  if (calculator) {
    if (code == LV_EVENT_CLICKED && k.calc) calcKey(k.calc);
    return;
  }
  if (k.text) {
    if (code == LV_EVENT_CLICKED) {
      pc::typeText(k.text);
      typed += k.text;
      typed_ms = millis();
    }
    return;
  }
  if (code == LV_EVENT_PRESSED) {
    // Keypad digits are arrows with Num Lock off; a numpad is for numbers.
    if (k.look == Look::Digit && pc::numLockKnown() && !pc::numLock()) pc::tapKey(KEY_NUM_LOCK);
    pc::pressKey(k.hid);
    if (k.look == Look::Digit || (k.look == Look::Op && k.hid != KEY_NUM_LOCK)) typed += k.calc;
    if (k.hid == KEY_KP_ENTER || k.hid == KEY_ESC) typed = "";
    if (k.hid == KEY_BACKSPACE && typed.length()) typed.remove(typed.length() - 1);
    typed_ms = millis();
  } else {  // released, or the finger slid off
    pc::releaseKey(k.hid);
  }
}

void onMode(lv_event_t *e) {
  calculator = lv_event_get_user_data(e) != nullptr;
  pc::releaseAllKeys();
}

// ---- building

lv_obj_t *keyButton(lv_obj_t *grid, const Key &k) {
  const uint32_t bg = k.look == Look::Enter ? kAccent : k.look == Look::Op ? kOpKey : kRaised;
  lv_obj_t *b = button(grid, 0, 0, bg, nullptr);
  lv_obj_set_style_radius(b, 12, 0);
  lv_obj_set_grid_cell(b, LV_GRID_ALIGN_STRETCH, k.col, k.col_span, LV_GRID_ALIGN_STRETCH, k.row, k.row_span);
  // Typing on the pad isn't a page swipe, and a swipe must never leave a key held down.
  lv_obj_set_gesture_bubble(b, false);
  const void *user = &k;
  for (lv_event_code_t code : {LV_EVENT_PRESSED, LV_EVENT_RELEASED, LV_EVENT_PRESS_LOST, LV_EVENT_CLICKED}) {
    lv_obj_add_event_cb(b, onKey, code, const_cast<void *>(user));
  }
  const lv_font_t *font = k.look == Look::Digit ? &lv_font_montserrat_28
                          : k.look == Look::Small ? &body_font
                          : k.look == Look::Enter ? &body_font
                                                  : &lv_font_montserrat_20;
  const uint32_t color = k.look == Look::Enter ? kOnAccent : k.look == Look::Op ? kOpText : kText;
  lv_obj_t *l = label(b, k.label, font, k.hid == KEY_BACKSPACE ? 0xFF9A70 : color);
  lv_obj_set_style_text_align(l, LV_TEXT_ALIGN_CENTER, 0);
  lv_obj_center(l);
  return b;
}

lv_obj_t *grid(lv_obj_t *parent, const int32_t *cols, const int32_t *rows) {
  lv_obj_t *g = box(parent);
  lv_obj_set_grid_dsc_array(g, cols, rows);
  lv_obj_set_style_pad_row(g, 8, 0);
  lv_obj_set_style_pad_column(g, 8, 0);
  return g;
}

lv_obj_t *modeButton(lv_obj_t *parent, const char *text, bool calc) {
  lv_obj_t *b = button(parent, 0, 40, kSurface, onMode, calc ? reinterpret_cast<void *>(1) : nullptr);
  lv_obj_set_flex_grow(b, 1);
  lv_obj_set_style_radius(b, 9, 0);
  lv_obj_center(label(b, text, &small_font, kMuted));
  return b;
}

void buildPanel(lv_obj_t *row) {
  lv_obj_t *c = card(row);
  lv_obj_set_size(c, kPanelW, LV_PCT(100));
  flexCol(c, 10);

  lv_obj_t *modes = card(c, kCodeBg, kCodeBg);
  lv_obj_set_size(modes, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_pad_all(modes, 4, 0);
  lv_obj_set_style_radius(modes, 12, 0);
  flexRow(modes, 4);
  w.mode_keys = modeButton(modes, "Send keys", false);
  w.mode_calc = modeButton(modes, "Calculator", true);

  lv_obj_t *display = card(c, kCodeBg, kCodeBg);
  lv_obj_set_size(display, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_pad_hor(display, 12, 0);
  lv_obj_set_style_pad_ver(display, 8, 0);
  flexCol(display, 2);
  w.hint = label(display, "", &small_font, kMuted);
  ellipsize(w.hint);
  w.readout = label(display, "", &lv_font_montserrat_28, kText);
  ellipsize(w.readout);
  lv_obj_set_style_text_align(w.readout, LV_TEXT_ALIGN_RIGHT, 0);

  static const int32_t side_cols[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  static const int32_t side_rows[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  lv_obj_t *side = grid(c, side_cols, side_rows);
  lv_obj_set_width(side, LV_PCT(100));
  lv_obj_set_flex_grow(side, 1);
  for (const Key &k : kSide) {
    lv_obj_t *b = keyButton(side, k);
    if (!k.calc) w.dimmed[w.dimmed_count++] = b;
  }

  static const int32_t sym_cols[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  static const int32_t sym_rows[] = {44, LV_GRID_TEMPLATE_LAST};
  lv_obj_t *symbols = grid(c, sym_cols, sym_rows);
  lv_obj_set_size(symbols, LV_PCT(100), 44);
  for (const Key &k : kSymbols) {
    lv_obj_t *b = keyButton(symbols, k);
    if (!k.calc) w.dimmed[w.dimmed_count++] = b;
  }
}

// ---- updating

void showMode() {
  lv_obj_t *on = calculator ? w.mode_calc : w.mode_keys;
  lv_obj_t *off = calculator ? w.mode_keys : w.mode_calc;
  lv_obj_set_style_bg_opa(on, LV_OPA_COVER, 0);
  lv_obj_set_style_bg_color(on, hex(kSelected), 0);
  lv_obj_set_style_bg_opa(off, LV_OPA_TRANSP, 0);
  setTextColor(lv_obj_get_child(on, 0), kText);
  setTextColor(lv_obj_get_child(off, 0), kMuted);
  for (int i = 0; i < w.dimmed_count; i++) lv_obj_set_style_opa(w.dimmed[i], calculator ? LV_OPA_30 : LV_OPA_COVER, 0);
}

void showReadout() {
  if (calculator) {
    setText(w.hint, result.length() ? "Enter types the result into the PC" : "Calculator");
    setText(w.readout, result.length() ? result.c_str() : expr.length() ? expr.c_str() : "0");
    return;
  }
  if (typed.length() && millis() - typed_ms > kTypedClearMs) typed = "";
  if (typed.length() > 14) typed.remove(0, typed.length() - 14);
  const String &app = model::zones.app;
  if (app.length()) setTextf(w.hint, "Typing into %s via USB", app.c_str());
  else setText(w.hint, "Typing via USB");
  setText(w.readout, typed.c_str());
}

void showNumLock() {
  const bool on = pc::numLockKnown() && pc::numLock();
  setBgColor(w.num_key, on ? kSelected : kOpKey);
  lv_obj_set_style_border_color(w.num_key, hex(kAccent), 0);
  lv_obj_set_style_border_width(w.num_key, on ? 2 : 0, 0);
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  flexRow(page, kGap);
  lv_obj_set_flex_align(page, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);

  static const int32_t cols[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  static const int32_t rows[] = {LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_FR(1), LV_GRID_TEMPLATE_LAST};
  lv_obj_t *pad = grid(page, cols, rows);
  lv_obj_set_height(pad, LV_PCT(100));
  lv_obj_set_flex_grow(pad, 1);
  for (const Key &k : kPad) {
    lv_obj_t *b = keyButton(pad, k);
    if (k.hid == KEY_NUM_LOCK) w.num_key = b;
  }
  buildPanel(page);
  showMode();
  showNumLock();
  showReadout();
}

void update() {
  // Cheap enough every loop: every setter skips unchanged values.
  if (calculator != shown_calc) {
    shown_calc = calculator;
    showMode();
  }
  const int lock = pc::numLockKnown() ? int(pc::numLock()) : -1;
  if (lock != lock_seen) {
    lock_seen = lock;
    showNumLock();
  }
  showReadout();
}

}  // namespace ui::numpad
