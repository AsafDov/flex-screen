// Panels: up to four lists the companion fills from the profile's "panels" (a web address's
// JSON, or a command on the PC: parcels, PRs to review, a build's status, anything), drawn
// the same way whatever they hold: rows of a line, a second line and a badge; a tap opens
// one that has a link on the PC. On the deck only when the profile has panels.
#pragma once

#include <lvgl.h>

namespace ui::panels {

void build(lv_obj_t *page);
void update();  // call every loop

}  // namespace ui::panels
