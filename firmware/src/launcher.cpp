#include "launcher.h"

#include <Arduino.h>
#include <time.h>

#include "icons.h"
#include "idle.h"
#include "model.h"
#include "page_numpad.h"
#include "pc.h"
#include "ui_kit.h"

namespace ui::launcher {
namespace {

// Square tiles, six to a row: twelve make two rows. The panel is as wide as they need and
// sits above the launcher button.
constexpr int kCols = 6;
constexpr int kTile = 104;
constexpr int kTileGap = 8;
constexpr int kPad = 14, kBorderW = 2;
constexpr int kW = kCols * kTile + (kCols - 1) * kTileGap + 2 * (kPad + kBorderW);
constexpr int kHeadH = 40;  // the Numpad button's height
constexpr int kBadge = 40;
constexpr int kButtonH = 44;
constexpr uint32_t kIconRetryMs = 3000;  // asking again for an icon the deck lacks

constexpr int kTilePadHor = 5;
constexpr int kNameW = kTile - 2 * kTilePadHor;

struct Tile {
  lv_obj_t *root, *badge, *image, *glyph, *name;
  uint32_t asked_ms;
  int lines;  // the name's height, in lines (1 or 2)
};

// The launcher's symbol is a face, the deck's familiar: two square eyes and a square mouth
// (it was four squares, a grid). On the rail's button it shows how things are going.
struct Face {
  lv_obj_t *root, *eye[2], *mouth;
};

struct {
  lv_obj_t *button;
  Face face;
  lv_obj_t *scrim, *panel, *note, *grid;
  Tile tiles[model::Shortcuts::kMax];
} w;

bool is_open = false;
bool open_seen = true;
uint32_t shortcuts_seen = UINT32_MAX, icons_seen = UINT32_MAX, tick_ms = 0;
int link_seen = -1;
int64_t day_seen = -1;

void setOpen(bool open) {
  is_open = open;
  if (open) shortcuts_seen = UINT32_MAX;  // fill it afresh
}

void onButton(lv_event_t *) { setOpen(!is_open); }
void onScrim(lv_event_t *) { setOpen(false); }
// The Numpad isn't a page: it pops up from here, over whatever page is on show.
void onNumpad(lv_event_t *) {
  setOpen(false);
  numpad::open();
}

void onTile(lv_event_t *e) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i < model::shortcuts.count && pc::connected()) pc::runShortcut(model::shortcuts.list[i].id.c_str());
  setOpen(false);
}

// ---- the face
//
// Drawn in a 22 px square: the eyes 8 px squares at the top corners, the mouth an 8 px
// square under the middle. Its moods, the first that applies:
//   asleep    the PC is away: eyes shut, a small mouth, faded
//   alert     Claude needs you (an approval, a question): wide eyes, an open mouth, amber,
//             bobbing
//   glum      a build failed in the last minute: low eyes, a flat mouth
//   busy      Claude is at work: eyes glancing side to side, a small mouth, in the accent
//   pleased   Claude finished in the last minute: squinting eyes, a wide mouth, green
//   drowsy    the PC has had no input for 5 minutes: eyelids half down
//   awake     the rest: the three squares
// Awake and busy it blinks every few seconds. Only a change of pose redraws it, and only
// its own few pixels.
constexpr int kFace = 22, kEye = 8;
constexpr uint32_t kRecentMs = 60 * 1000;      // pleased and glum last this long
constexpr uint32_t kDrowsyS = 5 * 60;          // the PC idle this long
constexpr uint32_t kBlinkMs = 130;
constexpr uint32_t kGlanceMs = 700;            // busy: a glance this often
constexpr uint32_t kBobMs = 450;               // alert: up, then down

enum class Mood { Asleep, Alert, Glum, Busy, Pleased, Drowsy, Awake };

// Where the parts are: the left eye's box (the right one mirrors it), the eyes moved
// sideways together, the mouth's size and top (it stays centered), the whole moved up,
// the color and the opacity.
struct Pose {
  int8_t ex, ey, ew, eh, glance, mw, mh, my, lift;
  uint32_t color;
  lv_opa_t opa;
  bool operator==(const Pose &o) const {
    return ex == o.ex && ey == o.ey && ew == o.ew && eh == o.eh && glance == o.glance && mw == o.mw && mh == o.mh &&
           my == o.my && lift == o.lift && color == o.color && opa == o.opa;
  }
};

Pose awake(uint32_t color) { return {0, 1, kEye, kEye, 0, kEye, kEye, 14, 0, color, LV_OPA_COVER}; }

lv_obj_t *square(lv_obj_t *parent, uint32_t color) {
  lv_obj_t *s = card(parent, color, color);
  lv_obj_set_style_pad_all(s, 0, 0);
  lv_obj_set_style_border_width(s, 0, 0);
  lv_obj_set_style_radius(s, 2, 0);
  return s;
}

void place(lv_obj_t *o, int x, int y, int w, int h) {
  if (lv_obj_get_style_x(o, LV_PART_MAIN) != x || lv_obj_get_style_y(o, LV_PART_MAIN) != y) lv_obj_set_pos(o, x, y);
  if (lv_obj_get_style_width(o, LV_PART_MAIN) != w || lv_obj_get_style_height(o, LV_PART_MAIN) != h) lv_obj_set_size(o, w, h);
}

void setPose(const Face &f, const Pose &p) {
  place(f.eye[0], p.ex + p.glance, p.ey, p.ew, p.eh);
  place(f.eye[1], kFace - p.ex - p.ew + p.glance, p.ey, p.ew, p.eh);
  place(f.mouth, (kFace - p.mw) / 2, p.my, p.mw, p.mh);
  for (lv_obj_t *o : {f.eye[0], f.eye[1], f.mouth}) setBgColor(o, p.color);
  if (lv_obj_get_style_opa(f.root, LV_PART_MAIN) != p.opa) lv_obj_set_style_opa(f.root, p.opa, 0);
  // Moved as a whole (the parts would be cut off at its top).
  if (lv_obj_get_style_translate_y(f.root, LV_PART_MAIN) != -p.lift) lv_obj_set_style_translate_y(f.root, -p.lift, 0);
}

Face face(lv_obj_t *parent, uint32_t color) {
  Face f;
  f.root = box(parent);
  lv_obj_set_size(f.root, kFace, kFace);
  for (lv_obj_t *&eye : f.eye) eye = square(f.root, color);
  f.mouth = square(f.root, color);
  setPose(f, awake(color));
  return f;
}

// Moments that last a minute: a build failing, Claude finishing.
uint32_t build_seen = UINT32_MAX, failed_ms = 0;
String claude_state;
uint32_t done_ms = 0;
bool failed_recently = false, done_recently = false;

// From when it happened, as the PC says (a build that failed an hour ago, or a session done
// long since, isn't news when the deck reconnects).
void noteMoments() {
  const uint32_t now = millis();
  const model::Build &b = model::build;
  if (b.version != build_seen) {
    build_seen = b.version;
    const uint32_t ago_ms = b.secs > 0 ? uint32_t(b.secs) * 1000 + (now - b.at_ms) : 0;
    if (b.state == "failed" && !failed_recently) failed_ms = ago_ms < kRecentMs ? now - ago_ms : 0;
  }
  failed_recently = b.state == "failed" && failed_ms && now - failed_ms < kRecentMs;
  const bool any = model::claude.count > 0;
  const String &state = any ? model::claude.sessions[0].state : String();
  if (state != claude_state) {
    const int32_t secs = any ? model::claude.sessions[0].secs : 0;
    done_ms = state == "done" && secs < int32_t(kRecentMs / 1000) ? now - uint32_t(secs > 0 ? secs : 0) * 1000 : 0;
    claude_state = state;
  }
  done_recently = state == "done" && done_ms && now - done_ms < kRecentMs;
}

Mood mood() {
  if (!pc::connected()) return Mood::Asleep;
  noteMoments();
  const bool asks = model::question.id.length() && model::question.kind == "ask";
  if (claude_state == "input" || model::approval.id.length() || asks) return Mood::Alert;
  if (failed_recently) return Mood::Glum;
  if (claude_state == "working") return Mood::Busy;
  if (done_recently) return Mood::Pleased;
  const model::Pc &p = model::pc;
  if (!p.locked && p.at_ms && p.idle_s + (millis() - p.at_ms) / 1000 >= kDrowsyS) return Mood::Drowsy;
  return Mood::Awake;
}

// Every few seconds, now and then twice; the gaps vary so it doesn't tick like a clock.
bool blinking(uint32_t now) {
  static uint32_t next_ms = 3000, end_ms = 0, count = 0;
  if (int32_t(now - end_ms) < 0) return true;
  if (int32_t(now - next_ms) < 0) return false;
  count++;
  end_ms = now + kBlinkMs;
  const bool twice = count % 5 == 3;
  next_ms = now + (twice ? kBlinkMs + 160 : 3500 + (count * 1371) % 3000);
  return true;
}

Pose pose(Mood m, bool open) {
  const uint32_t now = millis();
  const uint32_t base = open ? kText : kMuted;
  Pose p = awake(base);
  switch (m) {
    case Mood::Asleep:
      p = {0, 5, kEye, 2, 0, 6, 3, 16, 0, kMuted, LV_OPA_50};
      break;
    case Mood::Alert:
      p = {0, 0, 9, 9, 0, 6, 8, 13, int8_t((now / kBobMs) % 2 ? 2 : 0), kAmber, LV_OPA_COVER};
      break;
    case Mood::Glum:
      p = {0, 4, kEye, 5, 0, 12, 3, 17, 0, 0xFF9A70, LV_OPA_COVER};
      break;
    case Mood::Busy: {
      static const int8_t kGlances[] = {0, -2, 0, 2};
      p = {0, 1, kEye, kEye, kGlances[(now / kGlanceMs) % 4], 6, 4, 16, 0, kAccent, LV_OPA_COVER};
      break;
    }
    case Mood::Pleased:
      p = {0, 2, kEye, 4, 0, 14, 5, 15, 0, kGreen, LV_OPA_COVER};
      break;
    case Mood::Drowsy:
      p = {0, 5, kEye, 4, 0, 6, 4, 16, 0, base, LV_OPA_COVER};
      break;
    case Mood::Awake:
      break;
  }
  const bool can_blink = m == Mood::Awake || m == Mood::Busy || m == Mood::Alert;
  if (can_blink && blinking(now)) {
    p.ey += p.eh / 2 - 1;
    p.eh = 2;
  }
  return p;
}

// A tile's symbol and colors when it has no icon, by the name the companion gave it.
struct Look {
  const char *glyph;
  uint32_t bg, fg;
};

Look look(const String &glyph) {
  if (glyph == "mail") return {LV_SYMBOL_ENVELOPE, 0x3A2A10, kAmber};
  if (glyph == "calendar") return {"", kHighlight, kAccent};  // today's date
  if (glyph == "note") return {LV_SYMBOL_FILE, 0x2B2142, 0xB49CFF};
  if (glyph == "terminal") return {">_", 0x0A0C0F, kAccent};
  if (glyph == "code") return {"</>", 0x10273D, kAccent};
  if (glyph == "music" || glyph == "media") return {LV_SYMBOL_PLAY, 0x143A2E, kGreen};
  if (glyph == "volume") return {LV_SYMBOL_VOLUME_MAX, kHighlight, kAccent};
  if (glyph == "lock") return {LV_SYMBOL_POWER, 0x3A1A1A, 0xFF8A80};
  if (glyph == "desktop") return {LV_SYMBOL_HOME, kSurface, kText};
  if (glyph == "folder") return {LV_SYMBOL_DIRECTORY, 0x3A2A10, kAmber};
  if (glyph == "keys") return {LV_SYMBOL_KEYBOARD, kSurface, kText};
  if (glyph == "git") return {LV_SYMBOL_SHUFFLE, 0x3A2318, 0xE8936A};
  if (glyph == "call") return {LV_SYMBOL_CALL, 0x143A2E, kGreen};
  return {"", 0x2D5C8A, kText};  // the name's first letter
}

void buildTiles() {
  const int line = lv_font_get_line_height(&small_font);
  for (int i = 0; i < model::Shortcuts::kMax; i++) {
    Tile &t = w.tiles[i];
    t.asked_ms = 0;
    t.root = button(w.grid, kTile, kTile, kRaised, onTile, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(t.root, 14, 0);
    lv_obj_set_style_pad_ver(t.root, 6, 0);
    lv_obj_set_style_pad_hor(t.root, kTilePadHor, 0);
    flexCol(t.root, 7);
    // The badge and the name together, in the middle of the square.
    lv_obj_set_flex_align(t.root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);

    t.badge = card(t.root, kSurface, kSurface);
    lv_obj_set_size(t.badge, kBadge, kBadge);
    lv_obj_set_style_pad_all(t.badge, 0, 0);
    lv_obj_set_style_radius(t.badge, 11, 0);
    t.image = lv_image_create(t.badge);
    lv_obj_set_size(t.image, icons::kSize, icons::kSize);
    lv_obj_center(t.image);
    t.glyph = label(t.badge, "", &lv_font_montserrat_20, kText);
    lv_obj_center(t.glyph);

    // Up to two lines of name, then "…" (which needs a fixed height: one line or two, set
    // to what the name takes).
    t.name = label(t.root, "", &small_font, kText);
    lv_obj_set_width(t.name, kNameW);
    lv_obj_set_style_text_align(t.name, LV_TEXT_ALIGN_CENTER, 0);
    lv_label_set_long_mode(t.name, LV_LABEL_LONG_MODE_DOTS);
    t.lines = 1;
    lv_obj_set_height(t.name, line);
  }
}

// How many lines (1 or 2) a name takes in a tile.
int nameLines(const char *name) {
  lv_point_t size;
  lv_text_get_size(&size, name, &small_font, 0, 0, kNameW, LV_TEXT_FLAG_NONE);
  return size.y > lv_font_get_line_height(&small_font) ? 2 : 1;
}

void fill() {
  const model::Shortcuts &s = model::shortcuts;
  const bool online = pc::connected();
  const int count = online ? s.count : 0;
  setText(w.note, !online ? "PC offline" : "No shortcuts yet. Add apps, links and actions from the tray icon on the PC: Shortcuts…");
  setHidden(w.note, count > 0);
  setHidden(w.grid, count == 0);

  const int64_t now = model::localNow();
  char day[4] = "";
  if (now > 0) {
    const time_t t = time_t(now);
    struct tm tm;
    gmtime_r(&t, &tm);  // already shifted to local time
    snprintf(day, sizeof(day), "%d", tm.tm_mday);
  }
  for (int i = 0; i < model::Shortcuts::kMax; i++) {
    Tile &t = w.tiles[i];
    setHidden(t.root, i >= count);
    if (i >= count) continue;
    const model::Shortcut &x = s.list[i];
    const int lines = nameLines(x.name.c_str());
    if (lines != t.lines) {
      t.lines = lines;
      lv_obj_set_height(t.name, lines * lv_font_get_line_height(&small_font));
      lv_label_set_text(t.name, "");  // cut again for the new height
    }
    setText(t.name, x.name.c_str());
    const lv_image_dsc_t *icon = icons::get(x.icon);
    if (!icon && x.icon.length() && millis() - t.asked_ms >= kIconRetryMs) {
      t.asked_ms = millis();
      pc::requestIcon(x.icon.c_str());
    }
    setHidden(t.image, icon == nullptr);
    setHidden(t.glyph, icon != nullptr);
    if (icon) {
      if (lv_image_get_src(t.image) != icon) lv_image_set_src(t.image, icon);
      setBgColor(t.badge, kSurface);
      continue;
    }
    const Look l = look(x.glyph);
    char letter[2] = {x.name.length() ? char(toupper(x.name[0])) : '?', 0};
    const bool calendar = x.glyph == "calendar" && day[0];
    setText(t.glyph, calendar ? day : l.glyph[0] ? l.glyph : letter);
    setTextColor(t.glyph, l.fg);
    setBgColor(t.badge, l.bg);
  }
}

}  // namespace

void build(lv_obj_t *rail) {
  w.button = box(rail);
  lv_obj_set_size(w.button, LV_PCT(100), kButtonH);
  lv_obj_set_clickable(w.button, true);
  lv_obj_set_style_radius(w.button, 10, 0);
  lv_obj_set_style_bg_color(w.button, hex(kSelected), LV_STATE_PRESSED);
  lv_obj_set_style_bg_opa(w.button, LV_OPA_COVER, LV_STATE_PRESSED);
  lv_obj_add_event_cb(w.button, onButton, LV_EVENT_CLICKED, nullptr);
  w.face = face(w.button, kMuted);
  lv_obj_center(w.face.root);

  // Taps beside the panel close it rather than reaching the page underneath.
  w.scrim = box(lv_layer_top());
  lv_obj_set_size(w.scrim, 800, 480);
  lv_obj_set_clickable(w.scrim, true);
  lv_obj_add_event_cb(w.scrim, onScrim, LV_EVENT_CLICKED, nullptr);
  lv_obj_set_hidden(w.scrim, true);

  w.panel = card(lv_layer_top(), kSurface, kBorder);
  lv_obj_set_size(w.panel, kW, LV_SIZE_CONTENT);
  lv_obj_align(w.panel, LV_ALIGN_BOTTOM_LEFT, kColX, -(480 - kRailH));  // beside the rail's button, above the context bar
  lv_obj_set_style_border_width(w.panel, kBorderW, 0);
  lv_obj_set_style_radius(w.panel, 18, 0);
  lv_obj_set_style_pad_all(w.panel, kPad, 0);
  lv_obj_set_style_shadow_width(w.panel, 30, 0);
  lv_obj_set_style_shadow_color(w.panel, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.panel, LV_OPA_60, 0);
  lv_obj_set_clickable(w.panel, true);  // a tap between tiles stays on the panel
  flexCol(w.panel, 10);

  lv_obj_t *head = box(w.panel);
  lv_obj_set_size(head, LV_PCT(100), kHeadH);
  flexRow(head, 10);
  face(head, kMuted);
  label(head, "Shortcuts", &title_font, kText);
  lv_obj_t *gap = box(head);
  lv_obj_set_size(gap, 1, 1);
  lv_obj_set_flex_grow(gap, 1);
  label(head, "Change them from the tray icon: Shortcuts…", &small_font, kMuted);
  lv_obj_t *numpad = button(head, LV_SIZE_CONTENT, kHeadH, kRaised, onNumpad);
  lv_obj_set_style_radius(numpad, 10, 0);
  lv_obj_set_style_pad_hor(numpad, 14, 0);
  lv_obj_set_style_margin_left(numpad, 6, 0);
  flexRow(numpad, 8);
  label(numpad, LV_SYMBOL_KEYBOARD, &lv_font_montserrat_16, kAccent);
  label(numpad, "Numpad", &body_font, kText);

  w.note = label(w.panel, "", &body_font, kMuted);
  lv_obj_set_width(w.note, LV_PCT(100));
  lv_label_set_long_mode(w.note, LV_LABEL_LONG_MODE_WRAP);
  lv_obj_set_style_pad_ver(w.note, 18, 0);

  w.grid = box(w.panel);
  lv_obj_set_size(w.grid, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(w.grid, LV_FLEX_FLOW_ROW_WRAP);
  lv_obj_set_style_pad_column(w.grid, kTileGap, 0);
  lv_obj_set_style_pad_row(w.grid, kTileGap, 0);
  buildTiles();
  lv_obj_set_hidden(w.panel, true);
}

void open() { setOpen(true); }
void close() { setOpen(false); }

void update() {
  if (is_open && idle::asleep()) setOpen(false);  // woken up, it's the page again
  if (is_open != open_seen) {
    open_seen = is_open;
    setHidden(w.scrim, !is_open);
    setHidden(w.panel, !is_open);
    lv_obj_set_style_bg_color(w.button, hex(kSelected), 0);
    lv_obj_set_style_bg_opa(w.button, is_open ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
  }
  if (!idle::asleep()) {  // under the idle clock nobody sees it
    static Pose shown = {};
    const Pose p = pose(mood(), is_open);
    if (!(p == shown)) {
      shown = p;
      setPose(w.face, p);
    }
  }
  if (!is_open) return;  // filled when it opens
  const int online = pc::connected();
  const int64_t day = model::localNow() / 86400;
  const bool icon_due = millis() - tick_ms >= kIconRetryMs;
  if (model::shortcuts.version == shortcuts_seen && icons::version() == icons_seen && online == link_seen && day == day_seen && !icon_due) {
    return;
  }
  shortcuts_seen = model::shortcuts.version;
  icons_seen = icons::version();
  link_seen = online;
  day_seen = day;
  tick_ms = millis();
  fill();
}

}  // namespace ui::launcher
