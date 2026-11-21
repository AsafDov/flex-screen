# Profiles and settings

What changes from person to person or PC to PC is settings, not code: a new calendar, account
or site needs no new firmware or release.

## Profiles
Your settings live in a **profile**: its Google accounts and task sources, shortcuts,
context bar keys, colors and the rest. Keep one, or several to switch between (work and
home, say): tray icon → **Profile: …** lists them, and **New profile…** starts one as a copy
of the current one or from the defaults. The deck says *Profile: work* when it switches,
and reads the calendar and the tasks again at once.

- **Profiles are files**, `%LOCALAPPDATA%\flex-screen\profiles\<name>.json`. **Keep the
  profiles in…** moves them to another folder: a synced one (OneDrive, Dropbox, a git repo)
  gives every PC the same profiles. Pick the same folder on each PC.
- **Sign-ins stay on each PC.** A profile names the accounts and sites; each PC signs in to
  each one once, and keeps the tokens encrypted for its Windows user, one per account or site.
  A profile can go anywhere: there's nothing secret in it.
- **This PC's own settings** are in `%LOCALAPPDATA%\flex-screen\machine.json` (tray icon →
  Profile → **Open this PC's settings**): the profile it uses, where the profiles are, and
  the Obsidian vault's folder. Any profile setting written there wins on this PC only, so
  one profile can still differ per PC: `"jira": null` turns the profile's Jira off here, a
  `"shortcuts"` list of its own gives this PC other tiles.

```jsonc
// machine.json
{
  "profile": "work",                                   // profiles\work.json; "default" when not set
  "profilesFolder": "C:\\Users\\me\\OneDrive\\flex-screen",  // else %LOCALAPPDATA%\flex-screen\profiles
  "obsidianVault": "C:\\path\\to\\vault"                // Task sources › Obsidian daily note picks it; else the open vault
}
```

The `settings.json` from before profiles becomes the `default` profile (and the vault goes
to `machine.json`) the first time the companion starts; the old file stays beside it as
`settings.json.before-profiles`.

## Settings
Optional, in the profile. The tray icon's **Profile → Open profile file** opens it, with the
everyday settings it doesn't have yet (colors, idle times, layer names, cheat sheet time)
written in at their current values. Saved changes apply straight away. Its `"$schema"` line
gives editors that read it (VS Code) completion and checks for everything here
([companion/profile.schema.json](../companion/profile.schema.json)).

```jsonc
{
  "palette": "base",                                   // or "vibrant", "ember", "violet", "pastel"
  "idle": { "clockAfterMinutes": 10, "screenOffAfterMinutes": 30 },  // 0 turns either off
  "layerNames": ["BASE", "NAV", "SYMBOL"],             // your keyboard's layers
  "cheatSheetSeconds": 10,                             // hold a layer key this long for its cheat sheet; 0: only from the pill
  "countShortcuts": true,                              // count the shortcuts you press per app, for the context bar to offer; false: off
  "accounts": {                                        // by id (the tray's Google accounts and Calendars); without it, one: "google"
    "google": { "type": "google", "label": "Personal", "calendars": { "hide": ["Birthdays"] } },
    "google-work": { "type": "google", "label": "Work", "client": "google-client-work.json", "tasks": false },
    "microsoft-work": { "type": "microsoft", "label": "Work", "clientId": "…", "tenant": "organizations" },
    "ics-school": { "type": "ics", "label": "School", "color": "#33B679" }  // its link is kept on each PC; a public one can be "url" here
  },
  "calendarFilter": { "hideTitles": ["Lunch", "/^Focus/"], "hideAllDay": false, "hideDeclined": true, "hideFree": false },
  "hiddenTaskLists": ["google/MTIzNDU2"],              // task lists left off the deck (the tray's Task lists on the deck)
  "obsidianTasks": true,                               // the latest daily note's tasks on the deck
  "jira": { "url": "https://acme.atlassian.net", "email": "me@acme.com", "jql": "…" },  // set by Task sources › Jira
  "github": { "host": "github.com", "query": "is:open is:issue assignee:@me" },          // set by Task sources › GitHub
  "shortcuts": [                                       // the launcher's tiles, up to 12
    { "name": "Gmail", "url": "https://mail.google.com" },
    { "name": "Spotify", "app": "Spotify" },             // a Start menu app, by its name there
    { "name": "Projects", "path": "C:\\Projects" },      // a file, folder or program ("args" too)
    { "name": "Today's note", "action": "obs.today" },   // something the deck does
    { "name": "Snip", "keys": "Win+Shift+S" }            // keys pressed in the window in front
  ]
}
```

A shortcut can also have `"glyph"` (the symbol shown without an icon: `mail`, `calendar`,
`note`, `terminal`, `code`, `music`, `lock`, `desktop`, `folder`, `keys`, `git`, `call`,
`volume`, `volume-up`, `volume-down`, `screen-off`, `taskmgr`) and `"icon"` (a picture or
program whose icon to use). Actions: `obs.today`, `mail.open`, `pc.lock`, `desktop.show`,
`screens.off`, `taskmgr.open`, `media.toggle`, `media.next`, `media.prev`, `vol.mute`,
`vol.up`, `vol.down`, `mic.mute`, `term.focus`, `git.commit`, `git.fetch`, `git.pull`, `git.push`, `git.stash`,
`call.mic`, `call.camera`, `call.hand`, `call.leave`, `call.focus`. The tray icon's
**Shortcuts…** opens this file, with the default list written in if it has none.

Your own actions for the context bar, per app, by its exe name (`chrome`, `spotify`) or
the name the deck shows (`Chrome`), `"*"` for apps without a list of their own. They join
that app's list (long-press a key to pick them) and make up its bar until you rearrange it.
`"contextBar": false` turns the bar off:

```jsonc
"contextBar": {
  "Spotify": { "color": "#1DB954", "keys": [
    { "name": "Like", "keys": "Alt+Shift+B", "glyph": "heart" },
    { "name": "Shuffle", "keys": "Ctrl+S", "glyph": "shuffle" }
  ] },
  "Chrome": [ { "name": "Pin tab", "keys": "Alt+Shift+P", "glyph": "pin" } ]
}
```

A key is `keys` (pressed in that app's window; `"g, i"` presses one after the other),
`text` (typed; `\n` for Enter: `{ "name": "Status", "text": "git status\n" }`), `action`
(as for shortcuts), or `"action": "close"`. Up to 8 per app.
`"approveFromDeck": false` turns the approval banner off (also in the tray menu). Glyphs: `play`, `pause`, `terminal`, `keyboard`,
`code`, `file`, `folder`, `search`, `split-right`, `split-down`, `back`, `forward`, `up`,
`down`, `prev`, `next`, `reload`, `undo`, `redo`, `history`, `plus`, `close`, `copy`,
`paste`, `save`, `edit`, `calendar`, `graph`, `bell`, `chat`, `mic`, `mic-off`, `video`,
`hand`, `screen`, `leave`, `headphones`, `hashtag`, `switch`, `unread`, `heart`, `star`,
`shuffle`, `music`, and more (the list is in `firmware/scripts/gen_font_icons.py`).
