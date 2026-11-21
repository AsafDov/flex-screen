# The deck's firmware UI built for Windows: LVGL and ArduinoJson from the firmware's
# PlatformIO libdeps, the firmware's own UI and link sources unchanged, and the simulator's
# stand-ins for the board, display, USB and Arduino core. Built by build.ps1 (MSVC + Ninja).
cmake_minimum_required(VERSION 3.20)
project(flex_screen_sim C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(FW ${CMAKE_CURRENT_SOURCE_DIR}/../firmware)
set(LIBDEPS ${FW}/.pio/libdeps/deck)
set(SIM ${CMAKE_CURRENT_SOURCE_DIR})

if(NOT EXISTS ${LIBDEPS}/lvgl/lvgl.h)
  message(FATAL_ERROR "LVGL isn't in ${LIBDEPS}: build the firmware once (pio run -d firmware) to fetch it.")
endif()

# Source files are UTF-8 (·, …, Hebrew in fonts); every file gets sim_compat.h first.
set(COMMON_OPTIONS /utf-8 /FI${SIM}/sim_compat.h)

file(GLOB_RECURSE LVGL_SOURCES CONFIGURE_DEPENDS ${LIBDEPS}/lvgl/src/*.c)
add_library(lvgl STATIC ${LVGL_SOURCES})
target_include_directories(lvgl PUBLIC ${SIM} ${LIBDEPS}/lvgl)
target_compile_definitions(lvgl PUBLIC LV_CONF_INCLUDE_SIMPLE)
target_compile_options(lvgl PRIVATE ${COMMON_OPTIONS} /W0)

set(FIRMWARE_SOURCES approval ask call cheat ctxbar dayview demo diag glow icons idle launcher model page_audio page_capture page_dev page_numpad page_tasks page_today sessions
    page_windows pc ui ui_kit)
list(TRANSFORM FIRMWARE_SOURCES PREPEND ${FW}/src/)
list(TRANSFORM FIRMWARE_SOURCES APPEND .cpp)
# The tiles (firmware/src/tiles.h): every file under tiles/, as the firmware builds them.
file(GLOB TILE_SOURCES CONFIGURE_DEPENDS ${FW}/src/tiles/*.cpp)
list(APPEND FIRMWARE_SOURCES ${FW}/src/tiles.cpp ${TILE_SOURCES})

add_executable(flex-screen-sim
  ${FIRMWARE_SOURCES}
  ${FW}/src/fonts/font_extra.c
  ${FW}/src/fonts/font_icons.c
  sim_main.cpp sim_runtime.cpp sim_board.cpp sim_display.cpp)
# The stand-ins come first, so <Arduino.h> and friends are the simulator's.
target_include_directories(flex-screen-sim PRIVATE ${SIM}/shim ${SIM} ${FW}/src ${LIBDEPS}/ArduinoJson/src)
target_compile_options(flex-screen-sim PRIVATE ${COMMON_OPTIONS} /EHsc /W3 /wd4244 /wd4267 /wd4305 /wd4996 /wd4201)
# The firmware deserializes from and assigns to Arduino Strings; ArduinoJson only knows them
# on Arduino unless told (it then includes <Arduino.h>: the stand-in).
target_compile_definitions(flex-screen-sim PRIVATE ARDUINOJSON_ENABLE_ARDUINO_STRING=1)
target_link_libraries(flex-screen-sim PRIVATE lvgl ws2_32 user32 gdi32 dbghelp)

# A map file and symbols, for finding where a crash happened (build/flex-screen-sim.map).
target_link_options(flex-screen-sim PRIVATE /MAP /DEBUG)
target_compile_options(flex-screen-sim PRIVATE /Zi)
target_compile_options(lvgl PRIVATE /Zi)
