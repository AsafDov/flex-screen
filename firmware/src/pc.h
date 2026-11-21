// The deck's USB device and its line protocol with the companion app on the PC
// (protocol/v0.md).
//
// The native port is a composite device: a CDC serial link carrying newline-delimited
// JSON, plus an HID keyboard and media keys. Controls go through the companion when it's
// connected and fall back to plain media keys when it isn't.
#pragma once

#include <stdint.h>

namespace pc {

constexpr const char *kFirmwareVersion = "0.31.2";

void begin();  // starts USB; call first in setup()
void poll();   // reads incoming lines; call every loop

// True while the companion app has the port open and has spoken in the last few seconds,
// and while the demo plays (see demoMode()).
bool connected();

// The demo (ui::demo). While it's on, connected() says yes even offline, nothing goes to the
// PC, and the PC's state lines and toasts are held back, the newest of each topic, instead
// of shown. Turned off, those are applied. play() takes a made-up line as if the companion
// had sent it.
void demoMode(bool on);
void play(const char *line);

enum class MediaCmd { Toggle, Next, Prev };
void media(MediaCmd cmd);
void seek(float seconds);  // to that point of the track, when the app allows it
void toggleShuffle();
void cycleRepeat();

void setVolume(int level);  // companion only: there's no HID key for an absolute level
void toggleMute();
void setAppVolume(const char *app, int level);
void toggleAppMute(const char *app);
void selectOutput(const char *device);
void selectInput(const char *device);
void toggleMic();

void openClaude(const char *session);  // brings its window (VS Code, Claude) to the front
void stopClaude(const char *session);  // ends its turn before its next step
void replayCommand(const char *cmd);  // again, in the VS Code terminal it ran in last
void runRecent(const char *id);       // a Recent tile on Today: the same action again
void runShortcut(const char *id);     // a launcher tile: opens the app, link, … it stands for
void runContextKey(const char *id);   // a context bar key: into the app in front
// The context bar's layout for `app` (model::context.key): action `id` into place `slot`
// (count: a new key at the end; one already on the bar swaps places), a key off the bar,
// or all back to the app's defaults. The companion keeps it and sends the bar again.
void contextSet(const char *app, int slot, const char *id);
void contextRemove(const char *app, int slot);
void contextReset(const char *app);
// Holds the bar on the app it shows (on), or lets it follow the app in front again.
void contextLock(bool on);
void contextPick(const char *key, const char *choice);  // from a key's list (model::picker)

// Answers a Claude permission request (model::approval). `always` also applies Claude's own
// "don't ask again" suggestion.
void approveClaude(const char *id, bool always);
void denyClaude(const char *id);
// Answers Claude's question (model::question) with the options picked; none dismisses it
// (or, for a note, marks it read).
void answerClaude(const char *id, const int *picks, int count);
void interruptCommand();               // Ctrl+C
void focusTerminal();
void git(const char *operation);       // "fetch", "pull", "push", "stash", "commit"

void runAction(const char *action, const char *id);  // one the companion named (a toast's)
void lockPc();
void callReact(const char *type);  // Teams: "like", "love", "applause", "wow", "laugh"
void callVolume(int level);        // the call app's volume
void focusWindow(const char *id);
void closeWindow(const char *id);      // asks it to close, like its X button
void showDesktop();                    // Win+D, over HID: works without the companion
void requestIcon(const char *id);      // one the deck doesn't have (any more)
void snapWindow(int monitor, int zone);  // the foreground window, into a FancyZones zone
void snapWindowTo(const char *id, int monitor, int zone);  // that window (the desktop map's drag)
// That window onto a monitor, its top-left at (x, y) of the work area, its size kept.
void moveWindow(const char *id, int monitor, int x, int y);
// A LayoutChoice::id; the built-in layout already applied steps to its next zone count.
void setLayout(int monitor, const char *id);

// The deck as a USB keyboard, for the numpad; works without the companion. Keys are
// USBHIDKeyboard.h codes (KEY_KP_1, KEY_HOME, ...).
void pressKey(uint8_t key);
void releaseKey(uint8_t key);
void tapKey(uint8_t key);
void releaseAllKeys();
bool numLockKnown();  // the PC has reported its keyboard lights
bool numLock();
void pasteClip(const char *id);  // onto the clipboard, then Ctrl+V into the focused window
void pinClip(const char *id);    // pins or unpins
void clearClips();               // all but the pins
void obsidian(const char *what);  // "today": the daily note
void openNote(const char *path);
// Tasks (model::tasks), by source ("google"), list and id: ticked off or back on; a new
// one (`list` "" for the source's default, `due` "yyyy-MM-dd" or ""); read again now.
void setTaskDone(const char *src, const char *list, const char *id, bool done);
void addTask(const char *src, const char *list, const char *title, const char *due);
void refreshTasks();
// Asks for a task's whole title and notes (model::task_detail).
void openTask(const char *src, const char *list, const char *id);
// Opens the task where it lives, on the PC (Jira's page, the note in Obsidian…).
void openTaskLink(const char *src, const char *list, const char *id);
void joinMeeting(const char *id);  // opens the event's Meet/Zoom/Teams link
void openVial();                   // Vial's web app (vial.rocks) on the PC, to change the keymap

// Characters, typed by the companion as Unicode (so "(" doesn't depend on the keyboard
// layout); without it, as US-layout keys.
void typeText(const char *text);

// printf-style log line, sent to the companion and the UART port.
void log(const char *fmt, ...) __attribute__((format(printf, 1, 2)));

// Notes that a part of the start is done (`what` is a string literal): the hello says how
// long each took.
void bootMark(const char *what);
// A figure from the start for the hello (an object count, how long something took).
void bootValue(const char *what, uint32_t value);

// What the link has been through since the deck started, for the diagnostics screen.
struct LinkStats {
  uint32_t lines = 0;       // lines taken in
  uint32_t bad_lines = 0;   // lines that weren't JSON (garbled, or bytes missing)
  uint32_t long_lines = 0;  // lines past the 8 KB the deck takes, dropped
  uint32_t lost_bytes = 0;  // bytes a full receive buffer had no room for
  uint32_t bad_parts = 0;   // album art and icon parts that didn't decode
  uint32_t drops = 0;       // times the companion went quiet or let go of the port
  uint32_t up_ms = 0;       // millis() when the link last came up; 0 if it never has
  uint32_t down_ms = 0;     // millis() when it last went down; 0 if it hasn't
  uint32_t error_ms = 0;    // millis() at the last error; 0 if there's been none
  char error[64] = "";      // what it was: "bad line (812 bytes)"
};
const LinkStats &linkStats();

// Lines taken in since the last call, and the time spent parsing and applying them, for the
// benchmark (a screenshot's line includes sending it: a couple of seconds).
struct LineTiming {
  uint32_t lines = 0;
  uint64_t us = 0;
  uint32_t worst_us = 0;
};
LineTiming takeLineTiming();

// Why the deck last restarted: "power-on", "usb", "panic", "task watchdog", …
const char *resetReason();

}  // namespace pc
