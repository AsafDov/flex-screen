// Capture: tasks (page_tasks), the clipboard stack and Obsidian (today's note, quick
// capture, clipboard to Inbox, recent notes).
#pragma once

#include <lvgl.h>

namespace ui::capture {

void build(lv_obj_t *page);
void update();  // call every loop

}  // namespace ui::capture
