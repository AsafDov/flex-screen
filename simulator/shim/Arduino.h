// The parts of the Arduino core the deck's UI and link code use, for the PC simulator.
#pragma once

#include <ctype.h>
#include <math.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <algorithm>
#include <string>
#include <type_traits>

uint32_t millis();
uint32_t micros();
void delay(uint32_t ms);
inline uint32_t getCpuFrequencyMhz() { return 240; }  // the deck's, for the benchmark's report

template <typename A, typename B>
inline auto min(A a, B b) -> typename std::common_type<A, B>::type {
  return a < b ? a : b;
}
template <typename A, typename B>
inline auto max(A a, B b) -> typename std::common_type<A, B>::type {
  return a > b ? a : b;
}
template <typename T, typename L, typename H>
inline T constrain(T v, L lo, H hi) {
  return v < lo ? T(lo) : v > hi ? T(hi) : v;
}

// Arduino's String, on std::string.
class String {
 public:
  String() = default;
  String(const char *c) : s_(c ? c : "") {}
  String(const std::string &s) : s_(s) {}
  explicit String(char c) : s_(1, c) {}
  explicit String(int v) : s_(std::to_string(v)) {}
  explicit String(unsigned v) : s_(std::to_string(v)) {}
  explicit String(long v) : s_(std::to_string(v)) {}

  size_t length() const { return s_.size(); }
  bool isEmpty() const { return s_.empty(); }
  const char *c_str() const { return s_.c_str(); }
  bool reserve(size_t n) {
    s_.reserve(n);
    return true;
  }

  char operator[](size_t i) const { return i < s_.size() ? s_[i] : 0; }
  char &operator[](size_t i) { return s_[i]; }
  char charAt(size_t i) const { return (*this)[i]; }

  String &operator+=(const String &o) {
    s_ += o.s_;
    return *this;
  }
  String &operator+=(const char *c) {
    if (c) s_ += c;
    return *this;
  }
  String &operator+=(char c) {
    s_ += c;
    return *this;
  }
  bool concat(const char *c) {
    *this += c;
    return true;
  }
  bool concat(const char *c, size_t n) {
    s_.append(c, n);
    return true;
  }
  bool concat(char c) {
    s_ += c;
    return true;
  }

  friend String operator+(const String &a, const String &b) { return String(a.s_ + b.s_); }
  friend String operator+(const String &a, const char *b) { return String(a.s_ + (b ? b : "")); }
  friend String operator+(const char *a, const String &b) { return String(std::string(a ? a : "") + b.s_); }
  friend String operator+(const String &a, char b) { return String(a.s_ + b); }

  bool operator==(const String &o) const { return s_ == o.s_; }
  bool operator==(const char *c) const { return s_ == (c ? c : ""); }
  bool operator!=(const String &o) const { return s_ != o.s_; }
  bool operator!=(const char *c) const { return !(*this == c); }
  bool operator<(const String &o) const { return s_ < o.s_; }
  friend bool operator==(const char *c, const String &s) { return s == c; }
  friend bool operator!=(const char *c, const String &s) { return s != c; }
  bool equals(const String &o) const { return s_ == o.s_; }

  int indexOf(char c, size_t from = 0) const {
    const size_t i = s_.find(c, from);
    return i == std::string::npos ? -1 : int(i);
  }
  int indexOf(const char *c, size_t from = 0) const {
    const size_t i = s_.find(c, from);
    return i == std::string::npos ? -1 : int(i);
  }
  String substring(size_t from) const { return from >= s_.size() ? String() : String(s_.substr(from)); }
  String substring(size_t from, size_t to) const {
    if (from > to) std::swap(from, to);
    return from >= s_.size() ? String() : String(s_.substr(from, to - from));
  }
  bool startsWith(const String &p) const { return s_.rfind(p.s_, 0) == 0; }
  bool endsWith(const String &p) const {
    return s_.size() >= p.s_.size() && s_.compare(s_.size() - p.s_.size(), p.s_.size(), p.s_) == 0;
  }
  void remove(size_t index) {
    if (index < s_.size()) s_.erase(index);
  }
  void remove(size_t index, size_t count) {
    if (index < s_.size()) s_.erase(index, count);
  }
  void toUpperCase() {
    for (auto &c : s_) c = char(toupper(uint8_t(c)));
  }
  void toLowerCase() {
    for (auto &c : s_) c = char(tolower(uint8_t(c)));
  }
  void trim() {
    const auto b = s_.find_first_not_of(" \t\r\n");
    const auto e = s_.find_last_not_of(" \t\r\n");
    s_ = b == std::string::npos ? std::string() : s_.substr(b, e - b + 1);
  }
  long toInt() const { return atol(s_.c_str()); }
  float toFloat() const { return float(atof(s_.c_str())); }

 private:
  std::string s_;
};

// The UART log port: stdout here.
class HardwareSerial {
 public:
  void begin(unsigned long) {}
  size_t print(const char *s);
  size_t println(const char *s);
  size_t println(const String &s) { return println(s.c_str()); }
  size_t printf(const char *fmt, ...);
};
extern HardwareSerial Serial;

class EspClass {
 public:
  // The deck restarts into a new palette this way; the simulator starts itself again.
  [[noreturn]] void restart();
};
extern EspClass ESP;
