// The context bar: a strip above the tab bar, on every page but the Numpad, with keys for
// the app in front on the PC (VS Code, Chrome, Terminal…) in that app's color. A key goes
// to the companion, which presses its keys in that app's window. While there's no bar
// (offline, nothing to show, the Numpad page) the pages get its room back.
#pragma once

#include <lvgl.h>

namespace ui::ctxbar {

void build(lv_obj_t *screen, lv_obj_t *pages);  // pages: the tabview's content, which makes room
void update();                                  // call every loop

}  // namespace ui::ctxbar
