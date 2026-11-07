// The deck's USB device and its line protocol with the companion app on the PC
// (protocol/v0.md).
//
// The native port is a composite device: a CDC serial link carrying newline-delimited
// JSON, plus an HID keyboard and media keys. Controls go through the companion when it's
// connected and fall back to plain media keys when it isn't.
#pragma once

#include <stdint.h>

namespace pc {

constexpr const char *kFirmwareVersion = "0.10.1";

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
void replayCommand(const char *cmd);  // again, in the VS Code terminal it ran in last
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
void setTaskDone(const char *list, const char *id, bool done);  // in Google Tasks
void joinMeeting(const char *id);  // opens the event's Meet/Zoom/Teams link
void openMail();

// Characters, typed by the companion as Unicode (so "(" doesn't depend on the keyboard
// layout); without it, as US-layout keys.
void typeText(const char *text);

// printf-style log line, sent to the companion and the UART port.
void log(const char *fmt, ...) __attribute__((format(printf, 1, 2)));

}  // namespace pc
