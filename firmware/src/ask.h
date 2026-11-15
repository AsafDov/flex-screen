// Claude asking you something, or leaving you a note: a card under the status bar, on every
// page (under the approval banner when both show).
//
// A question has 2-4 options: one tap answers it, or, for one that takes several, tap them
// and Done. It comes from Claude's deck_ask tool, or from Claude Code's own question, whose
// prompt shows on the PC at the same time; whichever is answered first wins. The X leaves it
// to the PC (deck_ask hears that it was dismissed).
//
// A note (deck_notify) stays until OK; Open brings up the session's window.
#pragma once

namespace ui::ask {

void build();   // on LVGL's top layer, after the approval banner
void update();  // call every loop

}  // namespace ui::ask
