// The context bar's key icons: FontAwesome glyphs at 15 px, by name
// (scripts/gen_font_icons.py makes font_icons.c).
#pragma once

#include "lvgl.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
  const char *name;  // "search", "split-right", …
  const char *text;  // the glyph, as UTF-8
} font_icon_t;

extern const lv_font_t font_icons_15;
extern const font_icon_t font_icon_names[];  // sorted by name
extern const int font_icon_count;

#ifdef __cplusplus
}
#endif
