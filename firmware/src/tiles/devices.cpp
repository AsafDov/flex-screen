// The Devices tile (Audio): the PC's outputs, then its inputs, each a list under its
// heading; a tap makes one the Windows default. Every name gets the tile's width (side by
// side the third output was cut off), and more devices than fit scroll, from anywhere in
// the card (up to model::Devices::kMax of each kind). One of a kind (the
// manager places each kind once), so its state lives here.
#include "model.h"
#include "pc.h"
#include "tiles.h"
#include "ui_kit.h"

namespace ui::tiles {
namespace {

constexpr uint32_t kSwitchTimeoutMs = 4000;
constexpr int kDeviceH = 32;  // a device's button

struct {
  lv_obj_t *outputs;
  lv_obj_t *inputs;
} w;

uint32_t devices_seen = UINT32_MAX;
int shown_output = -1;  // which device button is lit, which may be ahead of the PC
int shown_input = -1;
uint32_t switch_pending_ms = 0;

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

lv_obj_t *buildList(lv_obj_t *parent, bool output) {
  lv_obj_t *head = box(parent);
  lv_obj_set_size(head, LV_PCT(100), LV_SIZE_CONTENT);
  flexRow(head, 6);
  if (output) {
    label(head, LV_SYMBOL_VOLUME_MAX, &lv_font_montserrat_14, kMuted);
  } else {
    const MicIcon mic = micIcon(head);
    setMicIcon(mic, kMuted, false);
  }
  label(head, output ? "OUTPUT" : "INPUT", &small_font, kMuted);
  lv_obj_t *list = box(parent);
  lv_obj_set_size(list, LV_PCT(100), LV_SIZE_CONTENT);
  flexCol(list, 5);
  return list;
}

void buildDevices(lv_obj_t *c) {
  lv_obj_set_style_pad_all(c, 10, 0);
  flexCol(c, 6);
  lv_obj_add_flag(c, LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_set_scroll_dir(c, LV_DIR_VER);
  lv_obj_set_scrollbar_mode(c, LV_SCROLLBAR_MODE_AUTO);
  // A drag from a heading, a gap or the padding scrolls too: a card that takes no presses
  // left those to the page, which took them as a swipe and changed page. Only a drag that
  // began on a device scrolled.
  lv_obj_set_clickable(c, true);
  w.outputs = buildList(c, true);
  lv_obj_t *gap = box(c);
  lv_obj_set_size(gap, 1, 4);
  w.inputs = buildList(c, false);
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
    // A row each, the list's width: the name from the left, "…" when too long.
    lv_obj_t *b = button(row, LV_PCT(100), kDeviceH, kRaised, cb, reinterpret_cast<void *>(intptr_t(i)));
    lv_obj_set_style_radius(b, 10, 0);
    lv_obj_set_style_pad_hor(b, 10, 0);
    lv_obj_t *l = label(b, d.name.c_str(), &body_font, kSoftText);
    ellipsize(l);
    lv_obj_align(l, LV_ALIGN_LEFT_MID, 0, 0);
    styleDevice(b, d.selected);
    if (d.selected) shown = i;
  }
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
}

class Devices : public Tile {
 public:
  void build(lv_obj_t *c, Span) override { buildDevices(c); }
  void update() override { updateDevices(); }
};

}  // namespace

// Four columns: as narrow as "Microphone Array" allows, the mixer gets the rest.
extern const Kind kDevices = {"devices", {4, 8}, {}, []() -> Tile * { return new Devices(); }};

}  // namespace ui::tiles
