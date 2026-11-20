// The Arduino, ESP-IDF and USB pieces the firmware calls, done on Windows: time, the
// UART log, Preferences, base64, the HID keyboard, and the USB serial link as a TCP server.
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <Arduino.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <USB.h>
#include <USBCDC.h>
#include <USBHIDConsumerControl.h>
#include <USBHIDKeyboard.h>
#include <Update.h>
#include <esp_system.h>
#include <mbedtls/base64.h>

#include <lvgl.h>

#include <fstream>
#include <map>
#include <sstream>

#include "board.h"
#include "display.h"
#include "idle.h"
#include "sim.h"
#include "ui.h"

HardwareSerial Serial;
EspClass ESP;
UpdateClass Update;
ESPUSB USB;

// ---- time

namespace {
LARGE_INTEGER qpc_freq, qpc_start;

uint64_t elapsedUs() {
  if (!qpc_freq.QuadPart) {
    QueryPerformanceFrequency(&qpc_freq);
    QueryPerformanceCounter(&qpc_start);
  }
  LARGE_INTEGER now;
  QueryPerformanceCounter(&now);
  return uint64_t(now.QuadPart - qpc_start.QuadPart) * 1000000ull / uint64_t(qpc_freq.QuadPart);
}
}  // namespace

uint32_t millis() { return uint32_t(elapsedUs() / 1000); }
uint32_t micros() { return uint32_t(elapsedUs()); }
void delay(uint32_t ms) { Sleep(ms); }

// ---- the log port

size_t HardwareSerial::print(const char *s) {
  fputs(s, stdout);
  fflush(stdout);
  return strlen(s);
}

size_t HardwareSerial::println(const char *s) {
  printf("%s\n", s);
  fflush(stdout);
  return strlen(s) + 1;
}

size_t HardwareSerial::printf(const char *fmt, ...) {
  va_list args;
  va_start(args, fmt);
  const int n = vprintf(fmt, args);
  va_end(args);
  fflush(stdout);
  return n < 0 ? 0 : size_t(n);
}

// ---- restarts

esp_reset_reason_t esp_reset_reason() {
  char value[8] = {};
  return GetEnvironmentVariableA("FLEX_SIM_RESET", value, sizeof(value)) && !strcmp(value, "sw") ? ESP_RST_SW
                                                                                                 : ESP_RST_POWERON;
}

// Starts this exe again with the same arguments and ends this one: what the deck does to
// apply a palette. A test reconnects to the new one.
void EspClass::restart() {
  printf("sim: restarting\n");
  fflush(stdout);
  sim::closeLink();
  SetEnvironmentVariableA("FLEX_SIM_RESET", "sw");
  STARTUPINFOW si = {sizeof(si)};
  PROCESS_INFORMATION pi = {};
  std::wstring cmd = GetCommandLineW();
  if (CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
  }
  ExitProcess(0);
}

// ---- Preferences: one file next to the exe, "namespace.key=value" per line

namespace {
std::string prefsPath() {
  char path[MAX_PATH];
  const DWORD n = GetModuleFileNameA(nullptr, path, MAX_PATH);
  std::string p(path, n);
  return p.substr(0, p.find_last_of("\\/") + 1) + "sim-prefs.txt";
}

std::map<std::string, std::string> loadPrefs() {
  std::map<std::string, std::string> all;
  std::ifstream in(prefsPath());
  std::string line;
  while (std::getline(in, line)) {
    const auto eq = line.find('=');
    if (eq != std::string::npos) all[line.substr(0, eq)] = line.substr(eq + 1);
  }
  return all;
}
}  // namespace

bool Preferences::begin(const char *name, bool) {
  ns_ = name ? name : "";
  return true;
}

String Preferences::getString(const char *key, const String &fallback) {
  const auto all = loadPrefs();
  const auto it = all.find(ns_ + "." + key);
  return it == all.end() ? fallback : String(it->second);
}

size_t Preferences::putString(const char *key, const String &value) {
  auto all = loadPrefs();
  all[ns_ + "." + key] = value.c_str();
  std::ofstream out(prefsPath(), std::ios::trunc);
  for (const auto &kv : all) out << kv.first << '=' << kv.second << '\n';
  return value.length();
}

// ---- base64 (mbedTLS's contract)

namespace {
const char kB64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

int b64Value(unsigned char c) {
  if (c >= 'A' && c <= 'Z') return c - 'A';
  if (c >= 'a' && c <= 'z') return c - 'a' + 26;
  if (c >= '0' && c <= '9') return c - '0' + 52;
  if (c == '+') return 62;
  if (c == '/') return 63;
  return -1;
}
}  // namespace

int mbedtls_base64_encode(unsigned char *dst, size_t dlen, size_t *olen, const unsigned char *src, size_t slen) {
  const size_t need = 4 * ((slen + 2) / 3);
  if (dlen < need + 1) {
    *olen = need + 1;
    return MBEDTLS_ERR_BASE64_BUFFER_TOO_SMALL;
  }
  size_t o = 0;
  for (size_t i = 0; i < slen; i += 3) {
    const uint32_t v = uint32_t(src[i]) << 16 | (i + 1 < slen ? uint32_t(src[i + 1]) << 8 : 0) |
                       (i + 2 < slen ? uint32_t(src[i + 2]) : 0);
    dst[o++] = kB64[(v >> 18) & 63];
    dst[o++] = kB64[(v >> 12) & 63];
    dst[o++] = i + 1 < slen ? kB64[(v >> 6) & 63] : '=';
    dst[o++] = i + 2 < slen ? kB64[v & 63] : '=';
  }
  dst[o] = 0;
  *olen = o;
  return 0;
}

int mbedtls_base64_decode(unsigned char *dst, size_t dlen, size_t *olen, const unsigned char *src, size_t slen) {
  size_t chars = 0, pad = 0;
  for (size_t i = 0; i < slen; i++) {
    if (src[i] == '=') {
      pad++;
      continue;
    }
    if (pad || b64Value(src[i]) < 0) return MBEDTLS_ERR_BASE64_INVALID_CHARACTER;
    chars++;
  }
  const size_t need = (chars * 6) / 8;
  if (!dst || dlen < need) {
    *olen = need;
    return MBEDTLS_ERR_BASE64_BUFFER_TOO_SMALL;
  }
  uint32_t acc = 0;
  int bits = 0;
  size_t o = 0;
  for (size_t i = 0; i < slen && src[i] != '='; i++) {
    acc = (acc << 6) | uint32_t(b64Value(src[i]));
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      dst[o++] = uint8_t(acc >> bits);
    }
  }
  *olen = o;
  return 0;
}

// ---- the keyboard and media keys: reported to the client

namespace {
void reportKey(int code, bool down) {
  char line[64];
  snprintf(line, sizeof(line), "{\"t\":\"sim.key\",\"k\":%d,\"down\":%s}", code, down ? "true" : "false");
  sim::sendLine(line);
}
}  // namespace

size_t USBHIDKeyboard::press(uint8_t k) {
  reportKey(k, true);
  return 1;
}

size_t USBHIDKeyboard::release(uint8_t k) {
  reportKey(k, false);
  return 1;
}

void USBHIDKeyboard::releaseAll() { sim::sendLine("{\"t\":\"sim.key\",\"all\":true,\"down\":false}"); }

size_t USBHIDKeyboard::print(const char *text) {
  JsonDocument doc;
  doc["t"] = "sim.type";
  doc["text"] = text;
  std::string line;
  serializeJson(doc, line);
  sim::sendLine(line.c_str());
  return strlen(text);
}

size_t USBHIDConsumerControl::press(uint16_t usage) {
  char line[64];
  snprintf(line, sizeof(line), "{\"t\":\"sim.media\",\"usage\":%u}", unsigned(usage));
  sim::sendLine(line);
  return 1;
}

// ---- the link: a TCP server standing in for USB CDC

namespace sim {
namespace {

SOCKET listener = INVALID_SOCKET;
SOCKET client = INVALID_SOCKET;
int port = 0;
std::string partial;   // bytes since the last newline
std::string incoming;  // whole lines for the firmware
size_t incoming_at = 0;

bool quit = false;
bool touch_down = false;
int16_t touch_x = 0, touch_y = 0;
bool light = false;
uint32_t frame_count = 0;

void dropClient() {
  if (client != INVALID_SOCKET) closesocket(client);
  client = INVALID_SOCKET;
  partial.clear();
  incoming.clear();
  incoming_at = 0;
  touch_down = false;
}

void sendRaw(const char *data, size_t size) {
  while (client != INVALID_SOCKET && size > 0) {
    const int n = send(client, data, int(size), 0);
    if (n > 0) {
      data += n;
      size -= size_t(n);
    } else if (WSAGetLastError() == WSAEWOULDBLOCK) {
      Sleep(1);
    } else {
      dropClient();
    }
  }
}

// The screen's objects, depth first: what a test needs to check a layout without reading
// pixels. Each: depth, class, absolute box, hidden, and a label's text.
void dumpObject(JsonArray out, lv_obj_t *obj, int depth) {
  JsonObject n = out.add<JsonObject>();
  n["d"] = depth;
  const lv_obj_class_t *cls = lv_obj_get_class(obj);
  n["c"] = cls == &lv_label_class ? "label" : cls == &lv_button_class ? "button" : cls == &lv_image_class ? "image"
          : cls == &lv_slider_class ? "slider" : cls == &lv_keyboard_class ? "keyboard" : cls == &lv_arc_class ? "arc" : "obj";
  lv_area_t a;
  lv_obj_get_coords(obj, &a);
  n["x"] = a.x1;
  n["y"] = a.y1;
  n["w"] = lv_area_get_width(&a);
  n["h"] = lv_area_get_height(&a);
  if (lv_obj_is_hidden(obj)) n["hidden"] = true;
  if (cls == &lv_label_class) n["text"] = lv_label_get_text(obj);
  for (uint32_t i = 0; i < lv_obj_get_child_count(obj); i++) dumpObject(out, lv_obj_get_child(obj, int32_t(i)), depth + 1);
}

// The simulator's own messages: touches, a ping to wait on, a dump of the screen, quit.
void handleSim(const std::string &line) {
  JsonDocument doc;
  if (deserializeJson(doc, line) != DeserializationError::Ok) return;
  const char *t = doc["t"] | "";
  if (!strcmp(t, "sim.touch")) {
    setTouch(doc["down"] | false, doc["x"] | 0, doc["y"] | 0);
  } else if (!strcmp(t, "sim.ping")) {
    char reply[96];
    snprintf(reply, sizeof(reply), "{\"t\":\"sim.pong\",\"id\":%d,\"frames\":%u,\"ms\":%u}", doc["id"] | 0,
             unsigned(frame_count), unsigned(millis()));
    sendLine(reply);
  } else if (!strcmp(t, "sim.page")) {
    // As the firmware's "page", but not a word from the companion: it doesn't count as the
    // PC being there, so an offline screen stays offline while a test looks at it.
    ui::idle::wake();
    ui::showPage(doc["i"] | 0);
  } else if (!strcmp(t, "sim.shot")) {
    // The firmware's "shot" reply (pc.cpp), likewise without counting as the PC.
    const auto *fb = static_cast<const uint8_t *>(display::frontBuffer());
    if (!fb) return;
    constexpr size_t kPart = 3072;
    constexpr size_t kSize = size_t(board::kWidth) * board::kHeight * 2;
    constexpr int kParts = int((kSize + kPart - 1) / kPart);
    static unsigned char b64[4 * kPart / 3 + 4];
    char head[96];
    for (int i = 0; i < kParts; i++) {
      const size_t at = size_t(i) * kPart;
      size_t len = 0;
      mbedtls_base64_encode(b64, sizeof(b64), &len, fb + at, std::min(kPart, kSize - at));
      const int n = snprintf(head, sizeof(head), "{\"t\":\"shot\",\"w\":%d,\"h\":%d,\"i\":%d,\"n\":%d,\"d\":\"",
                             board::kWidth, board::kHeight, i, kParts);
      sendRaw(head, size_t(n));
      sendRaw(reinterpret_cast<const char *>(b64), len);
      sendRaw("\"}\n", 3);
    }
  } else if (!strcmp(t, "sim.dump")) {
    lv_obj_update_layout(lv_screen_active());
    JsonDocument reply;
    reply["t"] = "sim.tree";
    reply["id"] = doc["id"] | 0;
    dumpObject(reply["screen"].to<JsonArray>(), lv_screen_active(), 0);
    dumpObject(reply["top"].to<JsonArray>(), lv_layer_top(), 0);
    std::string out;
    serializeJson(reply, out);
    sendLine(out.c_str());
  } else if (!strcmp(t, "sim.quit")) {
    quit = true;
  }
}

}  // namespace

void startLink(int p) {
  port = p;
  WSADATA wsa;
  WSAStartup(MAKEWORD(2, 2), &wsa);
  listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  BOOL reuse = TRUE;
  setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char *>(&reuse), sizeof(reuse));
  sockaddr_in addr = {};
  addr.sin_family = AF_INET;
  addr.sin_port = htons(uint16_t(port));
  inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);
  // Right after a restart the old process may still hold the port for a moment.
  for (int tries = 0; bind(listener, reinterpret_cast<sockaddr *>(&addr), sizeof(addr)) != 0; tries++) {
    if (tries == 50) {
      printf("sim: can't listen on port %d\n", port);
      exit(1);
    }
    Sleep(100);
  }
  listen(listener, 1);
  u_long nonblocking = 1;
  ioctlsocket(listener, FIONBIO, &nonblocking);
  printf("sim: link on 127.0.0.1:%d\n", port);
  fflush(stdout);
}

int linkPort() { return port; }

void closeLink() {
  dropClient();
  if (listener != INVALID_SOCKET) closesocket(listener);
  listener = INVALID_SOCKET;
}

void pollLink() {
  if (client == INVALID_SOCKET && listener != INVALID_SOCKET) {
    SOCKET s = accept(listener, nullptr, nullptr);
    if (s != INVALID_SOCKET) {
      u_long nonblocking = 1;
      ioctlsocket(s, FIONBIO, &nonblocking);
      BOOL nodelay = TRUE;
      setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char *>(&nodelay), sizeof(nodelay));
      client = s;
    }
  }
  if (client == INVALID_SOCKET) return;
  char buffer[16384];
  for (;;) {
    const int n = recv(client, buffer, sizeof(buffer), 0);
    if (n > 0) {
      partial.append(buffer, size_t(n));
      continue;
    }
    if (n == 0 || WSAGetLastError() != WSAEWOULDBLOCK) dropClient();
    break;
  }
  size_t start = 0;
  for (size_t nl; (nl = partial.find('\n', start)) != std::string::npos; start = nl + 1) {
    std::string line = partial.substr(start, nl - start);
    // {"t":"sim.…"} (with or without spaces): the simulator's own; anything else, the firmware's.
    if (line.substr(0, 24).find("\"sim.") != std::string::npos) handleSim(line);
    else incoming += line + '\n';
  }
  partial.erase(0, start);
}

void sendLine(const char *json) {
  sendRaw(json, strlen(json));
  sendRaw("\n", 1);
}

void setTouch(bool down, int x, int y) {
  touch_down = down;
  touch_x = int16_t(x < 0 ? 0 : x > 799 ? 799 : x);
  touch_y = int16_t(y < 0 ? 0 : y > 479 ? 479 : y);
}

bool touch(int16_t &x, int16_t &y) {
  x = touch_x;
  y = touch_y;
  return touch_down;
}

void setBacklight(bool on) { light = on; }
bool backlight() { return light; }
void framePresented() { frame_count++; }
uint32_t frames() { return frame_count; }
bool quitRequested() { return quit; }
void requestQuit() { quit = true; }

}  // namespace sim

// ---- USBCDC on the link

int USBCDC::available() {
  sim::pollLink();
  return int(sim::incoming.size() - sim::incoming_at);
}

size_t USBCDC::read(uint8_t *buffer, size_t size) {
  const size_t n = std::min(size, sim::incoming.size() - sim::incoming_at);
  memcpy(buffer, sim::incoming.data() + sim::incoming_at, n);
  sim::incoming_at += n;
  if (sim::incoming_at == sim::incoming.size()) {
    sim::incoming.clear();
    sim::incoming_at = 0;
  }
  return n;
}

size_t USBCDC::write(const uint8_t *buffer, size_t size) {
  sim::sendRaw(reinterpret_cast<const char *>(buffer), size);
  return size;
}

USBCDC::operator bool() const { return sim::client != INVALID_SOCKET; }

// ---- the firmware's PSRAM preference (psram_first.h): one kind of memory here

#include "psram_first.h"

namespace psram {
void preferForThisTask() {}
void pause() {}
void resume() {}
}  // namespace psram

// ---- the cores' load (load.h): the deck's FreeRTOS figures, which a PC doesn't have

#include "load.h"

namespace load {
void add(Part, uint32_t) {}
void touch(uint32_t) {}
void endPass() {}
void report() {}
}  // namespace load
