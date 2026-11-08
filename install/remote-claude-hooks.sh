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
events = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"]

root = {}
if os.path.exists(path):
    text = open(path, encoding="utf-8").read()
    if text.strip():
        root = json.loads(text)  # a file that doesn't parse stops here, untouched
hooks = root.setdefault("hooks", {})

def ours(entry):
    return any(url in (h.get("args") or []) for h in entry.get("hooks", []))

for name in events:
    entries = [e for e in hooks.get(name, []) if not ours(e)]
    if mode != "--remove":
        # As the Windows companion writes them: curl run directly, in the background.
        hook = {"type": "command", "command": "curl",
                "args": ["-s", "-m", "2", "-X", "POST", "-H", "Content-Type: application/json",
                         "--data-binary", "@-", url],
                "async": True, "timeout": 5}
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
