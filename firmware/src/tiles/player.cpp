// The Player tile (Audio): what's playing as a band across the page: the keys on the left
// (play/pause, skip, shuffle, repeat), the track and its progress in the middle (a slider
// to seek when the app can), the album on the right. In the cover's color, as Today's Now
// Playing. One of a kind (the manager places each kind once), so its state lives here.
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kProgressEveryMs = 250;

struct {
  lv_obj_t *card;
  lv_obj_t *holder;  // the album art's: as tall as the card's inside, and square
  lv_obj_t *art_icon;
  lv_obj_t *art;
  lv_obj_t *app;
  lv_obj_t *device;
  lv_obj_t *title;
  lv_obj_t *artist;
  lv_obj_t *progress;
  lv_obj_t *elapsed;
  lv_obj_t *total;
  lv_obj_t *shuffle;
  lv_obj_t *shuffle_icon;
  lv_obj_t *play_icon;
  lv_obj_t *repeat;
  lv_obj_t *repeat_icon;
} w;

uint32_t media_seen = UINT32_MAX;
uint32_t devices_seen = UINT32_MAX;
int connected_seen = -1;
int32_t art_size = -1;
uint32_t progress_ms = 0;
uint32_t accent = 0;  // the band's color as drawn

void onPrev(lv_event_t *) { pc::media(pc::MediaCmd::Prev); }
void onToggle(lv_event_t *) { pc::media(pc::MediaCmd::Toggle); }
void onNext(lv_event_t *) { pc::media(pc::MediaCmd::Next); }
void onShuffle(lv_event_t *) { pc::toggleShuffle(); }
void onRepeat(lv_event_t *) { pc::cycleRepeat(); }

// The progress bar is a slider when the app can seek: dragging it shows the time it would
// go to, and letting go goes there. (A tap on the bar jumps there.)
void onSeek(lv_event_t *e) {
  const model::Media &m = model::media;
  if (m.len <= 0) return;
  const float to = lv_slider_get_value(w.progress) / 1000.0f * m.len;
  if (lv_event_get_code(e) == LV_EVENT_RELEASED) {
    pc::seek(to);
    return;
  }
  char buf[16];
  formatTime(buf, sizeof(buf), to);
  lv_label_set_text(w.elapsed, buf);
}

// The keys on the left, what's playing in the middle with its progress on one line (times
// either side of the bar), and the album on the right, as tall as the band.
void buildPlayer(lv_obj_t *c) {
  w.card = c;
  lv_obj_set_style_pad_ver(c, 10, 0);
  lv_obj_set_style_pad_left(c, 16, 0);
  lv_obj_set_style_pad_right(c, 10, 0);
  flexRow(c, 20);

  lv_obj_t *controls = box(c);
  lv_obj_set_size(controls, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  flexCol(controls, 8);
  lv_obj_set_flex_align(controls, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_t *transport = box(controls);
  lv_obj_set_size(transport, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  flexRow(transport, 12);
  iconButton(transport, 46, 46, kRaised, LV_SYMBOL_PREV, kText, onPrev);
  lv_obj_t *play = button(transport, 56, 56, kText, onToggle);
  lv_obj_set_style_bg_color(play, hex(0xC9CED4), LV_STATE_PRESSED);
  w.play_icon = label(play, LV_SYMBOL_PLAY, &lv_font_montserrat_28, kBg);
  lv_obj_center(w.play_icon);
  iconButton(transport, 46, 46, kRaised, LV_SYMBOL_NEXT, kText, onNext);
  lv_obj_t *toggles = box(controls);
  lv_obj_set_size(toggles, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  flexRow(toggles, 8);
  w.shuffle_icon = iconButton(toggles, 56, 28, kRaised, LV_SYMBOL_SHUFFLE, kMuted, onShuffle);
  w.shuffle = lv_obj_get_parent(w.shuffle_icon);
  w.repeat_icon = iconButton(toggles, 56, 28, kRaised, LV_SYMBOL_LOOP, kMuted, onRepeat);
  w.repeat = lv_obj_get_parent(w.repeat_icon);
  for (lv_obj_t *b : {w.shuffle, w.repeat}) lv_obj_set_style_radius(b, 10, 0);

  lv_obj_t *info = box(c);
  lv_obj_set_size(info, 1, LV_SIZE_CONTENT);
  lv_obj_set_flex_grow(info, 1);
  flexCol(info, 2);
  lv_obj_t *where = box(info);
  lv_obj_set_size(where, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(where, 8);
  w.app = label(where, "", &small_font, kAccent);
  w.device = label(where, "", &small_font, kMuted);
  fillRow(w.device);
  w.title = label(info, "Nothing playing", &title_font, kText);
  w.artist = label(info, "", &body_font, kMuted);
  ellipsize(w.title);
  ellipsize(w.artist);
  lv_obj_t *times = box(info);
  lv_obj_set_size(times, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_margin_top(times, 8, 0);
  flexRow(times, 10);
  w.elapsed = label(times, "", &lv_font_montserrat_14, kMuted);
  w.progress = lv_slider_create(times);
  lv_obj_set_size(w.progress, 1, 6);
  lv_obj_set_flex_grow(w.progress, 1);
  lv_slider_set_range(w.progress, 0, 1000);
  lv_obj_set_style_bg_color(w.progress, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(w.progress, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(w.progress, hex(kText), LV_PART_KNOB);
  lv_obj_set_style_pad_all(w.progress, 4, LV_PART_KNOB);  // a 14 px dot
  lv_obj_set_style_pad_all(w.progress, 7, LV_PART_KNOB | LV_STATE_PRESSED);  // bigger under the finger
  lv_obj_set_ext_click_area(w.progress, 16);     // fingers are bigger than the 6 px track
  lv_obj_set_gesture_bubble(w.progress, false);  // dragging it isn't a page swipe
  lv_obj_add_event_cb(w.progress, onSeek, LV_EVENT_VALUE_CHANGED, nullptr);
  lv_obj_add_event_cb(w.progress, onSeek, LV_EVENT_RELEASED, nullptr);
  lv_obj_remove_flag(w.progress, LV_OBJ_FLAG_CLICKABLE);  // until an app that can seek plays
  lv_obj_set_style_bg_opa(w.progress, LV_OPA_TRANSP, LV_PART_KNOB);
  w.total = label(times, "", &lv_font_montserrat_14, kMuted);

  lv_obj_t *holder = w.holder = card(c, kArtBg, kArtBg);
  lv_obj_set_style_pad_all(holder, 0, 0);
  lv_obj_set_style_clip_corner(holder, true, 0);
  w.art_icon = label(holder, LV_SYMBOL_AUDIO, &lv_font_montserrat_48, 0x8CCBFF);
  lv_obj_center(w.art_icon);
  w.art = lv_image_create(holder);
  lv_image_set_inner_align(w.art, LV_IMAGE_ALIGN_STRETCH);
  lv_obj_set_hidden(w.art, true);
}

// The art as tall as the band's inside: the context bar coming or going changes it.
void sizeArt() {
  const int32_t h = lv_obj_get_content_height(w.card);
  if (h == art_size || h <= 0) return;
  art_size = h;
  lv_obj_set_size(w.holder, h, h);
  lv_obj_set_size(w.art, h, h);
}

// A plain bar (no dot, no touch: a swipe there turns the page) unless the app can seek.
void setSeekable(bool on) {
  if (on == lv_obj_has_flag(w.progress, LV_OBJ_FLAG_CLICKABLE)) return;
  if (on) {
    lv_obj_add_flag(w.progress, LV_OBJ_FLAG_CLICKABLE);
  } else {
    lv_obj_remove_flag(w.progress, LV_OBJ_FLAG_CLICKABLE);
  }
  lv_obj_set_style_bg_opa(w.progress, on ? LV_OPA_COVER : LV_OPA_TRANSP, LV_PART_KNOB);
}

void setToggle(lv_obj_t *icon, lv_obj_t *button, bool supported, bool on) {
  lv_obj_set_style_text_color(icon, hex(on ? accent : kMuted), 0);
  lv_obj_set_style_opa(button, supported ? LV_OPA_COVER : LV_OPA_30, 0);
  lv_obj_set_clickable(button, supported);
}

void updateProgress() {
  const model::Media &m = model::media;
  progress_ms = millis();
  if (lv_obj_has_state(w.progress, LV_STATE_PRESSED)) return;  // the finger has it
  if (!pc::connected() || !m.present || m.len <= 0) {
    lv_slider_set_value(w.progress, 0, LV_ANIM_OFF);
    lv_label_set_text(w.elapsed, "");
    lv_label_set_text(w.total, "");
    return;
  }
  const float pos = m.positionNow();
  lv_slider_set_value(w.progress, int(pos / m.len * 1000), LV_ANIM_OFF);
  char buf[16];
  formatTime(buf, sizeof(buf), pos);
  lv_label_set_text(w.elapsed, buf);
  formatTime(buf, sizeof(buf), m.len);
  lv_label_set_text(w.total, buf);
}

void showPlayerArt() {
  const lv_image_dsc_t *art = pc::connected() && model::media.present ? artFor(model::media.art) : nullptr;
  if (art) lv_image_set_src(w.art, art);
  lv_obj_set_hidden(w.art, art == nullptr);
  lv_obj_set_hidden(w.art_icon, art != nullptr);
}

void updateMedia() {
  const int up = pc::connected() ? 1 : 0;
  if (up != connected_seen) {
    connected_seen = up;
    media_seen = UINT32_MAX;
  }
  const model::Media &m = model::media;
  if (m.version != media_seen) {
    media_seen = m.version;
    setSeekable(up && m.present && m.seekable && m.len > 0);
    accent = mediaAccent(up);
    tintMediaCard(w.card, accent);
    setTextColor(w.app, accent);
    if (!lv_color_eq(lv_obj_get_style_bg_color(w.progress, LV_PART_INDICATOR), hex(accent))) {
      lv_obj_set_style_bg_color(w.progress, hex(accent), LV_PART_INDICATOR);
    }
    if (!up) {
      lv_label_set_text(w.title, "PC offline");
      lv_label_set_text(w.artist, "Buttons send media keys");
      lv_label_set_text(w.app, "");
    } else if (!m.present) {
      lv_label_set_text(w.title, "Nothing playing");
      lv_label_set_text(w.artist, "");
      lv_label_set_text(w.app, "");
    } else {
      lv_label_set_text(w.title, m.title.length() ? m.title.c_str() : "Unknown track");
      lv_label_set_text(w.artist, m.artist.c_str());
      String app = m.app;
      app.toUpperCase();
      lv_label_set_text(w.app, app.c_str());
    }
    lv_label_set_text(w.play_icon, up && m.playing ? LV_SYMBOL_PAUSE : LV_SYMBOL_PLAY);
    setToggle(w.shuffle_icon, w.shuffle, up && m.shuffle >= 0, m.shuffle == 1);
    const bool repeat_on = m.repeat == "all" || m.repeat == "one";
    lv_label_set_text(w.repeat_icon, m.repeat == "one" ? LV_SYMBOL_LOOP "1" : LV_SYMBOL_LOOP);
    setToggle(w.repeat_icon, w.repeat, up && m.repeat.length() > 0, repeat_on);
    showPlayerArt();
    updateProgress();
  } else if (millis() - progress_ms > kProgressEveryMs) {
    updateProgress();
  }
}

// Beside the app, the output it plays on.
void updateDevice() {
  if (model::devices.version == devices_seen) return;
  devices_seen = model::devices.version;
  const model::Device *out = model::devices.selectedOutput();
  setText(w.device, out ? out->name.c_str() : "");
}

class Player : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildPlayer(c); }
  void update() override {
    sizeArt();
    updateMedia();
    updateDevice();
  }
  void showArt() override { showPlayerArt(); }
};

}  // namespace

extern const Kind kPlayer = {"player", {14, 4}, {}, []() -> Tile * { return new Player(); }};

}  // namespace ui::tiles
