#include "pc.h"

#include <Arduino.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <Update.h>
#include <stdarg.h>

#include "USB.h"
#include "USBCDC.h"
#include "USBHIDConsumerControl.h"
#include "USBHIDKeyboard.h"
#include "board.h"
#include "esp32-hal-tinyusb.h"
#include "esp_heap_caps.h"
#include "esp_system.h"
#include "display.h"
#include "icons.h"
#include "idle.h"
#include "mbedtls/base64.h"
#include "model.h"
#include "ui.h"

namespace pc {
namespace {

// pid.codes test ID, so the deck never shares Espressif's 303A:1001 with the chip's
// own USB-Serial-JTAG bootloader port.
constexpr uint16_t kUsbVid = 0x1209;
constexpr uint16_t kUsbPid = 0x0001;

constexpr uint32_t kCompanionTimeoutMs = 5000;  // the companion pings every 2 s
constexpr size_t kMaxLine = 8192;               // image parts are ~4.2 KB
// Arduino's default is 256 bytes, which a burst of state messages overflows while the
// loop is busy drawing a frame; the overflow silently drops bytes mid-line. Album art
// arrives as ~4 KB parts every 15 ms, and a busy frame can take ~50 ms.
constexpr size_t kRxBuffer = 32 * 1024;

USBCDC cdc;
USBHIDKeyboard keyboard;
USBHIDConsumerControl consumer;

String rx;
bool port_open = false;
bool heard = false;
uint32_t last_rx_ms = 0;
int seen_count = 0;  // state topics remembered (seen_state, below handle())
volatile uint32_t rx_dropped = 0;  // bytes lost to a full receive buffer (USB task)

// An album-art image being reassembled from "img" parts.
struct {
  String id;
  uint8_t *pixels = nullptr;
  size_t size = 0;
  int w = 0;
  int h = 0;
  int parts = 0;
  int received = 0;
} incoming;

void send(const JsonDocument &doc) {
  if (!cdc) return;  // nobody has the port open
  serializeJson(doc, cdc);
  cdc.write('\n');
}

// Why the deck last restarted, so crashes show up in the companion's log.
const char *resetReason() {
  switch (esp_reset_reason()) {
    case ESP_RST_POWERON: return "power-on";
    case ESP_RST_EXT: return "reset pin";
    case ESP_RST_SW: return "software";
    case ESP_RST_PANIC: return "panic";
    case ESP_RST_INT_WDT: return "interrupt watchdog";
    case ESP_RST_TASK_WDT: return "task watchdog";
    case ESP_RST_WDT: return "watchdog";
    case ESP_RST_BROWNOUT: return "brownout";
    case ESP_RST_USB: return "usb";
    default: return "other";
  }
}

// The screen as it is, for checking layouts from the PC: the frame buffer on show in
// base64 parts of 3 KB, RGB565 little-endian, row by row. Takes a couple of seconds.
void sendShot() {
  const uint8_t *fb = static_cast<const uint8_t *>(display::frontBuffer());
  if (!fb || !cdc) return;
  constexpr size_t kPart = 3072;
  constexpr size_t kSize = size_t(board::kWidth) * board::kHeight * 2;
  constexpr int kParts = int((kSize + kPart - 1) / kPart);
  static unsigned char b64[4 * kPart / 3 + 4];
  char head[96];
  for (int i = 0; i < kParts; i++) {
    const size_t at = size_t(i) * kPart;
    size_t len = 0;
    mbedtls_base64_encode(b64, sizeof(b64), &len, fb + at, min(kPart, kSize - at));
    const int n = snprintf(head, sizeof(head), "{\"t\":\"shot\",\"w\":%d,\"h\":%d,\"i\":%d,\"n\":%d,\"d\":\"",
                           board::kWidth, board::kHeight, i, kParts);
    cdc.write(reinterpret_cast<const uint8_t *>(head), n);
    cdc.write(b64, len);
    cdc.write(reinterpret_cast<const uint8_t *>("\"}\n"), 3);
  }
}

// Sent only in reply to "sync". Saying hello when DTR rises raced the companion's
// handshake, which toggles DTR/RTS faster than the loop samples them.
void sendHello() {
  seen_count = 0;  // the companion sends everything after this: take it all
  JsonDocument doc;
  doc["t"] = "hello";
  doc["fw"] = kFirmwareVersion;
  doc["w"] = board::kWidth;
  doc["h"] = board::kHeight;
  doc["reset"] = resetReason();
  doc["ram"] = heap_caps_get_free_size(MALLOC_CAP_INTERNAL);
  doc["psram"] = heap_caps_get_free_size(MALLOC_CAP_SPIRAM);
  const board::TouchInfo touch = board::touchInfo();
  if (touch.ok) {
    doc["touch"]["cfg"] = touch.config_version;
    doc["touch"]["low_power_s"] = touch.low_power;
    doc["touch"]["refresh"] = touch.refresh;
  }
  send(doc);
}

void act(const char *action) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = action;
  send(doc);
}

void act(const char *action, const char *key, const char *value) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = action;
  doc[key] = value;
  send(doc);
}

// Arduino's CDC reboots into the bootloader on two triggers: a 1200-baud open, and
// DTR/RTS toggling the way esptool resets a chip. The second one also fires on ordinary
// DTR/RTS changes from the companion (and leaves the port half-closed when it doesn't),
// so it's switched off and only the 1200-baud trigger, which uploads use, is kept.
void onLineCoding(void *, esp_event_base_t, int32_t, void *data) {
  const auto *e = static_cast<const arduino_usb_cdc_event_data_t *>(data);
  if (e->line_coding.bit_rate == 1200) usb_persist_restart(RESTART_BOOTLOADER);
}

void onRxOverflow(void *, esp_event_base_t, int32_t, void *data) {
  rx_dropped += static_cast<const arduino_usb_cdc_event_data_t *>(data)->rx_overflow.dropped_bytes;
}

volatile int8_t num_lock = -1;  // -1 until the PC reports its keyboard lights

void onKeyboardLeds(void *, esp_event_base_t, int32_t, void *data) {
  num_lock = static_cast<const arduino_usb_hid_keyboard_event_data_t *>(data)->numlock;
}

void consumerKey(uint16_t usage) {
  consumer.press(usage);
  consumer.release();
}

void handleMedia(JsonObjectConst d) {
  model::Media &m = model::media;
  m.present = d["present"] | false;
  m.title = d["title"] | "";
  m.artist = d["artist"] | "";
  m.app = d["app"] | "";
  m.playing = d["playing"] | false;
  m.pos = d["pos"] | 0.0f;
  m.len = d["len"] | 0.0f;
  m.pos_at_ms = millis();
  m.shuffle = d["shuffle"].isNull() ? -1 : (d["shuffle"] | false) ? 1 : 0;
  m.repeat = d["repeat"] | "";
  m.art = d["art"] | "";
  m.version++;
}

void handleMixer(JsonObjectConst d) {
  model::Mixer &mx = model::mixer;
  mx.count = 0;
  for (JsonObjectConst a : d["apps"].as<JsonArrayConst>()) {
    if (mx.count == model::Mixer::kMax) break;
    model::App &app = mx.apps[mx.count++];
    app.id = a["id"] | "";
    app.name = a["name"] | "";
    app.level = a["v"] | 0;
    app.muted = a["muted"] | false;
  }
  mx.version++;
}

void handleClaude(JsonObjectConst d) {
  model::Claude &c = model::claude;
  c.count = 0;
  for (JsonObjectConst s : d["sessions"].as<JsonArrayConst>()) {
    if (c.count == model::Claude::kMax) break;
    model::ClaudeSession &out = c.sessions[c.count++];
    out.id = s["id"] | "";
    out.project = s["project"] | "";
    out.state = s["state"] | "";
    out.detail = s["detail"] | "";
    out.secs = s["secs"] | 0;
  }
  c.at_ms = millis();
  c.version++;
}

void handleTerm(JsonObjectConst d) {
  model::Term &t = model::term;
  t.repo = d["repo"] | "";
  t.cmd = d["cmd"] | "";
  t.running = d["running"] | false;
  t.secs = d["secs"] | -1;
  t.has_exit = !d["exit"].isNull();
  t.exit = d["exit"] | 0;
  t.ms = d["ms"] | 0;
  t.line_count = 0;
  for (const char *line : d["lines"].as<JsonArrayConst>()) {
    if (t.line_count == model::Term::kLines) break;
    t.lines[t.line_count++] = line ? line : "";
  }
  t.at_ms = millis();
  t.version++;
}

void handleGit(JsonObjectConst d) {
  model::Git &g = model::git;
  g.repo = d["repo"] | "";
  g.branch = d["branch"] | "";
  g.last = d["last"] | "";
  g.busy = d["busy"] | "";
  g.error = d["error"] | "";
  g.ahead = d["ahead"] | 0;
  g.behind = d["behind"] | 0;
  g.modified = d["m"] | 0;
  g.added = d["a"] | 0;
  g.deleted = d["del"] | 0;
  g.untracked = d["u"] | 0;
  g.last_secs = d["last_secs"] | -1;
  g.at_ms = millis();
  g.version++;
}

// Idle timings take effect at once; a different palette is saved and the deck restarts
// into it, since every page is built with the palette's colors.
void handleSettings(const JsonDocument &doc) {
  model::settings.clock_after_s = doc["idle_clock_s"] | model::settings.clock_after_s;
  model::settings.off_after_s = doc["idle_off_s"] | model::settings.off_after_s;
  const char *palette = doc["palette"] | "";
  if (!palette[0]) return;
  Preferences prefs;
  prefs.begin("deck", false);
  const String saved = prefs.getString("palette", "base");
  if (saved != palette) {
    prefs.putString("palette", palette);
    prefs.end();
    log("palette %s: restarting", palette);
    cdc.flush();
    delay(200);
    ESP.restart();
  }
  prefs.end();
}

void handleWindows(JsonObjectConst d) {
  model::Windows &w = model::windows;
  w.count = 0;
  for (JsonObjectConst item : d["list"].as<JsonArrayConst>()) {
    if (w.count == model::Windows::kMax) break;
    model::Window &win = w.list[w.count++];
    win.id = item["id"] | "";
    win.app = item["app"] | "";
    win.title = item["title"] | "";
    win.icon = item["icon"] | "";
    win.active = item["active"] | false;
    win.minimized = item["min"] | false;
  }
  w.version++;
}

void handleZones(JsonObjectConst d) {
  model::Zones &z = model::zones;
  z.mon_count = 0;
  for (JsonObjectConst m : d["mons"].as<JsonArrayConst>()) {
    if (z.mon_count == model::Zones::kMax) break;
    model::Monitor &mon = z.mons[z.mon_count++];
    mon.name = m["name"] | "";
    mon.primary = m["primary"] | false;
    mon.w = m["w"] | 0;
    mon.h = m["h"] | 0;
    mon.type = m["type"] | "";
    mon.layout = m["layout"] | "";
    mon.count = m["n"] | 0;
    mon.zone_count = 0;
    for (JsonArrayConst zone : m["zones"].as<JsonArrayConst>()) {
      if (mon.zone_count == model::Monitor::kMaxZones) break;
      model::Zone &out = mon.zones[mon.zone_count++];
      out.x = zone[0] | 0;
      out.y = zone[1] | 0;
      out.w = zone[2] | 0;
      out.h = zone[3] | 0;
      out.app = zone[4] | "";
    }
  }
  z.layout_count = 0;
  for (JsonObjectConst l : d["layouts"].as<JsonArrayConst>()) {
    if (z.layout_count == model::Zones::kMaxLayouts) break;
    model::LayoutChoice &out = z.layouts[z.layout_count++];
    out.id = l["id"] | "";
    out.name = l["name"] | "";
    out.custom = l["custom"] | false;
    out.cell_count = 0;
    for (JsonArrayConst c : l["cells"].as<JsonArrayConst>()) {
      if (out.cell_count == model::LayoutChoice::kMaxCells) break;
      auto &cell = out.cells[out.cell_count++];
      cell.x = c[0] | 0;
      cell.y = c[1] | 0;
      cell.w = c[2] | 1;
      cell.h = c[3] | 1;
    }
  }
  z.active = d["active"] | 0;
  z.app = d["app"] | "";
  z.version++;
}

int readDevices(JsonArrayConst list, model::Device *out) {
  int n = 0;
  for (JsonObjectConst d : list) {
    if (n == model::Devices::kMax) break;
    model::Device &dev = out[n++];
    dev.id = d["id"] | "";
    dev.name = d["name"] | "";
    dev.detail = d["detail"] | "";
    dev.selected = d["sel"] | false;
  }
  return n;
}

void handleState(const char *topic, JsonObjectConst d) {
  if (!strcmp(topic, "media")) {
    handleMedia(d);
  } else if (!strcmp(topic, "vol")) {
    model::volume.level = d["v"] | -1;
    model::volume.muted = d["muted"] | false;
    model::volume.version++;
  } else if (!strcmp(topic, "mixer")) {
    handleMixer(d);
  } else if (!strcmp(topic, "devices")) {
    model::devices.out_count = readDevices(d["out"], model::devices.out);
    model::devices.in_count = readDevices(d["in"], model::devices.in);
    model::devices.version++;
  } else if (!strcmp(topic, "mic")) {
    model::mic.available = d["available"] | false;
    model::mic.muted = d["muted"] | false;
    model::mic.version++;
  } else if (!strcmp(topic, "claude")) {
    handleClaude(d);
  } else if (!strcmp(topic, "term")) {
    handleTerm(d);
  } else if (!strcmp(topic, "build")) {
    model::Build &b = model::build;
    b.state = d["state"] | "none";
    b.cmd = d["cmd"] | "";
    b.history = d["history"] | "";
    b.ms = d["ms"] | 0;
    b.secs = d["secs"] | -1;
    b.at_ms = millis();
    b.version++;
  } else if (!strcmp(topic, "recent")) {
    model::Recent &r = model::recent;
    r.count = 0;
    for (JsonObjectConst c : d["list"].as<JsonArrayConst>()) {
      if (r.count == model::Recent::kMax) break;
      model::RecentCommand &out = r.list[r.count++];
      out.cmd = c["cmd"] | "";
      out.running = c["running"] | false;
      out.has_exit = !c["exit"].isNull();
      out.exit = c["exit"] | 0;
      out.ms = c["ms"] | 0;
      out.secs = c["secs"] | -1;
    }
    r.at_ms = millis();
    r.version++;
  } else if (!strcmp(topic, "actions")) {
    model::Actions &a = model::actions;
    a.count = 0;
    for (JsonObjectConst x : d["list"].as<JsonArrayConst>()) {
      if (a.count == model::Actions::kMax) break;
      model::RecentAction &out = a.list[a.count++];
      out.id = x["id"] | "";
      out.kind = x["kind"] | "";
      out.name = x["name"] | "";
      out.title = x["title"] | "";
      out.icon = x["icon"] | "";
    }
    a.version++;
  } else if (!strcmp(topic, "git")) {
    handleGit(d);
  } else if (!strcmp(topic, "windows")) {
    handleWindows(d);
  } else if (!strcmp(topic, "zones")) {
    handleZones(d);
  } else if (!strcmp(topic, "clips")) {
    model::Clips &c = model::clips;
    c.count = 0;
    for (JsonObjectConst item : d["list"].as<JsonArrayConst>()) {
      if (c.count == model::Clips::kMax) break;
      model::Clip &clip = c.list[c.count++];
      clip.id = item["id"] | "";
      clip.text = item["text"] | "";
      clip.kind = item["kind"] | "text";
      clip.pinned = item["pinned"] | false;
      clip.secs = item["secs"] | 0;
    }
    c.at_ms = millis();
    c.version++;
  } else if (!strcmp(topic, "obsidian")) {
    model::Obsidian &o = model::obsidian;
    o.found = d["found"] | false;
    o.vault = d["vault"] | "";
    o.recent_count = 0;
    for (JsonObjectConst n : d["recent"].as<JsonArrayConst>()) {
      if (o.recent_count == model::Obsidian::kMaxRecent) break;
      o.recent[o.recent_count].name = n["name"] | "";
      o.recent[o.recent_count].path = n["path"] | "";
      o.recent_count++;
    }
    o.version++;
  } else if (!strcmp(topic, "google")) {
    model::Google &g = model::google;
    g.status = d["status"] | "";
    g.event_count = 0;
    for (JsonObjectConst e : d["events"].as<JsonArrayConst>()) {
      if (g.event_count == model::Google::kMaxEvents) break;
      model::Event &ev = g.events[g.event_count++];
      ev.id = e["id"] | "";
      ev.title = e["title"] | "";
      ev.start = e["start"] | int64_t(0);
      ev.end = e["end"] | int64_t(0);
      ev.all_day = e["allday"] | false;
      ev.join = e["join"] | false;
    }
    g.task_count = 0;
    for (JsonObjectConst t : d["tasks"].as<JsonArrayConst>()) {
      if (g.task_count == model::Google::kMaxTasks) break;
      model::MailTask &task = g.tasks[g.task_count++];
      task.list = t["list"] | "";
      task.id = t["id"] | "";
      task.title = t["title"] | "";
      task.from = t["from"] | "";
      task.due = t["due"] | "";
      task.done = t["done"] | false;
    }
    g.open = d["open"] | 0;
    g.version++;
  } else if (!strcmp(topic, "call")) {
    model::Call &c = model::call;
    c.app = d["app"] | "";
    c.name = d["name"] | "";
    c.secs = d["secs"] | 0;
    c.at_ms = millis();
    c.mic_muted = d["mic_muted"] | false;
    c.camera = d["camera"] | -1;
    c.hand = d["hand"] | -1;
    c.deafened = d["deafened"] | false;
    c.can_mic = c.can_camera = c.can_hand = c.can_deafen = c.can_leave = c.can_react = false;
    for (const char *what : d["can"].as<JsonArrayConst>()) {
      if (!what) continue;
      if (!strcmp(what, "mic")) c.can_mic = true;
      else if (!strcmp(what, "camera")) c.can_camera = true;
      else if (!strcmp(what, "hand")) c.can_hand = true;
      else if (!strcmp(what, "deafen")) c.can_deafen = true;
      else if (!strcmp(what, "leave")) c.can_leave = true;
      else if (!strcmp(what, "react")) c.can_react = true;
    }
    c.note = d["note"] | "";
    c.volume = d["volume"] | -1;
    c.version++;
  } else if (!strcmp(topic, "pc")) {
    model::pc.locked = d["locked"] | false;
    model::pc.idle_s = d["idle"] | 0;
    model::pc.at_ms = millis();
    model::pc.version++;
  } else if (!strcmp(topic, "kbd")) {
    model::Keyboard &k = model::keyboard;
    k.present = d["present"] | false;
    k.name = d["name"] | "";
    k.label = d["label"] | "";
    k.layer = d["layer"] | 0;
    const int held_ms = d["held_ms"] | -1;
    k.held = held_ms >= 0;
    k.held_at_ms = millis() - uint32_t(max(held_ms, 0));
    k.key = d["key"] | -1;
    k.version++;
  } else if (!strcmp(topic, "keymap")) {
    model::Keymap &m = model::keymap;
    m.count = 0;
    for (JsonArrayConst k : d["keys"].as<JsonArrayConst>()) {
      if (m.count >= model::Keymap::kMaxKeys) break;
      m.keys[m.count++] = {int16_t(k[0] | 0), int16_t(k[1] | 0), int16_t(k[2] | 100), int16_t(k[3] | 100)};
    }
    m.layer_count = 0;
    for (JsonObjectConst l : d["layers"].as<JsonArrayConst>()) {
      if (m.layer_count >= model::Keymap::kMaxLayers) break;
      model::Keymap::Layer &out = m.layers[m.layer_count++];
      out.index = l["n"] | -1;
      out.name = l["name"] | "";
      out.labels = "";
      bool first = true;
      for (const char *label : l["keys"].as<JsonArrayConst>()) {
        if (!first) out.labels += '\t';
        out.labels += label ? label : "";
        first = false;
      }
      out.dim = l["dim"] | "";
    }
    m.version++;
  }
}

// A window icon: one part, RGB565A8.
void handleIcon(const JsonDocument &doc) {
  const char *id = doc["id"] | "";
  const int w = doc["w"] | 0;
  const int h = doc["h"] | 0;
  if (strcmp(doc["fmt"] | "", "rgb565a8") || w <= 0 || h <= 0 || w > 64 || h > 64 || !id[0]) return;
  const size_t size = size_t(w) * h * 3;
  auto *pixels = static_cast<uint8_t *>(heap_caps_malloc(size, MALLOC_CAP_SPIRAM));
  if (!pixels) return;
  const char *b64 = doc["b64"] | "";
  size_t written = 0;
  if (mbedtls_base64_decode(pixels, size, &written, reinterpret_cast<const unsigned char *>(b64), strlen(b64)) != 0 ||
      written != size) {
    log("icon %s: bad data", id);
    heap_caps_free(pixels);
    return;
  }
  icons::put(id, pixels, w, h);
}

// One part of an RGB565 image. Parts carry their byte offset, so order doesn't matter;
// the image is handed to the UI once every part has arrived.
void handleImage(const JsonDocument &doc) {
  if (!strcmp(doc["kind"] | "", "icon")) {
    handleIcon(doc);
    return;
  }
  const char *id = doc["id"] | "";
  const int part = doc["part"] | -1;
  const int parts = doc["parts"] | 0;
  const int w = doc["w"] | 0;
  const int h = doc["h"] | 0;
  if (strcmp(doc["fmt"] | "", "rgb565") || w <= 0 || h <= 0 || w > 256 || h > 256 || parts <= 0) return;

  if (incoming.id != id) {
    if (incoming.pixels) heap_caps_free(incoming.pixels);
    incoming.size = size_t(w) * h * 2;
    incoming.pixels = static_cast<uint8_t *>(heap_caps_malloc(incoming.size, MALLOC_CAP_SPIRAM));
    incoming.id = id;
    incoming.w = w;
    incoming.h = h;
    incoming.parts = parts;
    incoming.received = 0;
    if (!incoming.pixels) {
      log("img: no memory for %dx%d", w, h);
      incoming.id = "";
      return;
    }
  }

  const size_t off = doc["off"] | 0;
  const char *b64 = doc["b64"] | "";
  size_t written = 0;
  if (part < 0 || off >= incoming.size ||
      mbedtls_base64_decode(incoming.pixels + off, incoming.size - off, &written,
                            reinterpret_cast<const unsigned char *>(b64), strlen(b64)) != 0) {
    log("img %s: bad part %d", id, part);
    return;
  }
  if (++incoming.received < incoming.parts) return;

  model::Art &art = model::art;
  if (art.pending) heap_caps_free(art.pending);  // the UI never got to the previous one
  art.pending = incoming.pixels;
  art.pending_id = incoming.id;
  art.w = incoming.w;
  art.h = incoming.h;
  art.version++;
  incoming.pixels = nullptr;
  incoming.id = "";
}

void otaReply(const char *type, const char *key = nullptr, uint32_t value = 0) {
  JsonDocument doc;
  doc["t"] = type;
  if (key) doc[key] = value;
  send(doc);
}

void otaFail(const char *why) {
  Update.abort();
  JsonDocument doc;
  doc["t"] = "ota_error";
  doc["msg"] = why;
  send(doc);
  Serial.printf("ota: %s\n", why);
}

// Firmware updates over this link (scripts/ota_upload.py), written into the spare app
// slot. Flashing through the chip's ROM bootloader port kept wedging its USB until a
// replug; this path never leaves the running firmware. The uploader waits for each ack,
// so the receive buffer can't overflow while flash writes stall the loop.
void handleOta(const char *t, const JsonDocument &doc) {
  // Bytes received so far. Update.progress() can't stand in for it: it only moves when a
  // whole 4 KB flash sector has been written.
  static size_t received = 0;
  if (!strcmp(t, "ota")) {
    const size_t size = doc["size"] | 0;
    if (Update.isRunning()) Update.abort();
    received = 0;
    if (size == 0 || !Update.begin(size, U_FLASH)) return otaFail(Update.errorString());
    otaReply("ota_ready", "size", size);
  } else if (!strcmp(t, "ota_data")) {
    if (!Update.isRunning()) return otaFail("no update in progress");
    if ((doc["off"] | size_t(0)) != received) return otaFail("out-of-order chunk");
    static uint8_t chunk[4096];
    const char *b64 = doc["b64"] | "";
    size_t n = 0;
    if (mbedtls_base64_decode(chunk, sizeof(chunk), &n, reinterpret_cast<const unsigned char *>(b64),
                              strlen(b64)) != 0) {
      return otaFail("bad base64");
    }
    if (Update.write(chunk, n) != n) return otaFail(Update.errorString());
    received += n;
    otaReply("ota_ack", "off", received);
  } else if (!strcmp(t, "ota_end")) {
    if (!Update.end()) return otaFail(Update.errorString());  // verifies, sets the boot slot
    otaReply("ota_done");
    cdc.flush();
    delay(300);
    ESP.restart();
  }
}

void handle(const JsonDocument &doc) {
  const char *t = doc["t"] | "";
  if (!strcmp(t, "state")) {
    handleState(doc["topic"] | "", doc["d"].as<JsonObjectConst>());
  } else if (!strcmp(t, "img")) {
    handleImage(doc);
  } else if (!strncmp(t, "ota", 3)) {
    handleOta(t, doc);
  } else if (!strcmp(t, "toast")) {
    model::toast.level = doc["level"] | "ok";
    model::toast.text = doc["text"] | "";
    model::toast.action = doc["action"] | "";
    model::toast.id = doc["id"] | "";
    model::toast.version++;
  } else if (!strcmp(t, "settings")) {
    handleSettings(doc);
  } else if (!strcmp(t, "time")) {
    model::clock.epoch = doc["epoch"] | int64_t(0);
    model::clock.tz_offset_min = doc["tz"] | 0;
    model::clock.synced_at_ms = millis();
  } else if (!strcmp(t, "sync")) {
    sendHello();
  } else if (!strcmp(t, "bench")) {
    model::bench_requests++;
  } else if (!strcmp(t, "page")) {
    // From the companion's test endpoint, before a "shot": wakes the screen too.
    ui::idle::wake();
    ui::showPage(doc["i"] | 0);
  } else if (!strcmp(t, "shot")) {
    sendShot();
  }
  // "ping" needs no reply; receiving it is the point.
}

// The last state line per topic, as a hash. When the deck loses part of a burst, the
// companion sends every topic again; applying an unchanged snapshot is wasted work, and
// some is slow (a keymap rebuilds the cheat sheet), long enough for the next burst to
// overflow again. So a repeat is skipped. Forgotten when the port opens again.
struct SeenState {
  char topic[16];
  uint32_t hash;
};
constexpr int kSeenTopics = 32;
SeenState seen_state[kSeenTopics];

uint32_t lineHash(const String &s) {
  uint32_t h = 2166136261u;  // FNV-1a
  for (size_t i = 0; i < s.length(); i++) h = (h ^ uint8_t(s[i])) * 16777619u;
  return h;
}

// True if this topic's last snapshot was the same; remembers this one either way.
bool sameAsLast(const char *topic, uint32_t hash) {
  for (int i = 0; i < seen_count; i++) {
    if (strcmp(seen_state[i].topic, topic) != 0) continue;
    const bool same = seen_state[i].hash == hash;
    seen_state[i].hash = hash;
    return same;
  }
  if (seen_count < kSeenTopics && strlen(topic) < sizeof(SeenState::topic)) {
    strcpy(seen_state[seen_count].topic, topic);
    seen_state[seen_count++].hash = hash;
  }
  return false;
}

void handleLine() {
  JsonDocument doc;
  if (deserializeJson(doc, rx) != DeserializationError::Ok) {
    log("bad line (%u bytes)", unsigned(rx.length()));
    return;
  }
  heard = true;
  last_rx_ms = millis();
  if (!strcmp(doc["t"] | "", "state") && sameAsLast(doc["topic"] | "", lineHash(rx))) return;
  handle(doc);
}

}  // namespace

void begin() {
  keyboard.onEvent(ARDUINO_USB_HID_KEYBOARD_LED_EVENT, onKeyboardLeds);
  keyboard.begin();
  consumer.begin();
  cdc.setRxBufferSize(kRxBuffer);
  cdc.enableReboot(false);
  cdc.onEvent(ARDUINO_USB_CDC_LINE_CODING_EVENT, onLineCoding);
  cdc.onEvent(ARDUINO_USB_CDC_RX_OVERFLOW_EVENT, onRxOverflow);
  cdc.begin();
  USB.VID(kUsbVid);
  USB.PID(kUsbPid);
  USB.manufacturerName("flex-screen");
  USB.productName("flex-screen deck");
  USB.begin();
  rx.reserve(kMaxLine);
}

void poll() {
  const bool open = cdc;  // the host raised DTR and RTS
  if (open && !port_open) {
    rx = "";
    heard = false;
    seen_count = 0;  // a new companion run: take every snapshot again
  }
  port_open = open;

  if (rx_dropped) {
    log("receive buffer overflowed, %u bytes lost", unsigned(rx_dropped));
    rx_dropped = 0;
  }

  uint8_t chunk[512];
  while (cdc.available()) {
    const size_t n = cdc.read(chunk, sizeof(chunk));
    for (size_t i = 0; i < n; i++) {
      const char c = char(chunk[i]);
      if (c == '\n') {
        if (rx.length()) handleLine();
        rx = "";
      } else if (c != '\r' && rx.length() < kMaxLine) {
        rx += c;
      }
    }
  }
}

bool connected() { return port_open && heard && millis() - last_rx_ms < kCompanionTimeoutMs; }

void media(MediaCmd cmd) {
  if (connected()) {
    act(cmd == MediaCmd::Toggle ? "media.toggle"
        : cmd == MediaCmd::Next ? "media.next"
                                : "media.prev");
    return;
  }
  consumerKey(cmd == MediaCmd::Toggle ? CONSUMER_CONTROL_PLAY_PAUSE
              : cmd == MediaCmd::Next ? CONSUMER_CONTROL_SCAN_NEXT
                                      : CONSUMER_CONTROL_SCAN_PREVIOUS);
}

void toggleShuffle() { act("media.shuffle"); }
void cycleRepeat() { act("media.repeat"); }

void setVolume(int level) {
  if (!connected()) return;
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = "vol.set";
  doc["v"] = level;
  send(doc);
}

void toggleMute() {
  if (connected()) {
    act("vol.mute");
  } else {
    consumerKey(CONSUMER_CONTROL_MUTE);
  }
}

void setAppVolume(const char *app, int level) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = "mixer.set";
  doc["app"] = app;
  doc["v"] = level;
  send(doc);
}

void toggleAppMute(const char *app) { act("mixer.mute", "app", app); }
void selectOutput(const char *device) { act("dev.out", "id", device); }
void selectInput(const char *device) { act("dev.in", "id", device); }
void toggleMic() { act("mic.mute"); }
void openClaude(const char *session) { act("claude.open", "id", session); }
void replayCommand(const char *cmd) { act("term.replay", "cmd", cmd); }
void runRecent(const char *id) { act("recent.run", "id", id); }
void interruptCommand() { act("term.interrupt"); }
void focusTerminal() { act("term.focus"); }

void git(const char *operation) {
  char action[16];
  snprintf(action, sizeof(action), "git.%s", operation);
  act(action);
}

void runAction(const char *action, const char *id) { act(action, "id", id); }
void lockPc() { act("pc.lock"); }
void callReact(const char *type) { act("call.react", "type", type); }

void callVolume(int level) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = "call.volume";
  doc["v"] = level;
  send(doc);
}
void focusWindow(const char *id) { act("win.focus", "id", id); }
void closeWindow(const char *id) { act("win.close", "id", id); }
void requestIcon(const char *id) { act("icon.need", "id", id); }

void showDesktop() {
  keyboard.press(KEY_LEFT_GUI);
  keyboard.press('d');
  keyboard.releaseAll();
}

void snapWindow(int monitor, int zone) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = "zone.snap";
  doc["mon"] = monitor;
  doc["zone"] = zone;
  send(doc);
}

void setLayout(int monitor, const char *id) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = "zone.layout";
  doc["mon"] = monitor;
  doc["id"] = id;
  send(doc);
}

void pressKey(uint8_t key) { keyboard.press(key); }
void releaseKey(uint8_t key) { keyboard.release(key); }

void tapKey(uint8_t key) {
  keyboard.press(key);
  keyboard.release(key);
}

void releaseAllKeys() { keyboard.releaseAll(); }
bool numLockKnown() { return num_lock >= 0; }
bool numLock() { return num_lock == 1; }

void typeText(const char *text) {
  if (connected()) act("key.text", "text", text);
  else keyboard.print(text);
}

void pasteClip(const char *id) { act("clip.paste", "id", id); }
void pinClip(const char *id) { act("clip.pin", "id", id); }
void clearClips() { act("clip.clear"); }

void obsidian(const char *what) {
  char action[16];
  snprintf(action, sizeof(action), "obs.%s", what);
  act(action);
}

void openNote(const char *path) { act("obs.open", "path", path); }
void joinMeeting(const char *id) { act("cal.join", "id", id); }
void openMail() { act("mail.open"); }

void setTaskDone(const char *list, const char *id, bool done) {
  JsonDocument doc;
  doc["t"] = "act";
  doc["a"] = "task.done";
  doc["list"] = list;
  doc["id"] = id;
  doc["done"] = done;
  send(doc);
}

void log(const char *fmt, ...) {
  char msg[160];
  va_list args;
  va_start(args, fmt);
  vsnprintf(msg, sizeof(msg), fmt, args);
  va_end(args);
  Serial.println(msg);
  JsonDocument doc;
  doc["t"] = "log";
  doc["msg"] = msg;
  send(doc);
}

}  // namespace pc
