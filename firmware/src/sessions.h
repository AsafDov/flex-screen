// The Claude session board: every Claude session on the PC (the Claude app, VS Code, terminals,
// Remote-SSH) in one list, the ones that need you first, beside the plan's 5-hour usage as a
// burn-down with when it runs out at the recent pace. Over the pages; the status bar's Claude
// pill and the Dev page's Claude banner open it.
//
// A row brings up its session's window. A working session's Stop, held, ends its turn
// before its next step (a tap only says "Hold").
#pragma once

namespace ui::sessions {

void build();  // on LVGL's top layer
void open();
void update();  // call every loop

}  // namespace ui::sessions
