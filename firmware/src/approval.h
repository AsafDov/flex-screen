// Claude asking for permission: a banner under the status bar, on every page, with what it
// wants to run and Deny, Always (when Claude suggests a "don't ask again" rule) and
// Approve. Claude's own prompt shows on the PC at the same time; whichever is answered first
// wins, and the banner goes when the request does.
#pragma once

namespace ui::approval {

void build();   // on LVGL's top layer, after the pages
void update();  // call every loop

}  // namespace ui::approval
