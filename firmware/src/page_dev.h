// Dev: Claude Code status, git, build results and the VS Code terminal.
#pragma once

#include <lvgl.h>

namespace ui::dev {

void build(lv_obj_t *page);
void update();  // call every loop

}  // namespace ui::dev
