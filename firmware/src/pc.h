// The deck's USB device and its line protocol with the companion app on the PC
// (protocol/v0.md).
//
// The native port is a composite device: a CDC serial link carrying newline-delimited
// JSON, plus an HID keyboard and media keys. Controls go through the companion when it's
// connected and fall back to plain media keys when it isn't.
#pragma once

#include <stdint.h>

namespace pc {

constexpr const char *kFirmwareVersion = "0.24.2";

void begin();  // starts USB; call first in setup()
void poll();   // reads incoming lines; call every loop

// True while the companion app has the port open and has spoken in the last few seconds.
bool connected();

enum class MediaCmd { Toggle, Next, Prev };
void media(MediaCmd cmd);
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

// Characters, typed by the companion as Unicode (so "(" doesn't depend on the keyboard
// layout); without it, as US-layout keys.
void typeText(const char *text);

// printf-style log line, sent to the companion and the UART port.
void log(const char *fmt, ...) __attribute__((format(printf, 1, 2)));

}  // namespace pc
