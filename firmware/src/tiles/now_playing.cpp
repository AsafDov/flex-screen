// The Now Playing tile: the track, its art and source app, play/pause and skip, and the
// PC's master volume. In the cover's color when the PC found one. Without the companion
// the buttons still work, as plain media keys.
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

// After the user lets go of the slider, ignore the PC's volume reports for this long so
// a report sent before the last drag step doesn't snap the knob back.
constexpr uint32_t kHoldAfterTouchMs = 800;
constexpr int kArtSize = 96;  // at 48 the card, taller beside the rail, was mostly empty

class NowPlaying : public Tile {
 public:
  void build(lv_obj_t *card, Span) override {
    card_ = card;
    flexCol(card, 8);
    lv_obj_set_flex_align(card, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);

    lv_obj_t *info = box(card);
    lv_obj_set_size(info, LV_PCT(100), LV_SIZE_CONTENT);
    flexRow(info, 10);
    lv_obj_t *holder = ui::card(info, kArtBg, kArtBg);
    lv_obj_set_size(holder, kArtSize, kArtSize);
    lv_obj_set_style_pad_all(holder, 0, 0);
    lv_obj_set_style_radius(holder, 8, 0);
    lv_obj_set_style_clip_corner(holder, true, 0);
    art_icon_ = label(holder, LV_SYMBOL_AUDIO, &lv_font_montserrat_28, 0x8CCBFF);
    lv_obj_center(art_icon_);
    art_ = lv_image_create(holder);
    lv_obj_set_size(art_, kArtSize, kArtSize);
    lv_image_set_inner_align(art_, LV_IMAGE_ALIGN_STRETCH);
    lv_obj_set_hidden(art_, true);

    lv_obj_t *text = box(info);
    lv_obj_set_flex_grow(text, 1);
    lv_obj_set_height(text, LV_SIZE_CONTENT);
    flexCol(text, 1);
    title_ = label(text, "Nothing playing", &title_font, kText);
    artist_ = label(text, "", &body_font, kMuted);
    source_ = label(text, "", &body_font, kMuted);
    for (lv_obj_t *l : {title_, artist_, source_}) ellipsize(l);

    lv_obj_t *transport = box(card);
    lv_obj_set_size(transport, LV_PCT(100), LV_SIZE_CONTENT);
    flexRow(transport, 16);
    lv_obj_set_flex_align(transport, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    iconButton(transport, 48, 48, kSurface, LV_SYMBOL_PREV, kText, [](lv_event_t *) { pc::media(pc::MediaCmd::Prev); });
    lv_obj_t *play = button(transport, 56, 56, kText, [](lv_event_t *) { pc::media(pc::MediaCmd::Toggle); });
    lv_obj_set_style_bg_color(play, hex(0xC9CED4), LV_STATE_PRESSED);
    play_icon_ = label(play, LV_SYMBOL_PLAY, &lv_font_montserrat_20, kBg);
    lv_obj_center(play_icon_);
    iconButton(transport, 48, 48, kSurface, LV_SYMBOL_NEXT, kText, [](lv_event_t *) { pc::media(pc::MediaCmd::Next); });

    lv_obj_t *vol = box(card);
    lv_obj_set_size(vol, LV_PCT(100), 36);
    flexRow(vol, 10);
    label(vol, LV_SYMBOL_VOLUME_MAX, &lv_font_montserrat_16, kMuted);
    slider_ = lv_slider_create(vol);
    lv_obj_set_flex_grow(slider_, 1);
    lv_obj_set_height(slider_, 8);
    lv_slider_set_range(slider_, 0, 100);
    lv_obj_set_style_bg_color(slider_, hex(kTrack), LV_PART_MAIN);
    lv_obj_set_style_bg_color(slider_, hex(kAccent), LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(slider_, hex(kText), LV_PART_KNOB);
    lv_obj_set_style_pad_all(slider_, 6, LV_PART_KNOB);
    lv_obj_set_ext_click_area(slider_, 14);    // fingers are bigger than the 8 px track
    lv_obj_set_gesture_bubble(slider_, false);  // dragging it isn't a page swipe
    lv_obj_add_event_cb(slider_, onVolume, LV_EVENT_VALUE_CHANGED, this);
    lv_obj_add_event_cb(slider_, onVolume, LV_EVENT_RELEASED, this);
    vol_value_ = label(vol, "--", &lv_font_montserrat_16, kText);
    lv_obj_set_width(vol_value_, 32);
    lv_obj_set_style_text_align(vol_value_, LV_TEXT_ALIGN_RIGHT, 0);
  }

  void update() override {
    updateMedia();
    updateVolume();
  }

  void showArt() override {
    const lv_image_dsc_t *art = pc::connected() && model::media.present ? artFor(model::media.art) : nullptr;
    if (art) lv_image_set_src(art_, art);
    lv_obj_set_hidden(art_, art == nullptr);
    lv_obj_set_hidden(art_icon_, art != nullptr);
  }

 private:
  static void onVolume(lv_event_t *e) {
    NowPlaying *self = static_cast<NowPlaying *>(lv_event_get_user_data(e));
    const int v = lv_slider_get_value(self->slider_);
    lv_label_set_text_fmt(self->vol_value_, "%d", v);
    if (pc::connected()) previewVolume(v);
    self->vol_touched_ms_ = millis();
    self->volume_seen_ = UINT32_MAX;  // after the hold, the PC's level again, even if it didn't change
    // Rate-limit while dragging; always send where the finger lifted.
    const bool released = lv_event_get_code(e) == LV_EVENT_RELEASED;
    if (released || millis() - self->vol_sent_ms_ > 60) {
      pc::setVolume(v);
      self->vol_sent_ms_ = millis();
    }
  }

  void updateMedia() {
    const int up = pc::connected() ? 1 : 0;
    if (up != connected_seen_) {
      connected_seen_ = up;
      media_seen_ = UINT32_MAX;  // redraw from the model, or show the offline note
    }
    const model::Media &m = model::media;
    if (m.version == media_seen_) return;
    media_seen_ = m.version;
    const uint32_t accent = mediaAccent(up);
    tintMediaCard(card_, accent);
    if (!lv_color_eq(lv_obj_get_style_bg_color(slider_, LV_PART_INDICATOR), hex(accent))) {
      lv_obj_set_style_bg_color(slider_, hex(accent), LV_PART_INDICATOR);
    }
    if (!up) {
      lv_label_set_text(title_, "PC offline");
      lv_label_set_text(artist_, "Buttons send media keys");
      lv_label_set_text(source_, "");
      lv_label_set_text(play_icon_, LV_SYMBOL_PLAY);
      showArt();  // the placeholder, as on Audio: the last track's art stayed beside "PC offline"
      return;
    }
    if (!m.present) {
      lv_label_set_text(title_, "Nothing playing");
      lv_label_set_text(artist_, "");
      lv_label_set_text(source_, "");
    } else {
      lv_label_set_text(title_, m.title.length() ? m.title.c_str() : "Unknown track");
      lv_label_set_text(artist_, m.artist.c_str());
      lv_label_set_text(source_, m.app.c_str());
    }
    lv_label_set_text(play_icon_, m.playing ? LV_SYMBOL_PAUSE : LV_SYMBOL_PLAY);
    showArt();
  }

  void updateVolume() {
    const model::Volume &v = model::volume;
    if (v.version == volume_seen_) return;
    if (lv_obj_has_state(slider_, LV_STATE_PRESSED) || millis() - vol_touched_ms_ < kHoldAfterTouchMs) {
      return;  // try again next loop
    }
    volume_seen_ = v.version;
    if (v.level < 0) {  // no playback device: not the last level it had
      lv_slider_set_value(slider_, 0, LV_ANIM_OFF);
      lv_label_set_text(vol_value_, "--");
      return;
    }
    lv_slider_set_value(slider_, v.level, LV_ANIM_OFF);
    lv_label_set_text_fmt(vol_value_, "%d", v.level);
  }

  lv_obj_t *card_ = nullptr, *art_icon_ = nullptr, *art_ = nullptr;
  lv_obj_t *title_ = nullptr, *artist_ = nullptr, *source_ = nullptr, *play_icon_ = nullptr;
  lv_obj_t *slider_ = nullptr, *vol_value_ = nullptr;
  uint32_t media_seen_ = UINT32_MAX, volume_seen_ = UINT32_MAX;
  int connected_seen_ = -1;
  uint32_t vol_sent_ms_ = 0, vol_touched_ms_ = 0;
};

}  // namespace

// extern: tiles.cpp's list of kinds refers to it.
extern const Kind kNowPlaying = {"now-playing", {3, 2}, {}, []() -> Tile * { return new NowPlaying(); }};

}  // namespace ui::tiles
