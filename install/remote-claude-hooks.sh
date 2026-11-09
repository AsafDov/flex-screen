#!/bin/sh
# Adds the flex-screen deck's Claude Code hooks on a Linux machine you work on over SSH
# (VS Code's Remote-SSH), so the deck shows the Claude sessions running there.
#
#   sh remote-claude-hooks.sh            # add them (again: replaces ours, keeps the rest)
#   sh remote-claude-hooks.sh --remove   # take them out
#
# The hooks post to localhost:48151 on this machine, which leads to the deck's PC only
# through an SSH reverse tunnel. On the PC, in C:\Users\<you>\.ssh\config, under this
# machine's Host entry, add:
#
#     RemoteForward 48151 localhost:48151
#
# and reconnect (VS Code: "Remote-SSH: Kill VS Code Server on Host...", then open the folder
# again). Without the tunnel the hooks do nothing: they're asynchronous and give up in 2 s.
#
# It also writes ~/.claude/flex-screen-usage.sh, which the SessionStart and Stop hooks run
# in the background (at most every 3 minutes): Claude Code's own /usage, headless, so the
# deck's Usage key shows the plan's 5-hour figure for sessions here. No hook carries it.
set -eu

port="${FLEX_SCREEN_PORT:-48151}"
url="http://localhost:$port/claude"
settings="$HOME/.claude/settings.json"

command -v python3 >/dev/null || { echo "python3 is needed to edit $settings" >&2; exit 1; }
command -v curl >/dev/null || echo "Note: curl isn't installed here; the hooks run it." >&2

python3 - "$settings" "$url" "${1:-}" <<'EOF'
import json, os, shutil, sys

path, url, mode = sys.argv[1], sys.argv[2], sys.argv[3]
events = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd",
          "PermissionRequest", "PostModelSwitch"]

root = {}
if os.path.exists(path):
    text = open(path, encoding="utf-8").read()
    if text.strip():
        root = json.loads(text)  # a file that doesn't parse stops here, untouched
hooks = root.setdefault("hooks", {})

def ours(entry):
    return any(url in (h.get("args") or []) or url in str(h.get("command", "")) for h in entry.get("hooks", []))

# The event as Claude sends it, plus the model of Claude's last reply in the session, read
# from its transcript here (the PC can't open it): the deck's Model key shows it.
POST = (r"""j=$(cat); t=$(printf '%s' "$j" | sed -n 's/.*"transcript_path" *: *"\([^"]*\)".*/\1/p' | head -n 1); """
        r"""m=$([ -f "$t" ] && tail -c 262144 "$t" | grep '"type":"assistant"' | grep -v '"isSidechain":true' | tail -n 1 | grep -o '"model":"claude-[^"]*"' | head -n 1 | cut -d'"' -f4); """
        r"""printf '%s' "$j" | curl -s -m 2 -X POST -H 'Content-Type: application/json' -H "X-Claude-Model: $m" --data-binary @- """)

# The plan's usage for the deck's Usage key ("5h 42%"): no hook carries it, so a helper runs
# Claude Code's own /usage here and posts the figures. When a session starts and after each
# turn, in the background, at most every 3 minutes.
helper = os.path.join(os.path.dirname(path), "flex-screen-usage.sh")
HELPER = r'''#!/bin/sh
# flex-screen: the Claude plan's usage for the deck's Usage key ("5h 42%"), added by
# remote-claude-hooks.sh. Claude Code gives the 5-hour figure to no hook, so this runs
# Claude Code's own /usage here, headless (no model call, no transcript, hooks off), and
# posts the figures to the deck's PC through the SSH tunnel. The SessionStart and Stop hooks
# run it in the background; it goes at most every 3 minutes.
url="$1"
stamp="$(dirname "$0")/.flex-screen-usage-at"
now=$(date +%s)
last=$(cat "$stamp" 2>/dev/null)
case "$last" in ''|*[!0-9]*) last=0 ;; esac
[ $((now - last)) -ge 180 ] || exit 0
echo "$now" > "$stamp"

# Claude Code itself: the one running the hook, else one on the PATH, else VS Code's.
exe="${CLAUDE_CODE_EXECPATH:-}"
[ -n "$exe" ] && [ -x "$exe" ] || exe=$(command -v claude 2>/dev/null || true)
[ -n "$exe" ] && [ -x "$exe" ] || exe="$HOME/.local/bin/claude"
[ -x "$exe" ] || exe=$(ls -d "$HOME"/.vscode-server/extensions/anthropic.claude-code-*/resources/native-binary/claude 2>/dev/null | sort -V | tail -n 1)
[ -n "$exe" ] && [ -x "$exe" ] || exit 0

# As if run from a plain shell: nothing of the session that ran the hook.
for v in $(env | sed -n 's/^\(\(CLAUDE\|ANTHROPIC\)[A-Za-z0-9_]*\)=.*/\1/p'); do
  [ "$v" = CLAUDE_CONFIG_DIR ] || unset "$v"
done
cd /tmp 2>/dev/null || cd "$HOME" || exit 0
out=$(timeout 90 "$exe" -p /usage --no-session-persistence --settings '{"disableAllHooks":true}' 2>/dev/null) || exit 0
five=$(printf '%s\n' "$out" | sed -n 's/.*Current session: *\([0-9][0-9.]*\)% *used.*/\1/p' | head -n 1)
[ -n "$five" ] || exit 0
week=$(printf '%s\n' "$out" | sed -n 's/.*Current week[^:]*: *\([0-9][0-9.]*\)% *used.*/\1/p' | head -n 1)
more=""
[ -z "$week" ] || more=",\"seven_day\":$week"
printf '{"five_hour":%s%s}' "$five" "$more" |
  curl -s -m 2 -X POST -H 'Content-Type: application/json' --data-binary @- "$url/usage" >/dev/null 2>&1
exit 0
'''
USAGE = {"type": "command", "command": "sh '" + helper + "' " + url, "async": True, "timeout": 120}

for name in events:
    entries = [e for e in hooks.get(name, []) if not ours(e)]
    if mode != "--remove":
        # One command line, run by the shell: every Claude Code version on Linux takes that
        # (the separate "args" list the Windows companion writes is a newer form). In the
        # background, giving up after 2 s, so a missing tunnel costs nothing.
        hook = {"type": "command", "command": POST + url, "async": True, "timeout": 5}
        if name == "PermissionRequest":
            # Waits for Approve or Deny on the deck while Claude's own prompt shows as usual;
            # whichever answers first wins. No tunnel: curl fails at once, and "|| true"
            # keeps that from showing as a hook error.
            hook = {"type": "command",
                    "command": "curl -s -m 590 -X POST -H 'Content-Type: application/json' --data-binary @- "
                               + url + "/permission || true",
                    "timeout": 600}
        entry = {"matcher": "*"} if name in ("PreToolUse", "PostToolUse") else {}
        entry["hooks"] = [hook, USAGE] if name in ("SessionStart", "Stop") else [hook]
        entries.append(entry)
    if entries:
        hooks[name] = entries
    else:
        hooks.pop(name, None)
if not hooks:
    root.pop("hooks")

os.makedirs(os.path.dirname(path), exist_ok=True)
backup = path + ".before-flex-screen"
if os.path.exists(path) and not os.path.exists(backup):
    shutil.copy2(path, backup)
tmp = path + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump(root, f, indent=2)
    f.write("\n")
os.replace(tmp, path)
if mode == "--remove":
    for leftover in (helper, os.path.join(os.path.dirname(path), ".flex-screen-usage-at")):
        if os.path.exists(leftover):
            os.remove(leftover)
else:
    with open(helper, "w", encoding="utf-8", newline="\n") as f:
        f.write(HELPER)
    os.chmod(helper, 0o755)
print(("Removed the flex-screen hooks from " if mode == "--remove" else "Added the flex-screen hooks to ") + path)
EOF
