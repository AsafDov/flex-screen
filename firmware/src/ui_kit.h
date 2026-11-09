// Shared look and building blocks for the deck's screens: the design canvas's palette,
// fonts, small widget helpers, album art and a drawn microphone icon.
#pragma once

#include <Arduino.h>
#include <lvgl.h>

namespace ui {

// The palette. "base" is the design canvas's; "vibrant" is bluer and more saturated;
// "ember" is warm with an orange accent; "violet" is indigo with a violet accent.
// Pages read these when they build, so a change takes a restart (the deck saves the
// choice and restarts when the companion's settings name another one).
extern uint32_t kBg, kSurface, kBorder, kRaised, kSelected, kText, kSoftText, kMuted, kTrack;
extern uint32_t kAccent, kOnAccent, kGreen, kAmber, kAmberBg, kArtBg;
// A card that stands out in the accent's hue (Up next), its border, and soft text on it.
extern uint32_t kHighlight, kHighlightLine, kHighlightText;

// Sets the colors above; unknown names give "base".
void usePalette(const char *name);

constexpr int kBarH = 44;  // status bar and page tabs
constexpr int kGap = 10;

// Montserrat plus accented Latin, Cyrillic and punctuation (·, …, dashes), with DejaVu as the
// last fallback for Hebrew and Arabic. Use these for any text that comes from the PC.
extern lv_font_t title_font;  // 20 px
extern lv_font_t body_font;   // 16 px
extern lv_font_t small_font;  // 14 px

void initFonts();

lv_color_t hex(uint32_t c);
lv_obj_t *box(lv_obj_t *parent);  // unstyled container that doesn't scroll or take presses
lv_obj_t *card(lv_obj_t *parent, uint32_t bg = kSurface, uint32_t border = kBorder);
lv_obj_t *label(lv_obj_t *parent, const char *text, const lv_font_t *font, uint32_t color);
void flexCol(lv_obj_t *o, int gap);
void flexRow(lv_obj_t *o, int gap);
lv_obj_t *pill(lv_obj_t *parent);
// A round button; a kSurface background means transparent until pressed.
lv_obj_t *button(lv_obj_t *parent, int w, int h, uint32_t bg, lv_event_cb_t cb, void *user = nullptr);
// A button holding one centered symbol; returns the symbol's label.
lv_obj_t *iconButton(lv_obj_t *parent, int w, int h, uint32_t bg, const char *symbol, uint32_t color,
                     lv_event_cb_t cb, void *user = nullptr);
void ellipsize(lv_obj_t *label);  // one line, full width, "…" when too long
// Setters that skip unchanged values. Every set redraws the object, and pages refresh
// once a second for their "3 min ago" texts; redrawing whole cards for nothing kept the
// loop busy and touches waited.
void setText(lv_obj_t *label, const char *text);
// A label taking the rest of a flex row, one line, "…" when too long. (A label left
// content-sized never shortens its text, so it ran out of its row.) Anything else just
// takes the rest of the row.
void fillRow(lv_obj_t *label);
// Sets a label's text and makes it as wide as the text, up to `max`, then "…".
void setTextUpTo(lv_obj_t *label, const char *text, int32_t max);
void setTextf(lv_obj_t *label, const char *fmt, ...) __attribute__((format(printf, 2, 3)));
void setTextColor(lv_obj_t *o, uint32_t color);
void setBgColor(lv_obj_t *o, uint32_t color);
void setBorderColor(lv_obj_t *o, uint32_t color);
void setHidden(lv_obj_t *o, bool hidden);

// "3:07", or "1:02:07" past an hour.
void formatTime(char *buf, size_t n, float seconds);
// "just now", "42 s ago", "3 min ago", "2 h ago", "4 d ago"; "" for a negative age.
void formatAgo(char *buf, size_t n, int32_t secs);
// "42 s", "3 min 12 s".
void formatDuration(char *buf, size_t n, uint32_t ms);

// Album art: the shell installs each new image from model::art, the pages show it.
// Returns the image for this art id, or null if it isn't (or isn't yet) on the deck.
const lv_image_dsc_t *artFor(const String &id);
// Takes model::art's pending image. Call releaseOldArt() once pages have switched to it.
bool installPendingArt();
void releaseOldArt();

// A microphone drawn from shapes (LVGL's symbol font has none), 18x20 px.
struct MicIcon {
  lv_obj_t *root;
  lv_obj_t *capsule;
  lv_obj_t *arc;
  lv_obj_t *stem;
  lv_obj_t *slash;
};
MicIcon micIcon(lv_obj_t *parent);
void setMicIcon(const MicIcon &icon, uint32_t color, bool slashed);

}  // namespace ui
