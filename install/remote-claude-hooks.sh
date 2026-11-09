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
        entry["hooks"] = [hook]
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
print(("Removed the flex-screen hooks from " if mode == "--remove" else "Added the flex-screen hooks to ") + path)
EOF
