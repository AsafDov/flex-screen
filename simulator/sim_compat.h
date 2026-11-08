// Forced into every file of the simulator build (/FI): what the firmware gets from GCC
// and newlib on the ESP32 but MSVC doesn't have.
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <time.h>

// printf-format checking on GCC; nothing here.
#define __attribute__(x)

// newlib's reentrant gmtime (MSVC's gmtime_s takes its arguments the other way round).
static inline struct tm *gmtime_r(const time_t *t, struct tm *out) { return gmtime_s(out, t) == 0 ? out : 0; }
