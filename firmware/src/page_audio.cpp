#include "page_audio.h"

#include "model.h"
#include "pc.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::audio {
namespace {

constexpr int kArtSize = 180;  // matches the companion's ArtSize, so no scaling
constexpr int kChannels = 1 + model::Mixer::kMax;  // Master + apps
constexpr uint32_t kHoldAfterTouchMs = 1000;       // the mixer is polled every 750 ms
constexpr uint32_t kProgressEveryMs = 250;
constexpr uint32_t kSwitchTimeoutMs = 4000;

struct Channel {
  String id;  // "" for Master
  lv_obj_t *value = nullptr;
  lv_obj_t *slider = nullptr;
  lv_obj_t *mute_icon = nullptr;
  uint32_t sent_ms = 0;
  uint32_t touched_ms = 0;
};

struct {
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
  lv_obj_t *outputs;
  lv_obj_t *inputs;
  lv_obj_t *channels;
} w;

Channel channels[kChannels];
int channel_count = 0;
String channel_ids;  // the app ids the faders were built for

uint32_t media_seen = UINT32_MAX;
uint32_t devices_seen = UINT32_MAX;
uint32_t mixer_seen = UINT32_MAX;
uint32_t volume_seen = UINT32_MAX;
int connected_seen = -1;
uint32_t progress_ms = 0;
int shown_output = -1;  // which device button is lit, which may be ahead of the PC
int shown_input = -1;
uint32_t switch_pending_ms = 0;

void onPrev(lv_event_t *) { pc::media(pc::MediaCmd::Prev); }
void onToggle(lv_event_t *) { pc::media(pc::MediaCmd::Toggle); }
void onNext(lv_event_t *) { pc::media(pc::MediaCmd::Next); }
void onShuffle(lv_event_t *) { pc::toggleShuffle(); }
void onRepeat(lv_event_t *) { pc::cycleRepeat(); }

void styleDevice(lv_obj_t *button, bool selected) {
  lv_obj_set_style_bg_color(button, hex(selected ? kSelected : kRaised), 0);
  lv_obj_set_style_border_color(button, hex(kAccent), 0);
  lv_obj_set_style_border_width(button, selected ? 2 : 0, 0);
  lv_obj_set_style_text_color(lv_obj_get_child(button, 0), hex(selected ? kText : kSoftText), 0);
}

// Windows takes up to a couple of seconds to switch devices, so the tapped button lights
// up at once; the PC's answer then redraws the row (or the old choice comes back if no
// answer arrives in time).
void pickDevice(lv_event_t *e, lv_obj_t *row, const model::Device *list, int count, int &shown,
                void (*select)(const char *)) {
  const int i = int(intptr_t(lv_event_get_user_data(e)));
  if (i >= count || i == shown) return;
  for (uint32_t c = 0; c < lv_obj_get_child_count(row); c++) styleDevice(lv_obj_get_child(row, c), int(c) == i);
  shown = i;
  switch_pending_ms = millis();
  select(list[i].id.c_str());
}

void onOutput(lv_event_t *e) {
  pickDevice(e, w.outputs, model::devices.out, model::devices.out_count, shown_output, pc::selectOutput);
}

void onInput(lv_event_t *e) {
  pickDevice(e, w.inputs, model::devices.in, model::devices.in_count, shown_input, pc::selectInput);
}

void onFader(lv_event_t *e) {
  Channel &ch = channels[intptr_t(lv_event_get_user_data(e))];
  const int v = lv_slider_get_value(ch.slider);
  lv_label_set_text_fmt(ch.value, "%d", v);
  ch.touched_ms = millis();
  if (ch.id.length() == 0 && pc::connected()) previewVolume(v);
  // Rate-limit while dragging; always send where the finger lifted.
  if (lv_event_get_code(e) != LV_EVENT_RELEASED && millis() - ch.sent_ms < 60) return;
  ch.sent_ms = millis();
  if (ch.id.length() == 0) {
    pc::setVolume(v);
  } else {
    pc::setAppVolume(ch.id.c_str(), v);
  }
}

void onChannelMute(lv_event_t *e) {
  const Channel &ch = channels[intptr_t(lv_event_get_user_data(e))];
  if (ch.id.length() == 0) {
    pc::toggleMute();
  } else {
    pc::toggleAppMute(ch.id.c_str());
  }
}

void buildNowPlaying(lv_obj_t *parent) {
  lv_obj_t *c = card(parent);
  lv_obj_set_size(c, 296, LV_PCT(100));
  lv_obj_set_style_pad_all(c, 14, 0);
  flexCol(c, 4);

  // Art as large as the card's height allows, with where it's playing and the
  // shuffle/repeat toggles in the column beside it.
  lv_obj_t *top = box(c);
  lv_obj_set_size(top, LV_PCT(100), kArtSize);
  flexRow(top, 10);
  lv_obj_set_flex_align(top, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  lv_obj_set_style_margin_bottom(top, 4, 0);

  lv_obj_t *holder = card(top, kArtBg, kArtBg);
  lv_obj_set_size(holder, kArtSize, kArtSize);
  lv_obj_set_style_pad_all(holder, 0, 0);
  lv_obj_set_style_clip_corner(holder, true, 0);
  w.art_icon = label(holder, LV_SYMBOL_AUDIO, &lv_font_montserrat_48, 0x8CCBFF);
  lv_obj_center(w.art_icon);
  w.art = lv_image_create(holder);
  lv_obj_set_size(w.art, kArtSize, kArtSize);
  lv_obj_set_hidden(w.art, true);

  lv_obj_t *side = box(top);
  lv_obj_set_flex_grow(side, 1);
  lv_obj_set_height(side, LV_PCT(100));
  flexCol(side, 6);
  lv_obj_set_flex_align(side, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_START);
  w.app = label(side, "", &small_font, kAccent);
  w.device = label(side, "", &small_font, kMuted);
  ellipsize(w.app);
  ellipsize(w.device);
  lv_obj_t *gap = box(side);
  lv_obj_set_flex_grow(gap, 1);
  w.shuffle_icon = iconButton(side, 40, 40, kRaised, LV_SYMBOL_SHUFFLE, kMuted, onShuffle);
  w.shuffle = lv_obj_get_parent(w.shuffle_icon);
  w.repeat_icon = iconButton(side, 40, 40, kRaised, LV_SYMBOL_LOOP, kMuted, onRepeat);
  w.repeat = lv_obj_get_parent(w.repeat_icon);
  for (lv_obj_t *b : {w.shuffle, w.repeat}) lv_obj_set_style_radius(b, 10, 0);

  w.title = label(c, "Nothing playing", &title_font, kText);
  w.artist = label(c, "", &body_font, kMuted);
  ellipsize(w.title);
  ellipsize(w.artist);

  w.progress = lv_bar_create(c);
  lv_obj_set_size(w.progress, LV_PCT(100), 6);
  lv_bar_set_range(w.progress, 0, 1000);
  lv_obj_set_style_bg_color(w.progress, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(w.progress, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_margin_top(w.progress, 4, 0);
  lv_obj_t *times = box(c);
  lv_obj_set_size(times, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(times, 0);
  lv_obj_set_flex_align(times, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  w.elapsed = label(times, "", &lv_font_montserrat_14, kMuted);
  w.total = label(times, "", &lv_font_montserrat_14, kMuted);

  lv_obj_t *spacer = box(c);
  lv_obj_set_width(spacer, LV_PCT(100));
  lv_obj_set_flex_grow(spacer, 1);

  lv_obj_t *transport = box(c);
  lv_obj_set_size(transport, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(transport, 16);
  lv_obj_set_flex_align(transport, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  iconButton(transport, 48, 48, kRaised, LV_SYMBOL_PREV, kText, onPrev);
  lv_obj_t *play = button(transport, 60, 60, kText, onToggle);
  lv_obj_set_style_bg_color(play, hex(0xC9CED4), LV_STATE_PRESSED);
  w.play_icon = label(play, LV_SYMBOL_PLAY, &lv_font_montserrat_28, kBg);
  lv_obj_center(w.play_icon);
  iconButton(transport, 48, 48, kRaised, LV_SYMBOL_NEXT, kText, onNext);
}

lv_obj_t *buildDeviceRow(lv_obj_t *parent, const char *kind) {
  lv_obj_t *row = box(parent);
  lv_obj_set_size(row, LV_PCT(100), 40);
  flexRow(row, 8);
  lv_obj_t *k = label(row, kind, &lv_font_montserrat_14, kMuted);
  lv_obj_set_width(k, 64);  // "OUTPUT" is ~58 px; never wrap
  lv_label_set_long_mode(k, LV_LABEL_LONG_MODE_CLIP);
  lv_obj_t *buttons = box(row);
  lv_obj_set_flex_grow(buttons, 1);
  lv_obj_set_height(buttons, 40);
  flexRow(buttons, 8);
  // More than three devices scroll sideways instead of shrinking.
  lv_obj_set_scrollable(buttons, true);
  lv_obj_set_scroll_dir(buttons, LV_DIR_HOR);
  lv_obj_set_scrollbar_mode(buttons, LV_SCROLLBAR_MODE_OFF);
  return buttons;
}

void buildRight(lv_obj_t *parent) {
  lv_obj_t *right = box(parent);
  lv_obj_set_flex_grow(right, 1);
  lv_obj_set_height(right, LV_PCT(100));
  flexCol(right, kGap);

  lv_obj_t *devices = card(right);
  lv_obj_set_size(devices, LV_PCT(100), LV_SIZE_CONTENT);
  lv_obj_set_style_pad_all(devices, 10, 0);
  flexCol(devices, 8);
  w.outputs = buildDeviceRow(devices, "OUTPUT");
  w.inputs = buildDeviceRow(devices, "INPUT");

  lv_obj_t *mixer = card(right);
  lv_obj_set_width(mixer, LV_PCT(100));
  lv_obj_set_flex_grow(mixer, 1);
  lv_obj_set_style_pad_all(mixer, 10, 0);
  flexCol(mixer, 6);
  lv_obj_t *head = box(mixer);
  lv_obj_set_size(head, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(head, 0);
  lv_obj_set_flex_align(head, LV_FLEX_ALIGN_SPACE_BETWEEN, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  label(head, "MIXER", &lv_font_montserrat_14, kMuted);
  label(head, "Drag a fader, tap the icon to mute", &lv_font_montserrat_14, kMuted);
  w.channels = box(mixer);
  lv_obj_set_width(w.channels, LV_PCT(100));
  lv_obj_set_flex_grow(w.channels, 1);
  flexRow(w.channels, 6);
}

void fillDevices(lv_obj_t *row, const model::Device *list, int count, lv_event_cb_t cb, int &shown) {
  lv_obj_clean(row);
  shown = -1;
  if (count == 0) {
    label(row, "None", &lv_font_montserrat_16, kMuted);
    return;
  }
  for (int i = 0; i < count; i++) {
    const model::Device &d = list[i];
    lv_obj_t *b = button(row, 124, 40, kRaised, cb, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(b, 10, 0);
    lv_obj_set_style_pad_hor(b, 10, 0);
    lv_obj_t *l = label(b, d.name.c_str(), &body_font, kSoftText);
    ellipsize(l);
    lv_obj_set_style_text_align(l, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_center(l);
    styleDevice(b, d.selected);
    if (d.selected) shown = i;
  }
}

void buildChannel(int index, const char *name, bool master) {
  Channel &ch = channels[index];
  lv_obj_t *col = box(w.channels);
  lv_obj_set_flex_grow(col, 1);
  lv_obj_set_height(col, LV_PCT(100));
  flexCol(col, 4);
  lv_obj_set_flex_align(col, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_set_style_pad_ver(col, 6, 0);
  if (master) {
    lv_obj_set_style_bg_color(col, hex(0x1A1E24), 0);
    lv_obj_set_style_bg_opa(col, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(col, 10, 0);
  }
  void *user = reinterpret_cast<void *>(intptr_t(index));

  ch.value = label(col, "--", &lv_font_montserrat_16, kText);
  // The fader gets the column's spare height through a holder padded for the knob's
  // overhang: a margin on the fader itself isn't counted when flex shares out the height,
  // which pushed the mute buttons out of the mixer.
  lv_obj_t *holder = box(col);
  lv_obj_set_width(holder, 40);
  lv_obj_set_flex_grow(holder, 1);
  lv_obj_set_style_pad_ver(holder, 8, 0);
  ch.slider = lv_slider_create(holder);
  lv_obj_set_size(ch.slider, 8, LV_PCT(100));  // taller than wide: LVGL makes it vertical
  lv_obj_align(ch.slider, LV_ALIGN_CENTER, 0, 0);
  lv_slider_set_range(ch.slider, 0, 100);
  lv_obj_set_style_bg_color(ch.slider, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(ch.slider, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(ch.slider, hex(kText), LV_PART_KNOB);
  lv_obj_set_style_radius(ch.slider, 7, LV_PART_KNOB);
  lv_obj_set_style_pad_hor(ch.slider, 14, LV_PART_KNOB);  // a 36x14 pill like the canvas
  lv_obj_set_style_pad_ver(ch.slider, 3, LV_PART_KNOB);
  lv_obj_set_ext_click_area(ch.slider, 18);
  lv_obj_set_gesture_bubble(ch.slider, false);  // dragging it isn't a page swipe
  lv_obj_add_event_cb(ch.slider, onFader, LV_EVENT_VALUE_CHANGED, user);
  lv_obj_add_event_cb(ch.slider, onFader, LV_EVENT_RELEASED, user);

  lv_obj_t *n = label(col, name, &body_font, master ? kText : kMuted);
  ellipsize(n);
  lv_obj_set_style_text_align(n, LV_TEXT_ALIGN_CENTER, 0);
  ch.mute_icon = iconButton(col, 36, 36, kRaised, LV_SYMBOL_VOLUME_MAX, kText, onChannelMute, user);
  lv_obj_set_style_radius(lv_obj_get_parent(ch.mute_icon), 10, 0);
}

void setChannel(Channel &ch, int level, bool muted) {
  const bool touched = lv_obj_has_state(ch.slider, LV_STATE_PRESSED) || millis() - ch.touched_ms < kHoldAfterTouchMs;
  if (!touched && level >= 0) {
    lv_slider_set_value(ch.slider, level, LV_ANIM_OFF);
    lv_label_set_text_fmt(ch.value, "%d", level);
  }
  lv_label_set_text(ch.mute_icon, muted ? LV_SYMBOL_MUTE : LV_SYMBOL_VOLUME_MAX);
  lv_obj_set_style_text_color(ch.mute_icon, hex(muted ? kAmber : kText), 0);
  lv_obj_set_style_bg_color(lv_obj_get_parent(ch.mute_icon), hex(muted ? kAmberBg : kRaised), 0);
}

void updateMixer() {
  const model::Mixer &mx = model::mixer;
  const model::Volume &vol = model::volume;
  if (mx.version == mixer_seen && vol.version == volume_seen) return;
  mixer_seen = mx.version;
  volume_seen = vol.version;

  String ids;
  for (int i = 0; i < mx.count; i++) ids += mx.apps[i].id + "\n";
  if (ids != channel_ids || channel_count == 0) {
    // A different set of apps: rebuild the faders.
    channel_ids = ids;
    lv_obj_clean(w.channels);
    channels[0] = Channel();
    buildChannel(0, "Master", true);
    for (int i = 0; i < mx.count; i++) {
      channels[i + 1] = Channel();
      channels[i + 1].id = mx.apps[i].id;
      buildChannel(i + 1, mx.apps[i].name.c_str(), false);
    }
    channel_count = 1 + mx.count;
  }
  setChannel(channels[0], vol.level, vol.muted);
  for (int i = 0; i < mx.count; i++) setChannel(channels[i + 1], mx.apps[i].level, mx.apps[i].muted);
}

void updateDevices() {
  const model::Devices &d = model::devices;
  if (switch_pending_ms && millis() - switch_pending_ms > kSwitchTimeoutMs) {
    devices_seen = UINT32_MAX;  // no answer from the PC: show what it last reported
  }
  if (d.version == devices_seen) return;
  devices_seen = d.version;
  switch_pending_ms = 0;
  fillDevices(w.outputs, d.out, d.out_count, onOutput, shown_output);
  fillDevices(w.inputs, d.in, d.in_count, onInput, shown_input);
  const model::Device *out = d.selectedOutput();
  lv_label_set_text(w.device, out ? out->name.c_str() : "");
}

void setToggle(lv_obj_t *icon, lv_obj_t *button, bool supported, bool on) {
  lv_obj_set_style_text_color(icon, hex(on ? kAccent : kMuted), 0);
  lv_obj_set_style_opa(button, supported ? LV_OPA_COVER : LV_OPA_30, 0);
  lv_obj_set_clickable(button, supported);
}

void updateProgress() {
  const model::Media &m = model::media;
  progress_ms = millis();
  if (!pc::connected() || !m.present || m.len <= 0) {
    lv_bar_set_value(w.progress, 0, LV_ANIM_OFF);
    lv_label_set_text(w.elapsed, "");
    lv_label_set_text(w.total, "");
    return;
  }
  const float pos = m.positionNow();
  lv_bar_set_value(w.progress, int(pos / m.len * 1000), LV_ANIM_OFF);
  char buf[16];
  formatTime(buf, sizeof(buf), pos);
  lv_label_set_text(w.elapsed, buf);
  formatTime(buf, sizeof(buf), m.len);
  lv_label_set_text(w.total, buf);
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
    showArt();
    updateProgress();
  } else if (millis() - progress_ms > kProgressEveryMs) {
    updateProgress();
  }
}

}  // namespace

void build(lv_obj_t *page) {
  lv_obj_set_style_pad_all(page, kGap, 0);
  lv_obj_set_flex_flow(page, LV_FLEX_FLOW_ROW);
  lv_obj_set_style_pad_column(page, kGap, 0);
  lv_obj_set_scrollable(page, false);
  buildNowPlaying(page);
  buildRight(page);
}

void update() {
  updateMedia();
  updateDevices();
  updateMixer();
}

void showArt() {
  const lv_image_dsc_t *art = pc::connected() && model::media.present ? artFor(model::media.art) : nullptr;
  if (art) lv_image_set_src(w.art, art);
  lv_obj_set_hidden(w.art, art == nullptr);
  lv_obj_set_hidden(w.art_icon, art != nullptr);
}

}  // namespace ui::audio
