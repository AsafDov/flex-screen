// State the companion pushes to the deck. pc.cpp writes it; the UI redraws whatever
// changed, using each topic's version counter. Everything runs on the loop task.
#pragma once

#include <Arduino.h>

namespace model {

struct Media {
  bool present = false;  // a media session exists on the PC
  String title;
  String artist;
  String app;
  bool playing = false;
  float pos = 0;  // seconds, as of pos_at_ms
  float len = 0;  // seconds; 0 if unknown
  uint32_t pos_at_ms = 0;
  int8_t shuffle = -1;  // -1: the app has no shuffle, 0: off, 1: on
  String repeat;        // "none", "one", "all", or "" when the app has no repeat
  String art;           // id of this track's Art, or ""
  uint32_t version = 0;

  // Where playback is now, extrapolated from the last report.
  float positionNow() const;
};

struct Volume {
  int level = -1;  // 0-100, -1 until the companion reports it
  bool muted = false;
  uint32_t version = 0;
};

struct App {
  String id;  // process name, or "system"
  String name;
  int level = 0;
  bool muted = false;
};

struct Mixer {
  static constexpr int kMax = 5;  // faders next to Master
  App apps[kMax];
  int count = 0;
  uint32_t version = 0;
};

struct Device {
  String id;
  String name;    // "Speakers"
  String detail;  // "2- Realtek(R) Audio"
  bool selected = false;
};

struct Devices {
  static constexpr int kMax = 4;
  Device out[kMax];
  Device in[kMax];
  int out_count = 0;
  int in_count = 0;
  uint32_t version = 0;

  const Device *selectedOutput() const;
};

struct Mic {
  bool available = false;
  bool muted = false;
  uint32_t version = 0;
};

// Album art. pc.cpp assembles a new image into `pending`; the UI shows it, then frees the
// previous one once nothing draws from it.
struct Art {
  String pending_id;
  uint8_t *pending = nullptr;  // RGB565, PSRAM
  int w = 0;
  int h = 0;
  uint32_t version = 0;
};

struct ClaudeSession {
  String id;
  String project;
  String state;  // "working", "input" (waiting for an approval), "done", "idle"
  String detail;
  int32_t secs = 0;  // in this state for this long when the report arrived
};

struct Claude {
  static constexpr int kMax = 4;
  ClaudeSession sessions[kMax];  // most urgent first
  int count = 0;
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

struct Term {
  static constexpr int kLines = 8;
  String repo;
  String cmd;
  bool running = false;
  int32_t secs = -1;  // since the command started, as of at_ms; -1 before any
  bool has_exit = false;
  int exit = 0;
  uint32_t ms = 0;  // how long it took, once finished
  String lines[kLines];
  int line_count = 0;
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

struct Build {
  String state = "none";  // "none", "running", "passed", "failed"
  String cmd;
  String history;  // last results, oldest first: 'P' passed, 'F' failed
  uint32_t ms = 0;
  int32_t secs = -1;  // since it finished (or started), as of at_ms
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

// A command run in VS Code's terminal lately (the workspace that ran the newest one).
struct RecentCommand {
  String cmd;
  bool running = false;
  bool has_exit = false;  // the shell reported an exit code
  int exit = 0;
  uint32_t ms = 0;    // how long it took
  int32_t secs = -1;  // since it ended (started, while running), as of Recent::at_ms
};

struct Recent {
  static constexpr int kMax = 4;
  RecentCommand list[kMax];  // newest first, each command once
  int count = 0;
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

// The Today page's Recent card: the last few things done from the deck that can be done
// again (a window brought up, a note opened, a command run…), newest first, each once.
struct RecentAction {
  String id;
  String kind;   // window, note, command, meeting, mail, claude, device
  String name;   // the app, or what kind of thing it is
  String title;  // the window's title, the note, the command…
  String icon;   // a window's icon id, else ""
};

struct Actions {
  static constexpr int kMax = 5;
  RecentAction list[kMax];
  int count = 0;
  uint32_t version = 0;
};

// A tile in the shortcuts launcher; the companion runs it.
struct Shortcut {
  String id;
  String name;
  String icon;   // an app's or site's icon id, else ""
  String glyph;  // the symbol without an icon: mail, calendar, note, terminal, … or "" (the first letter)
};

struct Shortcuts {
  static constexpr int kMax = 12;  // three rows of four
  Shortcut list[kMax];
  int count = 0;
  uint32_t version = 0;
};

// The context bar: keys for the app in front, in its color; the companion runs them.
struct ContextKey {
  String id;
  String name;
  String glyph;  // an icon name from fonts/font_icons.h ("search"), or ""
};

struct Context {
  static constexpr int kMax = 8;
  String app;          // "Chrome"
  String icon;         // its window icon's id
  uint32_t color = 0;  // the bar's color, 0xRRGGBB
  ContextKey keys[kMax];
  int count = 0;       // 0: no bar
  uint32_t version = 0;
};

struct Git {
  String repo;
  String branch;
  String last;   // last commit's subject
  String busy;   // "fetch", "pull"… while one runs
  String error;  // "Not a git repository", …
  int ahead = 0;
  int behind = 0;
  int modified = 0;
  int added = 0;
  int deleted = 0;
  int untracked = 0;
  int32_t last_secs = -1;
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

struct Toast {
  String level;  // "ok", "error" or "attention"
  String text;
  String action;  // a deck action tapping it runs ("claude.open"), with `id`; "" dismisses
  String id;
  uint32_t version = 0;
};

// From the companion's settings.json.
struct Settings {
  uint32_t clock_after_s = 10 * 60;  // idle this long: the clock screen (0: never)
  uint32_t off_after_s = 30 * 60;    // idle this long: backlight off (0: never)
};

// A call in Teams, Zoom, Discord, Webex or a browser.
struct Call {
  String app;  // "teams", "zoom", "discord", "webex", "browser"; "" when there's none
  String name;
  int32_t secs = 0;  // into the call when reported
  uint32_t at_ms = 0;
  bool mic_muted = false;
  int camera = -1;  // 1 on, 0 off, -1 unknown
  int hand = -1;    // 1 raised, 0 not, -1 unknown
  bool deafened = false;
  bool can_mic = false, can_camera = false, can_hand = false, can_deafen = false, can_leave = false, can_react = false;
  String note;  // a setup hint
  int volume = -1;  // the app's volume in the mixer
  uint32_t version = 0;
};

struct Pc {
  bool locked = false;
  uint32_t idle_s = 0;  // since the last keyboard or mouse input, when reported
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

struct Window {
  String id;  // the window handle, as the companion sends it
  String app;
  String title;
  String icon;  // icons:: id, or ""
  bool active = false;
  bool minimized = false;  // still a tile; a tap brings it back
};

struct Windows {
  static constexpr int kMax = 8;  // the 3x3 grid, next to Show desktop
  Window list[kMax];
  int count = 0;
  uint32_t version = 0;
};

// A FancyZones zone, in the monitor's work-area pixels.
struct Zone {
  int x = 0, y = 0, w = 0, h = 0;
  String app;  // whose window fills it now, or ""
};

struct Monitor {
  static constexpr int kMaxZones = 12;
  String name;  // "DELL P2722H"
  bool primary = false;
  int w = 0, h = 0;  // work area
  String type;       // "priority-grid", "columns", "rows", "grid", "focus", "custom", …
  String layout;     // the applied layout's LayoutChoice::id
  int count = 0;
  Zone zones[kMaxZones];
  int zone_count = 0;
};

// A layout button: one of the user's custom FancyZones layouts, or a built-in one.
struct LayoutChoice {
  static constexpr int kMaxCells = 12;
  String id;  // the built-in type ("columns"), or the custom layout's uuid
  String name;
  bool custom = false;
  struct {
    int8_t x, y, w, h;
  } cells[kMaxCells];  // its zones in a 40x26 thumbnail
  int cell_count = 0;
};

struct Zones {
  static constexpr int kMax = 3;
  static constexpr int kMaxLayouts = 4;
  LayoutChoice layouts[kMaxLayouts];  // custom layouts first
  int layout_count = 0;
  Monitor mons[kMax];  // left to right
  int mon_count = 0;
  int active = 0;  // the monitor the foreground window is on
  String app;      // the foreground window's app: what a zone tap snaps
  uint32_t version = 0;
};

// The Corne (any VIA/Vial keyboard): which layer is active.
struct Keyboard {
  bool present = false;
  String name;
  String label;  // "BASE", "LOWER", …
  int layer = 0;
  bool held = false;        // the layer is on while a key is held (not toggled)
  uint32_t held_at_ms = 0;  // when that key went down, in millis()
  int key = -1;             // that key, in Keymap::keys
  uint32_t version = 0;
};

// The layer cheat sheet, from a Vial keyboard: where its keys are and each layer's labels.
struct Keymap {
  static constexpr int kMaxKeys = 64;
  static constexpr int kMaxLayers = 8;
  struct Key {
    int16_t x, y, w, h;  // hundredths of a key from the keyboard's top-left; x, y the centre
  } keys[kMaxKeys];
  int count = 0;
  struct Layer {
    int index = -1;
    String name;
    String labels;  // one per key, tab-separated: "top\nmain\nbottom", or just "main"
    String dim;     // '1' per key the layer leaves to the base layer
  } layers[kMaxLayers];
  int layer_count = 0;
  uint32_t version = 0;

  const Layer *find(int index) const {
    for (int i = 0; i < layer_count; i++) {
      if (layers[i].index == index) return &layers[i];
    }
    return nullptr;
  }
};

struct Clip {
  String id;
  String text;  // one line, shortened
  String kind;  // "link", "code", "color", "command", "text"
  bool pinned = false;
  int32_t secs = 0;  // its age when reported
};

struct Clips {
  static constexpr int kMax = 6;
  Clip list[kMax];  // pins first, then the newest
  int count = 0;
  uint32_t at_ms = 0;
  uint32_t version = 0;
};

struct Note {
  String name;
  String path;  // in the vault
};

struct Obsidian {
  static constexpr int kMaxRecent = 5;
  bool found = false;
  String vault;
  Note recent[kMaxRecent];
  int recent_count = 0;
  uint32_t version = 0;
};

struct Event {
  String id;
  String title;
  int64_t start = 0, end = 0;  // UTC seconds
  bool all_day = false;
  bool join = false;  // has a Meet/Zoom/Teams link
};

struct MailTask {
  String list, id;
  String title;
  String from;
  String due;  // "2026-11-07", or ""
  bool done = false;
};

struct Google {
  static constexpr int kMaxEvents = 8;
  static constexpr int kMaxTasks = 6;
  String status;  // "ok", "setup" (no OAuth client yet), "signin", "error"
  Event events[kMaxEvents];
  int event_count = 0;
  MailTask tasks[kMaxTasks];
  int task_count = 0;
  int open = 0;
  uint32_t version = 0;

  // The first timed event that hasn't ended, or null.
  const Event *next(int64_t now) const;
};

struct Clock {
  int64_t epoch = 0;  // UTC seconds at synced_at_ms; 0 before the first sync
  int32_t tz_offset_min = 0;
  uint32_t synced_at_ms = 0;
};

extern Media media;
extern Volume volume;
extern Mixer mixer;
extern Devices devices;
extern Mic mic;
extern Art art;
extern Claude claude;
extern Term term;
extern Build build;
extern Recent recent;
extern Actions actions;
extern Shortcuts shortcuts;
extern Context context;
extern Git git;
extern Toast toast;
extern Settings settings;
extern Pc pc;
extern Call call;
extern Windows windows;
extern Keyboard keyboard;
extern Keymap keymap;
extern Clips clips;
extern Obsidian obsidian;
extern Google google;
extern uint32_t bench_requests;  // the companion asked to time a full redraw of every page
extern Zones zones;
extern Clock clock;

// Seconds since something happened, given its age when reported at `at_ms` (-1 stays -1).
int32_t agedSecs(int32_t secs, uint32_t at_ms);

// Local wall-clock seconds since the epoch, or 0 before the companion sends the time.
int64_t localNow();
// UTC seconds, or 0 before the companion sends the time.
int64_t utcNow();

}  // namespace model
