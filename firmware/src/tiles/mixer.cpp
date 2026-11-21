// The Mixer tile (Audio): a fader for the master volume and one for each app playing
// sound; the name under a fader mutes it (amber while muted). All the tile's height goes to
// the faders: no heading row. One of a kind (the manager places each kind once), so its
// state lives here.
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr int kChannels = 1 + model::Mixer::kMax;  // Master + apps
constexpr uint32_t kHoldAfterTouchMs = 1000;       // the mixer is polled every 750 ms

struct Channel {
  String id;  // "" for Master
  lv_obj_t *value = nullptr;
  lv_obj_t *slider = nullptr;
  lv_obj_t *pill = nullptr;  // the name, which is the mute button
  lv_obj_t *name = nullptr;
  uint32_t sent_ms = 0;
  uint32_t touched_ms = 0;
};

lv_obj_t *faders;  // the channels' row
Channel channels[kChannels];
int channel_count = 0;
String channel_ids;  // the app ids the faders were built for

uint32_t mixer_seen = UINT32_MAX;
uint32_t volume_seen = UINT32_MAX;

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

void buildMixer(lv_obj_t *c) {
  lv_obj_set_style_pad_all(c, 8, 0);
  faders = box(c);
  lv_obj_set_size(faders, LV_PCT(100), LV_PCT(100));
  flexRow(faders, 6);
}

void buildChannel(int index, const char *name, bool master) {
  Channel &ch = channels[index];
  lv_obj_t *col = box(faders);
  lv_obj_set_flex_grow(col, 1);
  lv_obj_set_height(col, LV_PCT(100));
  flexCol(col, 4);
  lv_obj_set_flex_align(col, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_set_style_pad_ver(col, 4, 0);
  if (master) {
    lv_obj_set_style_bg_color(col, hex(0x1A1E24), 0);
    lv_obj_set_style_bg_opa(col, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(col, 10, 0);
    lv_obj_set_style_pad_hor(col, 4, 0);
  }
  void *user = reinterpret_cast<void *>(intptr_t(index));

  ch.value = label(col, "--", &lv_font_montserrat_16, kText);
  // The fader gets the column's spare height through a holder padded for the knob's
  // overhang: a margin on the fader itself isn't counted when flex shares out the height,
  // which pushed the mute buttons out of the mixer.
  lv_obj_t *holder = box(col);
  lv_obj_set_width(holder, 44);
  lv_obj_set_flex_grow(holder, 1);
  lv_obj_set_style_pad_ver(holder, 10, 0);
  ch.slider = lv_slider_create(holder);
  lv_obj_set_size(ch.slider, 10, LV_PCT(100));  // taller than wide: LVGL makes it vertical
  lv_obj_align(ch.slider, LV_ALIGN_CENTER, 0, 0);
  lv_slider_set_range(ch.slider, 0, 100);
  lv_obj_set_style_bg_color(ch.slider, hex(kTrack), LV_PART_MAIN);
  lv_obj_set_style_bg_color(ch.slider, hex(kAccent), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(ch.slider, hex(kText), LV_PART_KNOB);
  lv_obj_set_style_radius(ch.slider, 9, LV_PART_KNOB);
  lv_obj_set_style_pad_hor(ch.slider, 15, LV_PART_KNOB);  // a 40x20 pill
  lv_obj_set_style_pad_ver(ch.slider, 5, LV_PART_KNOB);
  lv_obj_set_ext_click_area(ch.slider, 18);
  lv_obj_set_gesture_bubble(ch.slider, false);  // dragging it isn't a page swipe
  lv_obj_add_event_cb(ch.slider, onFader, LV_EVENT_VALUE_CHANGED, user);
  lv_obj_add_event_cb(ch.slider, onFader, LV_EVENT_RELEASED, user);

  // The name is the mute button, amber while muted (a name and a mute icon under each
  // fader took the height the faders needed).
  ch.pill = button(col, LV_PCT(100), 36, kRaised, onChannelMute, user);
  lv_obj_set_style_radius(ch.pill, 10, 0);
  lv_obj_set_style_pad_hor(ch.pill, 2, 0);
  ch.name = label(ch.pill, name, &lv_font_montserrat_14, master ? kText : kSoftText);
  ellipsize(ch.name);
  lv_obj_set_style_text_align(ch.name, LV_TEXT_ALIGN_CENTER, 0);
  lv_obj_center(ch.name);
}

// The level, with the mute sign while muted; the fader's fill greys out then too.
void showLevel(Channel &ch, int level, bool muted) {
  char text[16];
  snprintf(text, sizeof(text), muted ? LV_SYMBOL_MUTE " %d" : "%d", level);
  setText(ch.value, text);
  setTextColor(ch.value, muted ? kAmber : kText);
}

// False while the finger is on the fader or has just left it (a report from the PC can
// predate the drag and would snap it back): the caller tries again until it's taken.
// Before any touch there's nothing to wait out: at boot, millis() is below the hold too,
// and the first mixer report used to be dropped for good (seen in the simulator).
bool setChannel(Channel &ch, int level, bool muted) {
  const bool master = ch.id.length() == 0;
  setBgColor(ch.pill, muted ? kAmberBg : kRaised);
  setTextColor(ch.name, muted ? kAmber : master ? kText : kSoftText);
  const lv_color_t fill = hex(muted ? kMuted : kAccent);
  if (!lv_color_eq(lv_obj_get_style_bg_color(ch.slider, LV_PART_INDICATOR), fill)) {
    lv_obj_set_style_bg_color(ch.slider, fill, LV_PART_INDICATOR);
  }
  const bool touched = lv_obj_has_state(ch.slider, LV_STATE_PRESSED) ||
                       (ch.touched_ms != 0 && millis() - ch.touched_ms < kHoldAfterTouchMs);
  if (touched) return false;
  if (level >= 0) {
    if (lv_slider_get_value(ch.slider) != level) lv_slider_set_value(ch.slider, level, LV_ANIM_OFF);
    showLevel(ch, level, muted);
  }
  return true;
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
    lv_obj_clean(faders);
    channels[0] = Channel();
    buildChannel(0, "Master", true);
    for (int i = 0; i < mx.count; i++) {
      channels[i + 1] = Channel();
      channels[i + 1].id = mx.apps[i].id;
      buildChannel(i + 1, mx.apps[i].name.c_str(), false);
    }
    channel_count = 1 + mx.count;
  }
  bool taken = setChannel(channels[0], vol.level, vol.muted);
  for (int i = 0; i < mx.count; i++) taken &= setChannel(channels[i + 1], mx.apps[i].level, mx.apps[i].muted);
  // A fader still under (or just off) the finger didn't take its level: again next loop,
  // so a change made meanwhile isn't lost until the next report.
  if (!taken) mixer_seen = UINT32_MAX;
}

class Mixer : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildMixer(c); }
  void update() override { updateMixer(); }
};

}  // namespace

extern const Kind kMixer = {"mixer", {10, 8}, {}, []() -> Tile * { return new Mixer(); }};

}  // namespace ui::tiles
