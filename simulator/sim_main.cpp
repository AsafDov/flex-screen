// flex-screen deck simulator: the deck's firmware UI on the PC.
//
//   flex-screen-sim.exe [--port 48160] [--headless] [--scale 1]
//
// Shows the 800x480 screen in a window (the mouse is the finger) and listens on
// 127.0.0.1:<port> for the companion's side of the link: the same newline-delimited JSON
// as over USB (protocol/v0.md), plus the simulator's own "sim.*" lines (see README.md).
// --headless skips the window, for tests.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <Arduino.h>
#include <lvgl.h>

#include <vector>

#include "board.h"
#include "display.h"
#include "pc.h"
#include "sim.h"
#include "ui.h"

namespace {

HWND window = nullptr;
int scale = 1;
std::vector<uint32_t> pixels(size_t(board::kWidth) * board::kHeight);
uint32_t painted = UINT32_MAX;

// The frame on show, RGB565 to 32-bit BGRA (black while the backlight is off).
void convert() {
  const auto *fb = static_cast<const uint16_t *>(display::frontBuffer());
  if (!fb || !sim::backlight()) {
    std::fill(pixels.begin(), pixels.end(), 0u);
    return;
  }
  for (size_t i = 0; i < pixels.size(); i++) {
    const uint16_t p = fb[i];
    const uint32_t r = (p >> 11) & 0x1F, g = (p >> 5) & 0x3F, b = p & 0x1F;
    pixels[i] = ((r << 3 | r >> 2) << 16) | ((g << 2 | g >> 4) << 8) | (b << 3 | b >> 2);
  }
}

void mouse(LPARAM lp, bool down) {
  const int x = short(LOWORD(lp)), y = short(HIWORD(lp));
  sim::setTouch(down, x / scale, y / scale);
}

LRESULT CALLBACK windowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
  switch (msg) {
    case WM_LBUTTONDOWN:
      SetCapture(hwnd);
      mouse(lp, true);
      return 0;
    case WM_MOUSEMOVE:
      if (wp & MK_LBUTTON) mouse(lp, true);
      return 0;
    case WM_LBUTTONUP:
      ReleaseCapture();
      mouse(lp, false);
      return 0;
    case WM_PAINT: {
      PAINTSTRUCT ps;
      HDC dc = BeginPaint(hwnd, &ps);
      convert();
      BITMAPINFO bi = {};
      bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
      bi.bmiHeader.biWidth = board::kWidth;
      bi.bmiHeader.biHeight = -board::kHeight;  // top-down
      bi.bmiHeader.biPlanes = 1;
      bi.bmiHeader.biBitCount = 32;
      bi.bmiHeader.biCompression = BI_RGB;
      SetStretchBltMode(dc, COLORONCOLOR);
      StretchDIBits(dc, 0, 0, board::kWidth * scale, board::kHeight * scale, 0, 0, board::kWidth, board::kHeight,
                    pixels.data(), &bi, DIB_RGB_COLORS, SRCCOPY);
      EndPaint(hwnd, &ps);
      return 0;
    }
    case WM_ERASEBKGND:
      return 1;
    case WM_DESTROY:
      sim::requestQuit();
      PostQuitMessage(0);
      return 0;
  }
  return DefWindowProcW(hwnd, msg, wp, lp);
}

void openWindow() {
  WNDCLASSW wc = {};
  wc.lpfnWndProc = windowProc;
  wc.hInstance = GetModuleHandleW(nullptr);
  wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
  wc.lpszClassName = L"FlexScreenSim";
  RegisterClassW(&wc);
  const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
  RECT r = {0, 0, board::kWidth * scale, board::kHeight * scale};
  AdjustWindowRect(&r, style, FALSE);
  wchar_t title[96];
  swprintf(title, 96, L"flex-screen deck (simulator) - link on port %d", sim::linkPort());
  window = CreateWindowW(wc.lpszClassName, title, style | WS_VISIBLE, CW_USEDEFAULT, CW_USEDEFAULT, r.right - r.left,
                         r.bottom - r.top, nullptr, nullptr, wc.hInstance, nullptr);
}

}  // namespace

int main(int argc, char **argv) {
  int port = 48160;
  bool headless = false;
  for (int i = 1; i < argc; i++) {
    if (!strcmp(argv[i], "--port") && i + 1 < argc) port = atoi(argv[++i]);
    else if (!strcmp(argv[i], "--headless")) headless = true;
    else if (!strcmp(argv[i], "--scale") && i + 1 < argc) scale = std::max(1, atoi(argv[++i]));
  }
  SetProcessDPIAware();  // one screen pixel per deck pixel, not a blurry stretch
  // The deck's C library runs in UTC (the companion's "time" carries the offset); mktime()
  // in this process would otherwise use the PC's zone and shift due dates by a day.
  _putenv("TZ=UTC0");
  _tzset();
  sim::startLink(port);

  // As main.cpp's setup().
  pc::begin();
  board::begin();
  display::begin();
  ui::begin();
  lv_refr_now(nullptr);
  board::backlight(true);
  if (!headless) openWindow();
  printf("sim: deck ready\n");
  fflush(stdout);

  // As main.cpp's loop(), with the window's messages in between.
  while (!sim::quitRequested()) {
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
      TranslateMessage(&msg);
      DispatchMessageW(&msg);
    }
    pc::poll();
    ui::update();
    const uint32_t idle = lv_timer_handler();
    // A new frame, or the backlight going on or off (the idle screen), repaints the window.
    const uint32_t shown = sim::frames() * 2 + (sim::backlight() ? 1 : 0);
    if (window && shown != painted) {
      painted = shown;
      InvalidateRect(window, nullptr, FALSE);
    }
    Sleep(idle < 5 ? idle : 5);
  }
  sim::closeLink();
  return 0;
}
