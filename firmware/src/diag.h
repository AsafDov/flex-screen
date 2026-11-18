// Deck diagnostics: the firmware, how long the deck has been up, its free memory, the
// frame rate and what the link with the PC has been through (lines, errors, drops). An
// overlay over the pages, not a page of its own; holding the status bar's clock opens it.
// It needs nothing from the PC, so it works offline too, which is when it's most wanted.
#pragma once

namespace ui::diag {

void build();   // on LVGL's top layer; starts counting frames
void open();
void update();  // call every loop: keeps the counts even while it's closed

}  // namespace ui::diag
