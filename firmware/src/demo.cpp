#include "demo.h"

#include <Arduino.h>
#include <lvgl.h>
#include <math.h>
#include <time.h>

#include "cheat.h"
#include "dayview.h"
#include "idle.h"
#include "launcher.h"
#include "model.h"
#include "page_numpad.h"
#include "pc.h"
#include "sessions.h"
#include "ui.h"
#include "ui_kit.h"

namespace ui::demo {
namespace {

// Every topic the demo sends. The deck's own state for them is copied when it starts and
// put back when it ends, with a new version so the pages draw it again.
#define DEMO_TOPICS(X)                                                                                         \
  X(media) X(volume) X(mixer) X(devices) X(mic) X(claude) X(term) X(build) X(recent) X(actions) X(shortcuts)     \
  X(context) X(approval) X(question) X(usage) X(git) X(pc) X(call) X(windows) X(zones) X(keyboard) X(keymap)       \
  X(clips) X(obsidian) X(google) X(tasks)

struct Saved {
#define DEMO_FIELD(name) decltype(model::name) name;
  DEMO_TOPICS(DEMO_FIELD)
#undef DEMO_FIELD
  model::Clock clock;
  int page = 0;
};
Saved *saved = nullptr;  // in PSRAM, like everything the loop allocates
uint32_t made_up_clock_ms = 0;  // the time the demo made up, by its synced_at_ms; 0 if the PC's was there

// ---- The made-up day ----
// As the companion would send it (protocol/v0.md), with times from now: "$U+270" is the UTC
// time 270 s on, "$D-1" yesterday's local date.

const char kMedia[] = R"j({"present":true,"title":"Low Orbit","artist":"Night Office","app":"Spotify","playing":true,
  "pos":96,"len":254,"shuffle":false,"repeat":"none","art":""})j";

const char kDevices[] = R"j({"out":[{"id":"o1","name":"Speakers","detail":"Realtek(R) Audio","sel":true},
  {"id":"o2","name":"Headphones","detail":"USB Audio","sel":false},{"id":"o3","name":"Monitor","detail":"Display Audio","sel":false}],
  "in":[{"id":"i1","name":"Microphone Array","detail":"Intel Smart Sound","sel":true},
  {"id":"i2","name":"Headset","detail":"USB Audio","sel":false}]})j";

const char kMic[] = R"j({"available":true,"muted":false})j";

const char kUsage[] = R"j({"five":62,"week":17,"start":$U-9000,"resets":$U+9000,"eta":$U+7600,"rate":18,"points":[[0,0],
  [300,1],[600,2],[900,3],[1200,5],[1500,6],[1800,8],[2100,9],[2400,11],[2700,13],[3000,15],[3300,17],[3600,19],
  [3900,21],[4200,23],[4500,25],[4800,27],[5100,30],[5400,32],[5700,34],[6000,37],[6300,39],[6600,41],[6900,44],
  [7200,46],[7500,49],[7800,51],[8100,54],[8400,57],[8700,59],[9000,62]]})j";

const char kSessions[] = R"j({"sessions":[
  {"id":"demo-s2","project":"api-gateway","state":"input","detail":"Bash: kubectl rollout restart deploy/api","secs":95,
   "app":"VS Code","model":"Sonnet 4.5"},
  {"id":"demo-s1","project":"flex-screen","state":"working","detail":"Edit: page_today.cpp","secs":840,
   "app":"Claude app","model":"Opus 5.5"},
  {"id":"demo-s3","project":"icons","state":"working","detail":"Write: arrow.svg","secs":360,"app":"Terminal",
   "model":"Haiku 4.5","progress":{"label":"Converting icons","done":7,"total":12}},
  {"id":"demo-s4","project":"website","state":"done","detail":"Finished","secs":1320,"app":"VS Code · SSH","model":"Opus 5.5"},
  {"id":"demo-s5","project":"notes","state":"idle","detail":"","secs":3000,"app":"Claude app","model":""}]})j";

const char kTerm[] = R"j({"repo":"flex-screen","cmd":"git status","running":false,"secs":120,"exit":0,"ms":300,
  "lines":["On branch master","Your branch is ahead of 'origin/master' by 2 commits."]})j";

const char kBuild[] = R"j({"state":"passed","cmd":"dotnet build","ms":4800,"secs":600,"history":"PPFPPPPFPP"})j";

const char kActions[] = R"j({"list":[{"id":"a1","kind":"window","name":"VS Code","title":"page_today.cpp - flex-screen","icon":""},
  {"id":"a2","kind":"note","name":"Obsidian","title":"Today's note","icon":""},
  {"id":"a3","kind":"command","name":"Terminal","title":"git status","icon":""},
  {"id":"a4","kind":"meeting","name":"Meeting","title":"Standup","icon":""},
  {"id":"a5","kind":"device","name":"Output","title":"Headphones","icon":""}]})j";

const char kShortcuts[] = R"j({"list":[{"id":"k1","name":"Gmail","icon":"","glyph":"mail"},
  {"id":"k2","name":"Calendar","icon":"","glyph":"calendar"},{"id":"k3","name":"Today's note","icon":"","glyph":"note"},
  {"id":"k4","name":"Spotify","icon":"","glyph":"music"},{"id":"k5","name":"VS Code","icon":"","glyph":"code"},
  {"id":"k6","name":"Terminal","icon":"","glyph":"terminal"},{"id":"k7","name":"Git","icon":"","glyph":"git"},
  {"id":"k8","name":"Lock PC","icon":"","glyph":"lock"}]})j";

const char kContextCode[] = R"j({"app":"VS Code","key":"code","icon":"","color":3120112,"keys":[
  {"id":"build","name":"Build","glyph":"play"},{"id":"terminal","name":"Terminal","glyph":"terminal"},
  {"id":"goto-file","name":"Go to file","glyph":"file"},{"id":"search","name":"Search","glyph":"search"},
  {"id":"approve","name":"Approve","glyph":"approve"},{"id":"model","name":"Model","glyph":"brain"}]})j";
const char kContextGmail[] = R"j({"app":"Gmail","key":"chrome:gmail","icon":"","color":15352629,"keys":[
  {"id":"compose","name":"Compose","glyph":"edit"},{"id":"reply","name":"Reply","glyph":"reply"},
  {"id":"archive","name":"Archive","glyph":"download"},{"id":"delete","name":"Delete","glyph":"trash"},
  {"id":"unread","name":"Unread","glyph":"unread"},{"id":"search","name":"Search","glyph":"search"}]})j";
const char kContextSpotify[] = R"j({"app":"Spotify","key":"spotify","icon":"","color":2021216,"keys":[
  {"id":"prev","name":"Previous","glyph":"prev-track"},{"id":"play","name":"Play/pause","glyph":"pause"},
  {"id":"next","name":"Next","glyph":"next-track"},{"id":"like","name":"Like","glyph":"heart"},
  {"id":"shuffle","name":"Shuffle","glyph":"shuffle"},{"id":"search","name":"Search","glyph":"search"}]})j";

const char kContextExplorer[] = R"j({"app":"Explorer","key":"explorer","icon":"","color":15777320,"keys":[
  {"id":"up","name":"Up","glyph":"up"},{"id":"new-folder","name":"New folder","glyph":"folder-plus"},
  {"id":"rename","name":"Rename","glyph":"cursor"},{"id":"copy-path","name":"Copy path","glyph":"copy"},
  {"id":"terminal-here","name":"Terminal here","glyph":"terminal"},{"id":"code-here","name":"VS Code here","glyph":"code"}]})j";

const char kNoApproval[] = R"j({"id":"","count":0})j";
const char kApproval[] = R"j({"id":"demo-p1","project":"flex-screen","tool":"Bash","detail":"Bash: npm run build",
  "always":true,"count":1})j";

const char kNoQuestion[] = R"j({"id":""})j";
const char kAsk[] = R"j({"id":"demo-q1","kind":"ask","level":"","session":"demo-s1","project":"flex-screen",
  "text":"Convert the other 12 files too?","options":[{"label":"Yes","note":""},{"label":"No","note":""},
  {"label":"Only the icons","note":""}],"multi":false,"step":1,"steps":1,"count":1})j";
const char kNote[] = R"j({"id":"demo-n1","kind":"note","level":"done","session":"demo-s1","project":"flex-screen",
  "text":"All 12 icons converted; 2 need a look","options":[],"multi":false,"step":1,"steps":1,"count":1})j";

const char kGit[] = R"j({"repo":"flex-screen","branch":"master","ahead":2,"behind":0,"m":3,"a":1,"del":0,"u":2,
  "last":"Recent actions on Today","last_secs":3600,"busy":"","error":""})j";

const char kPc[] = R"j({"locked":false,"idle":0})j";
const char kNoCall[] = R"j({"app":""})j";

const char kClips[] = R"j({"list":[{"id":"c1","text":"ssh deploy@10.0.0.12","kind":"command","pinned":true,"secs":5000},
  {"id":"c2","text":"https://example.com/release-notes","kind":"link","pinned":false,"secs":30},
  {"id":"c3","text":"lv_obj_set_style_pad_all(c, 10, 0);","kind":"code","pinned":false,"secs":200},
  {"id":"c4","text":"#5AB4FF","kind":"color","pinned":false,"secs":900},
  {"id":"c5","text":"Meeting moved to 13:00, room 4","kind":"text","pinned":false,"secs":3000},
  {"id":"c6","text":"Thanks! The report is attached.","kind":"text","pinned":false,"secs":7200}]})j";

const char kObsidian[] = R"j({"found":true,"vault":"Notes","recent":[{"name":"Daily note","path":"Daily/today.md"},
  {"name":"flex-screen ideas","path":"Projects/flex-screen ideas.md"},{"name":"Reading list","path":"Reading list.md"},
  {"name":"Inbox","path":"Inbox.md"},{"name":"Groceries","path":"Home/Groceries.md"}]})j";

// The day: two meetings done, Standup in a few minutes (the status bar's countdown, Up next),
// the afternoon, and tomorrow's first, in three calendars' colors (Work, Team, Personal).
const char kCalendar[] = R"j({"status":"ok","events":[
  {"id":"e5","title":"Focus time","cal":"Work","color":5017087,"start":$U-12600,"end":$U-9000,"allday":false,"join":false},
  {"id":"e6","title":"Coffee with Noa","cal":"Personal","color":3982474,"start":$U-5400,"end":$U-3600,"allday":false,"join":false},
  {"id":"e1","title":"Standup","cal":"Team","color":10976255,"start":$U+270,"end":$U+1170,"allday":false,"join":true},
  {"id":"e2","title":"Design review","cal":"Work","color":5017087,"start":$U+10800,"end":$U+14400,"allday":false,"join":true},
  {"id":"e3","title":"1:1 with Dana","cal":"Work","color":5017087,"start":$U+18000,"end":$U+19800,"allday":false,"join":true},
  {"id":"e7","title":"Gym","cal":"Personal","color":3982474,"start":$U+25200,"end":$U+28800,"allday":false,"join":false},
  {"id":"e8","title":"Sprint planning","cal":"Team","color":10976255,"start":$U+86400,"end":$U+90000,"allday":false,"join":true}]})j";

const char kTasks[] = R"j({"rev":1,"status":"ok","lists":[{"src":"google","id":"L1","name":"My Tasks","show":true},
  {"src":"google","id":"L2","name":"Home","show":true},{"src":"google","id":"L3","name":"Work","show":true}],"tasks":[
  {"src":"google","list":"L3","id":"t1","title":"Review the release notes","sub":"","due":"$D-1","done":false,"child":false},
  {"src":"google","list":"L1","id":"t2","title":"Send the signed contract","sub":"From: Dana","due":"$D+0","done":false,"child":false},
  {"src":"google","list":"L2","id":"t3","title":"Pay the electricity bill","sub":"","due":"$D+0","done":false,"child":false},
  {"src":"google","list":"L3","id":"t4","title":"Show the deck to the team","sub":"","due":"$D+0","done":false,"child":false},
  {"src":"google","list":"L1","id":"t5","title":"Book the car service","sub":"","due":"$D+2","done":false,"child":false},
  {"src":"google","list":"L3","id":"t6","title":"Update the onboarding doc","sub":"","due":"$D+5","done":false,"child":false},
  {"src":"google","list":"L2","id":"t7","title":"Fix the balcony light","sub":"","due":"","done":false,"child":false}],
  "open":7,"sched":6})j";

// A small split keyboard (3x5 a side, three thumb keys each) with three layers, every key
// labelled (an empty one draws as a blank key). The left thumb's middle key holds NAV (key
// 16), the right's SYMBOL (key 34). Arrows and media keys are the symbol font's icons, which
// the cheat sheet draws large.
const char kKeymap[] = R"j({"keys":[[50,50,100,100],[150,50,100,100],[250,50,100,100],[350,50,100,100],[450,50,100,100],
  [50,150,100,100],[150,150,100,100],[250,150,100,100],[350,150,100,100],[450,150,100,100],[50,250,100,100],
  [150,250,100,100],[250,250,100,100],[350,250,100,100],[450,250,100,100],[250,360,100,100],[350,360,100,100],
  [450,360,100,100],[700,50,100,100],[800,50,100,100],[900,50,100,100],[1000,50,100,100],[1100,50,100,100],
  [700,150,100,100],[800,150,100,100],[900,150,100,100],[1000,150,100,100],[1100,150,100,100],[700,250,100,100],
  [800,250,100,100],[900,250,100,100],[1000,250,100,100],[1100,250,100,100],[700,360,100,100],[800,360,100,100],
  [900,360,100,100]],"layers":[
  {"n":0,"name":"BASE","keys":["Q","W","E","R","T","A","S","D","F","G","Z","X","C","V","B","Esc","Spc\n\nNAV","Tab",
   "Y","U","I","O","P","H","J","K","L",";","N","M",",",".","/","Ent","Bsp\n\nSYM","Del"],
   "dim":"000000000000000000000000000000000000"},
  {"n":1,"name":"NAV","keys":["Esc","Home","","End","PgUp","Tab","","","","PgDn",
   "Undo","Cut","Copy","Paste","Redo","Ctrl","NAV","Shift",
   "","","","","","","Win","Alt","Lock","Snip",
   "F1","F2","F3","F4","F5","Ent","Bsp","Del"],"dim":"000000000000000000000000000000000000"},
  {"n":2,"name":"SYMBOL","keys":["!","@","#","$","%","^","&","*","(",")","[","]","{","}","\\","Esc","Spc","Tab",
   "1","2","3","4","5","6","7","8","9","0","-","=","+","_","|","Ent","SYM","Del"],
   "dim":"000000000000000000000000000000000000"}]})j";

// The made-up line's times, from now (see above).
String expand(const char *s) {
  String out;
  out.reserve(strlen(s) + 64);
  const int64_t utc = model::utcNow(), local = model::localNow();
  while (*s) {
    if (s[0] == '$' && (s[1] == 'U' || s[1] == 'D') && (s[2] == '+' || s[2] == '-')) {
      char *end = nullptr;
      const long n = strtol(s + 2, &end, 10);
      char buf[24];
      if (s[1] == 'U') {
        snprintf(buf, sizeof(buf), "%lld", (long long)(utc + n));
      } else {
        const time_t t = time_t(local + int64_t(n) * 86400);
        struct tm tm;
        gmtime_r(&t, &tm);  // local time already
        strftime(buf, sizeof(buf), "%Y-%m-%d", &tm);
      }
      out += buf;
      s = end;
      continue;
    }
    out += *s++;
  }
  return out;
}

void state(const char *topic, const char *d) {
  String line = "{\"t\":\"state\",\"topic\":\"";
  line += topic;
  line += "\",\"d\":";
  line += expand(d);
  line += '}';
  pc::play(line.c_str());
}

void toast(const char *text) {
  char line[160];
  snprintf(line, sizeof(line), "{\"t\":\"toast\",\"level\":\"ok\",\"text\":\"%s\"}", text);
  pc::play(line);
}

// The terminal's recent commands, `npm test` on top: running (`secs` since it started), or
// passed (`secs` since it finished).
void recent(bool running, int secs) {
  char d[480];
  snprintf(d, sizeof(d), R"j({"list":[{"cmd":"npm test","running":%s,"exit":0,"ms":3400,"secs":%d},
  {"cmd":"git status","running":false,"exit":0,"ms":300,"secs":120},
  {"cmd":"npm run lint","running":false,"exit":1,"ms":5200,"secs":3000},
  {"cmd":"dotnet build -c Release","running":false,"exit":0,"ms":4800,"secs":7200}]})j",
           running ? "true" : "false", secs);
  state("recent", d);
}

// The faders and the master volume, swinging `secs` into the Audio step (0: at rest).
struct Fader {
  const char *id, *name;
  int level;
};
constexpr Fader kFaders[] = {
    {"spotify", "Spotify", 47}, {"ms-teams", "Teams", 80}, {"chrome", "Chrome", 33}, {"discord", "Discord", 60}, {"system", "System", 85},
};

int swing(int level, float secs, int i) {
  const float phase = 1.3f * float(i);
  const float v = float(level) + 26.0f * (sinf(2.4f * secs + phase) - sinf(phase));
  return v < 0 ? 0 : v > 100 ? 100 : int(v + 0.5f);
}

void levels(float secs) {
  String d = "{\"apps\":[";
  for (int i = 0; i < int(sizeof(kFaders) / sizeof(kFaders[0])); i++) {
    char app[96];
    snprintf(app, sizeof(app), "%s{\"id\":\"%s\",\"name\":\"%s\",\"v\":%d,\"muted\":false}", i ? "," : "", kFaders[i].id,
             kFaders[i].name, swing(kFaders[i].level, secs, i));
    d += app;
  }
  d += "]}";
  state("mixer", d.c_str());
  char vol[40];
  snprintf(vol, sizeof(vol), "{\"v\":%d,\"muted\":false}", swing(42, secs * 0.7f, 7));
  state("vol", vol);
}

// One Claude session, in VS Code: `done` of 12 icons converted, or no progress (-1).
void oneSession(const char *what, const char *detail, int done) {
  char progress[80] = "";
  if (done >= 0) snprintf(progress, sizeof(progress), ",\"progress\":{\"label\":\"Converting icons\",\"done\":%d,\"total\":12}", done);
  char d[320];
  snprintf(d, sizeof(d),
           "{\"sessions\":[{\"id\":\"demo-s1\",\"project\":\"flex-screen\",\"state\":\"%s\",\"detail\":\"%s\",\"secs\":95,"
           "\"app\":\"VS Code\",\"model\":\"Opus 5.5\"%s}]}",
           what, detail, progress);
  state("claude", d);
}

// The windows on the PC, `active` the one in front (VS Code 0, Chrome 1, Spotify 6).
void windowsList(int active) {
  static const char *const kWindows[][2] = {
      {"VS Code", "page_today.cpp - flex-screen"}, {"Chrome", "Inbox (8) - Gmail"}, {"Terminal", "PowerShell"},
      {"Obsidian", "Daily note - Notes"},           {"Explorer", "flex-screen"},     {"Claude", "Claude"},
      {"Spotify", "Night Office - Low Orbit"},      {"Figma", "Deck stand"},
  };
  String d = "{\"list\":[";
  for (int i = 0; i < 8; i++) {
    const bool minimized = (i == 3 || i == 6) && i != active;
    char win[200];
    snprintf(win, sizeof(win), "%s{\"id\":\"%d\",\"app\":\"%s\",\"title\":\"%s\",\"icon\":\"\",\"active\":%s,\"min\":%s}",
             i ? "," : "", 101 + i, kWindows[i][0], kWindows[i][1], i == active ? "true" : "false",
             minimized ? "true" : "false");
    d += win;
  }
  d += "]}";
  state("windows", d.c_str());
}

// Two monitors' FancyZones, `app` the one in front (what a zone tap would snap).
void zones(const char *app) {
  char d[1200];
  snprintf(d, sizeof(d), R"j({"mons":[
    {"name":"Main display","primary":true,"w":1920,"h":1032,"type":"custom","n":3,"layout":"custom-1",
     "zones":[[0,0,1215,1032,"VS Code"],[1215,0,705,516,"Terminal"],[1215,516,705,516,"Spotify"]]},
    {"name":"Side display","primary":false,"w":1080,"h":1920,"type":"rows","n":2,"layout":"rows",
     "zones":[[0,0,1080,960,"Chrome"],[0,960,1080,960,""]]}],
    "active":0,"app":"%s","layouts":[
    {"id":"custom-1","name":"Coding","custom":true,"cells":[[0,0,24,23],[25,0,14,11],[25,12,14,11]]},
    {"id":"priority-grid","name":"Priority","custom":false,"cells":[[0,0,9,25],[10,0,19,25],[30,0,9,25]]},
    {"id":"columns","name":"Columns","custom":false,"cells":[[0,0,12,25],[13,0,12,25],[26,0,13,25]]},
    {"id":"rows","name":"Rows","custom":false,"cells":[[0,0,39,12],[0,13,39,12]]}]})j",
           app);
  state("zones", d);
}

void inCall(bool muted, int hand) {
  char d[220];
  snprintf(d, sizeof(d),
           "{\"app\":\"teams\",\"name\":\"Teams\",\"secs\":754,\"mic_muted\":%s,\"camera\":1,\"hand\":%d,\"deafened\":false,"
           "\"can\":[\"mic\",\"camera\",\"hand\",\"leave\",\"react\"],\"note\":\"\",\"volume\":70}",
           muted ? "true" : "false", hand);
  state("call", d);
}

// Zoom, annotating a shared screen: the pen or the marker in a color (`color`, an index), or
// the laser's spotlight (`color` -1: the laser has its kinds, not colors).
void zoomDrawing(const char *tool, int color) {
  char draw[460];
  if (color >= 0) {
    snprintf(draw, sizeof(draw),
             "\"colors\":[\"FFFFFF\",\"F42E2E\",\"FFD43C\",\"3BC255\",\"1A73E8\",\"B57CF2\",\"FF5BB4\",\"FF8A1F\","
             "\"A6D93A\",\"62D3F5\",\"5A5A5A\",\"9E1B1B\",\"8A5A2B\",\"1E7A35\",\"1D3E9E\"],\"color\":%d,"
             "\"kinds\":[],\"kind\":\"\",\"can_color\":true,\"can_width\":true",
             color);
  } else {
    snprintf(draw, sizeof(draw),
             "\"colors\":[],\"color\":-1,\"kinds\":[{\"id\":\"spotlight\",\"name\":\"Spotlight\"},"
             "{\"id\":\"arrow\",\"name\":\"Arrow\"}],\"kind\":\"spotlight\",\"can_color\":false,\"can_width\":false");
  }
  char d[900];
  snprintf(d, sizeof(d),
           "{\"app\":\"zoom\",\"name\":\"Zoom\",\"secs\":1520,\"mic_muted\":false,\"camera\":1,\"hand\":0,"
           "\"deafened\":false,\"can\":[\"mic\",\"camera\",\"hand\",\"annotate\",\"leave\"],\"note\":\"\",\"volume\":70,"
           "\"draw\":{\"mode\":\"annotate\",\"tools\":[\"spotlight\",\"pen\",\"highlighter\",\"vanish\",\"eraser\","
           "\"select\"],\"tool\":\"%s\",%s,\"width\":1,\"undo\":true,\"redo\":false,\"save\":true}}",
           tool, draw);
  state("call", d);
}

// The keyboard on `layer`, its key held for a minute already (the cheat sheet's wait is
// over), or none held (-1).
void keyboard(int layer, const char *name, int key) {
  char d[120];
  snprintf(d, sizeof(d), "{\"present\":true,\"name\":\"Corne\",\"layer\":%d,\"label\":\"%s\",\"held_ms\":%d,\"key\":%d}",
           layer, name, key >= 0 ? 60000 : -1, key);
  state("kbd", d);
}

void loadDay() {
  state("media", kMedia);
  levels(0);
  state("devices", kDevices);
  state("mic", kMic);
  oneSession("working", "Edit: page_today.cpp", -1);
  state("usage", kUsage);
  state("term", kTerm);
  state("build", kBuild);
  recent(false, 900);
  state("actions", kActions);
  state("shortcuts", kShortcuts);
  state("context", kContextCode);
  state("approval", kNoApproval);
  state("question", kNoQuestion);
  state("git", kGit);
  state("pc", kPc);
  state("call", kNoCall);
  windowsList(0);
  zones("VS Code");
  state("keymap", kKeymap);
  keyboard(0, "BASE", -1);
  state("clips", kClips);
  state("obsidian", kObsidian);
  state("calendar", kCalendar);
  state("tasks", kTasks);
}

// ---- Touches ----
// A made-up tap where a finger would go: a ring over the thing tapped, which shows as pressed
// for a moment. Only the look: no event reaches it, so nothing happens but what the step does.

struct {
  lv_obj_t *scrim, *pill, *count, *text, *ring;
} w;
lv_obj_t *pressed = nullptr;  // what the ring is on, shown as pressed
uint32_t touch_ms = 0;        // when; 0: no touch showing
constexpr uint32_t kTouchMs = 350;
constexpr int kRing = 54;

// The topmost label showing under `root` that starts with `text` (one cut short with "…"
// still starts the same). The demo's own caption doesn't count.
lv_obj_t *findLabel(lv_obj_t *root, const char *text) {
  if (root == w.pill || lv_obj_has_flag(root, LV_OBJ_FLAG_HIDDEN)) return nullptr;
  if (lv_obj_check_type(root, &lv_label_class)) {
    const char *t = lv_label_get_text(root);
    return t && !strncmp(t, text, strlen(text)) && lv_obj_is_visible(root) ? root : nullptr;
  }
  for (int32_t i = int32_t(lv_obj_get_child_count(root)) - 1; i >= 0; i--) {
    if (lv_obj_t *found = findLabel(lv_obj_get_child(root, i), text)) return found;
  }
  return nullptr;
}

void endTouch() {
  if (pressed) lv_obj_remove_state(pressed, LV_STATE_PRESSED);
  pressed = nullptr;
  touch_ms = 0;
  lv_obj_set_hidden(w.ring, true);
}

// The demo's caption, and a touch showing, over whatever came forward meanwhile (the day view
// brings itself forward when it opens).
void onTop() {
  lv_obj_move_foreground(w.scrim);
  lv_obj_move_foreground(w.pill);
  lv_obj_move_foreground(w.ring);
}

// Taps what shows `text`: the overlays first, then the page.
void touch(const char *text) {
  endTouch();
  lv_obj_t *l = findLabel(lv_layer_top(), text);
  if (!l) l = findLabel(lv_screen_active(), text);
  if (!l) {
    pc::log("demo: nothing showing says \"%s\"", text);
    return;
  }
  lv_obj_t *target = l;  // the button, tile or card it's on
  while (target && !lv_obj_has_flag(target, LV_OBJ_FLAG_CLICKABLE)) target = lv_obj_get_parent(target);
  lv_area_t a;
  lv_obj_get_coords(target ? target : l, &a);
  if (lv_area_get_width(&a) > 600) lv_obj_get_coords(l, &a);  // a whole panel: the words themselves
  lv_obj_set_pos(w.ring, (a.x1 + a.x2) / 2 - kRing / 2, (a.y1 + a.y2) / 2 - kRing / 2);
  lv_obj_set_hidden(w.ring, false);
  onTop();
  if (target) {
    lv_obj_add_state(target, LV_STATE_PRESSED);
    pressed = target;
  }
  touch_ms = millis() | 1;
}

// ---- The steps ----

uint32_t at = 0, prev = 0;  // ms into the step, this pass and the last
bool passed(uint32_t ms) { return prev < ms && at >= ms; }
bool every(uint32_t ms) { return at / ms != prev / ms; }

void todayStep() { showPage(kTodayPage); }

void dayStep() { showPage(kTodayPage); }
void dayTick() {
  if (passed(1000)) touch("UP NEXT");
  if (passed(1350)) {
    day::open();
    onTop();
  }
}
void dayEnd() { day::close(); }

void audioStep() { showPage(kAudioPage); }
void audioTick() {
  if (every(120)) levels(at / 1000.0f);
}
void audioEnd() { levels(0); }

void devStep() {
  showPage(kDevPage);
  oneSession("working", "Write: icons/arrow.svg", 3);
  recent(true, 0);
}
void devTick() {
  if (every(450) && 3 + int(at / 450) <= 12) oneSession("working", "Write: icons/arrow.svg", 3 + int(at / 450));
  if (passed(3800)) {
    recent(false, 0);
    toast("npm test passed in 3.4 s");
  }
}

void approvalStep() {
  oneSession("input", "Bash: npm run build", -1);
  state("approval", kApproval);
}
void approvalTick() {
  if (passed(3200)) touch("Approve");
  if (!passed(3550)) return;
  state("approval", kNoApproval);
  oneSession("working", "Bash: npm run build", -1);
  toast("Approved: npm run build");
}

void questionStep() { state("question", kAsk); }
void questionTick() {
  if (passed(3400)) touch("Only the icons");
  if (passed(3750)) state("question", kNote);
}
void questionEnd() { state("question", kNoQuestion); }

void boardStep() {
  state("claude", kSessions);
  sessions::open();
}
void boardEnd() {
  sessions::close();
  oneSession("working", "Edit: page_today.cpp", -1);
}

// A window comes to the front: the bar takes its app's keys, the Windows page marks it, and
// a zone tap would snap it.
void bringUp(const char *context, int window, const char *app) {
  state("context", context);
  windowsList(window);
  zones(app);
}

void appsStep() { showPage(kWindowsPage); }
void appsTick() {
  if (passed(1600)) touch("Inbox (8)");
  if (passed(1950)) bringUp(kContextGmail, 1, "Chrome");
  if (passed(4400)) touch("Night Office");
  if (passed(4750)) bringUp(kContextSpotify, 6, "Spotify");
  if (passed(7200)) touch("flex-screen");
  if (passed(7550)) bringUp(kContextExplorer, 4, "Explorer");
}
void appsEnd() { bringUp(kContextCode, 0, "VS Code"); }

void captureStep() { showPage(kCapturePage); }

// A Teams call, then (the next step) Zoom: the call goes on, so the glow blinks blue once.
void callStep() {
  showPage(kTodayPage);
  inCall(false, 0);
}
void callTick() {
  if (passed(2000)) touch("Mic");
  if (passed(2350)) inCall(true, 0);
  if (passed(3900)) touch("Hand");
  if (passed(4250)) inCall(true, 1);
}

void zoomStep() { zoomDrawing("pen", 1); }
void zoomTick() {
  if (passed(2000)) touch("Marker");
  if (passed(2350)) zoomDrawing("highlighter", 2);
  if (passed(4200)) touch("Laser");
  if (passed(4550)) zoomDrawing("spotlight", -1);
}
void zoomEnd() { state("call", kNoCall); }

void shortcutsStep() { launcher::open(); }
void shortcutsTick() {
  if (passed(2600)) touch("Numpad");
  if (passed(2950)) {
    launcher::close();
    numpad::open();
    onTop();
  }
  if (passed(4300)) touch("7");
  if (passed(5300)) touch("9");
}
void shortcutsEnd() {
  launcher::close();
  numpad::close();
}

// Held layer keys show the sheet after settings.json's wait; with that turned off it's pinned.
void layersStep() {
  keyboard(1, "NAV", 16);
  if (!model::settings.cheat_hold_s) cheat::toggle();
}
void layersTick() {
  if (passed(3000)) keyboard(2, "SYMBOL", 34);
}
void layersEnd() {
  keyboard(0, "BASE", -1);
  cheat::unpin();
}

struct Step {
  const char *caption;
  uint32_t ms;
  bool top;  // the caption over the status line rather than the context bar
  void (*begin)();
  void (*tick)();  // every pass while it's on (and once at its end), or null
  void (*end)();   // or null
};

const Step kSteps[] = {
    {"Your day at a glance: meetings, music, tasks", 6000, false, todayStep, nullptr, nullptr},
    {"Tap Up next for the whole day", 6500, true, dayStep, dayTick, dayEnd},
    {"Every app's volume on its own fader", 6000, false, audioStep, audioTick, audioEnd},
    {"Claude Code, the terminal, builds and git", 6500, false, devStep, devTick, nullptr},
    {"Claude asks first: approve it from the deck", 5000, false, approvalStep, approvalTick, nullptr},
    {"Claude can ask you, and tell you when it's done", 6500, false, questionStep, questionTick, questionEnd},
    {"Every Claude session on the PC, and the plan's usage", 6000, false, boardStep, nullptr, boardEnd},
    {"Tap a window: the bar below follows the app in front", 10000, true, appsStep, appsTick, appsEnd},
    {"Clipboard history, notes and tasks", 5000, false, captureStep, nullptr, nullptr},
    {"On a call: mute, raise a hand, react", 6000, false, callStep, callTick, nullptr},
    {"In Zoom: draw on a shared screen from the deck", 7000, false, zoomStep, zoomTick, zoomEnd},
    {"Shortcuts a tap away, and a numpad over USB", 7000, true, shortcutsStep, shortcutsTick, shortcutsEnd},
    {"Hold a layer key and the deck shows that layer", 6000, true, layersStep, layersTick, layersEnd},
    {"That's flex-screen", 3500, false, todayStep, nullptr, nullptr},
};
constexpr int kStepCount = int(sizeof(kSteps) / sizeof(kSteps[0]));

bool on = false;
int step = -1;
uint32_t step_ms = 0;

// ---- The caption ----

constexpr int kPillH = 36;

void onScreenTouched(lv_event_t *) {
  stop();
  if (lv_indev_t *indev = lv_indev_active()) lv_indev_wait_release(indev);  // nothing under it gets this touch
}

void showCaption(const Step &s) {
  setTextUpTo(w.text, s.caption, 460);
  setTextf(w.count, "%d/%d", step + 1, kStepCount);
  // Over the context bar, or over the status line beside the rail.
  if (s.top) lv_obj_align(w.pill, LV_ALIGN_TOP_MID, kRailW / 2, (kStatusH - kPillH) / 2);
  else lv_obj_align(w.pill, LV_ALIGN_BOTTOM_MID, 0, -(480 - kCtxY - (kCtxH + kPillH) / 2));
  lv_obj_fade_in(w.pill, 250, 0);
}

void nextStep() {
  endTouch();
  if (step >= 0 && kSteps[step].end) kSteps[step].end();
  if (++step >= kStepCount) {
    stop();
    return;
  }
  step_ms = millis();
  at = prev = 0;
  kSteps[step].begin();
  onTop();
  showCaption(kSteps[step]);
}

// The firmware's build day at 10:12, for a deck that hasn't had the time from a PC.
int64_t madeUpTime() {
  static const char kMonths[] = "JanFebMarAprMayJunJulAugSepOctNovDec";
  char mon[4] = "";
  int day = 1, year = 2026;
  sscanf(__DATE__, "%3s %d %d", mon, &day, &year);
  const char *m = strstr(kMonths, mon);
  const int month = m ? int(m - kMonths) / 3 + 1 : 1;
  // Days since 1970-01-01 (Howard Hinnant's days_from_civil, for years after 0).
  const int y = year - (month <= 2 ? 1 : 0);
  const int era = y / 400, yoe = y - era * 400;
  const int doy = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
  const int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
  return (int64_t(era) * 146097 + doe - 719468) * 86400 + 10 * 3600 + 12 * 60;
}

}  // namespace

void build() {
  w.scrim = box(lv_layer_top());
  lv_obj_set_size(w.scrim, 800, 480);
  lv_obj_set_clickable(w.scrim, true);
  lv_obj_add_event_cb(w.scrim, onScreenTouched, LV_EVENT_PRESSED, nullptr);
  lv_obj_set_hidden(w.scrim, true);

  w.pill = card(lv_layer_top(), kRaised, kAccent);
  lv_obj_set_size(w.pill, LV_SIZE_CONTENT, kPillH);
  lv_obj_set_style_radius(w.pill, kPillH / 2, 0);
  lv_obj_set_style_pad_ver(w.pill, 0, 0);
  lv_obj_set_style_pad_left(w.pill, 5, 0);
  lv_obj_set_style_pad_right(w.pill, 16, 0);
  lv_obj_set_style_shadow_width(w.pill, 24, 0);
  lv_obj_set_style_shadow_color(w.pill, hex(0x000000), 0);
  lv_obj_set_style_shadow_opa(w.pill, LV_OPA_50, 0);
  flexRow(w.pill, 10);
  lv_obj_set_clickable(w.pill, true);
  lv_obj_add_event_cb(w.pill, onScreenTouched, LV_EVENT_PRESSED, nullptr);

  lv_obj_t *tag = card(w.pill, kAccent, kAccent);
  lv_obj_set_size(tag, LV_SIZE_CONTENT, kPillH - 10);
  lv_obj_set_style_radius(tag, (kPillH - 10) / 2, 0);
  lv_obj_set_style_pad_ver(tag, 0, 0);
  lv_obj_set_style_pad_hor(tag, 10, 0);
  flexRow(tag, 6);
  label(tag, "DEMO", &small_font, kOnAccent);
  w.count = label(tag, "", &small_font, kOnAccent);
  lv_obj_set_style_text_opa(w.count, LV_OPA_70, 0);

  w.text = label(w.pill, "", &body_font, kText);
  label(w.pill, "Tap to end", &small_font, kMuted);
  lv_obj_set_hidden(w.pill, true);

  // Where a made-up tap lands: a fingertip-sized ring.
  w.ring = box(lv_layer_top());
  lv_obj_set_size(w.ring, kRing, kRing);
  lv_obj_set_style_radius(w.ring, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_bg_color(w.ring, hex(0xFFFFFF), 0);
  lv_obj_set_style_bg_opa(w.ring, LV_OPA_30, 0);
  lv_obj_set_style_border_color(w.ring, hex(0xFFFFFF), 0);
  lv_obj_set_style_border_opa(w.ring, LV_OPA_80, 0);
  lv_obj_set_style_border_width(w.ring, 3, 0);
  lv_obj_set_hidden(w.ring, true);
}

void start() {
  if (on) return;
  // Whatever's open over the pages makes way (the diagnostics close themselves).
  launcher::close();
  numpad::close();
  sessions::close();
  day::close();
  cheat::unpin();

  saved = new Saved;
#define DEMO_SAVE(name) saved->name = model::name;
  DEMO_TOPICS(DEMO_SAVE)
#undef DEMO_SAVE
  saved->clock = model::clock;
  saved->page = currentPage();
  made_up_clock_ms = 0;
  if (!model::clock.epoch) {
    made_up_clock_ms = millis() | 1;
    model::clock.epoch = madeUpTime();
    model::clock.tz_offset_min = 0;
    model::clock.synced_at_ms = made_up_clock_ms;
  }

  pc::demoMode(true);
  on = true;
  loadDay();
  pc::log("demo: started");

  lv_obj_set_hidden(w.scrim, false);
  lv_obj_set_hidden(w.pill, false);
  step = -1;
  nextStep();  // and over anything that came forward since it was built
}

void stop() {
  if (!on) return;
  on = false;
  endTouch();
  launcher::close();
  numpad::close();
  sessions::close();
  day::close();
  cheat::unpin();

  // The deck's own state again, as a new version of each, so every page redraws it.
#define DEMO_RESTORE(name)                    \
  {                                           \
    const uint32_t v = model::name.version; \
    model::name = saved->name;                \
    model::name.version = v + 1;              \
  }
  DEMO_TOPICS(DEMO_RESTORE)
#undef DEMO_RESTORE
  if (made_up_clock_ms && model::clock.synced_at_ms == made_up_clock_ms) model::clock = saved->clock;
  pc::demoMode(false);  // then what the PC sent meanwhile
  showPage(saved->page);
  delete saved;
  saved = nullptr;

  lv_obj_set_hidden(w.scrim, true);
  lv_obj_set_hidden(w.pill, true);
  if (step >= kStepCount) pc::log("demo: played through");
  else pc::log("demo: ended at step %d of %d", step + 1, kStepCount);
}

void update() {
  if (!on) return;
  idle::wake();  // no clock screen in the middle of it
  if (touch_ms && millis() - touch_ms >= kTouchMs) endTouch();
  const Step &s = kSteps[step];
  at = millis() - step_ms;
  // What's due before the step ends still happens, after a stall too (a screenshot holds the
  // loop for 2.5 s: the approval stayed up through the next step).
  const bool over = at >= s.ms;
  if (over) at = s.ms;
  if (s.tick) s.tick();
  prev = at;
  if (over) nextStep();
}

bool playing() { return on; }

}  // namespace ui::demo
