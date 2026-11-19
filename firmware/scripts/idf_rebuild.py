# Helpers for env:deck's ESP-IDF rebuild (custom_sdkconfig, pioarduino's hybrid compile).
#
# 1. The linker-script step (ldgen) gets every linker fragment's path on its command line:
#    about 10,700 characters, and cmd.exe stops at 8191, so on Windows that step fails. This
#    patches the platform's espidf.py, once, to pass the list through a file instead.
# 2. pioarduino keeps the hash of the settings it rebuilt ESP-IDF with in the project's
#    sdkconfig.defaults. A checkout without that file (a new worktree, a fresh clone) makes
#    it reinstall the whole Arduino framework (a download) and rebuild (~15 min), even when
#    the shared libraries were rebuilt with the same settings. So the file is kept next to
#    those libraries after a build and copied into a checkout that has none; pioarduino
#    still compares the hash and rebuilds if the settings differ.
import atexit
import shutil
from pathlib import Path

Import("env")  # noqa: F821

platform = env.PioPlatform()  # noqa: F821
project_dir = Path(env.subst("$PROJECT_DIR"))  # noqa: F821

MARK = "flex-screen: ldgen list through a file"
ANCHOR = (
    "    cmd = (\n"
    "        '\"$ESPIDF_PYTHONEXE\" \"{script}\" --input $SOURCE '\n"
    "        '--config \"{config}\" --fragments {fragments} --output $TARGET '"
)
PATCH = (
    "    # " + MARK + " (firmware/scripts/idf_rebuild.py): cmd.exe caps a command at\n"
    "    # 8191 characters and the fragment list alone is ~10 K.\n"
    "    os.makedirs(BUILD_DIR, exist_ok=True)\n"
    "    frag_file = str(Path(BUILD_DIR) / \"ldgen_fragments.txt\")\n"
    "    with open(frag_file, \"w\") as fp:\n"
    "        fp.write(\";\".join(fs.to_unix_path(f) for f in linker_script_fragments))\n"
    "    wrapper = str(Path(BUILD_DIR) / \"ldgen_from_file.py\")\n"
    "    with open(wrapper, \"w\") as fp:\n"
    "        fp.write(\n"
    "            \"import os, runpy, sys\\n\"\n"
    "            \"script = sys.argv[1]; args = sys.argv[2:]\\n\"\n"
    "            \"i = args.index('--fragments-file')\\n\"\n"
    "            \"args[i:i + 2] = ['--fragments-list', open(args[i + 1]).read()]\\n\"\n"
    "            \"sys.path.insert(0, os.path.dirname(script))\\n\"\n"
    "            \"sys.argv = [script] + args\\n\"\n"
    "            \"runpy.run_path(script, run_name='__main__')\\n\"\n"
    "        )\n"
    "    args[\"fragments\"] = '\"%s\"' % fs.to_unix_path(frag_file)\n"
    "    args[\"script\"] = '%s\" \"%s' % (wrapper, args[\"script\"])\n"
    "\n" + ANCHOR.replace("--fragments {fragments}", "--fragments-file {fragments}")
)


def patch_ldgen():
    path = Path(platform.get_dir()) / "builder" / "frameworks" / "espidf.py"
    if not path.is_file():
        return
    raw = path.read_bytes().decode("utf-8")
    crlf = "\r\n" in raw
    text = raw.replace("\r\n", "\n")
    if MARK in text:
        return
    if ANCHOR not in text:
        print("idf_rebuild: espidf.py has changed; the ldgen command isn't patched")
        return
    text = text.replace(ANCHOR, PATCH, 1)
    path.write_bytes((text.replace("\n", "\r\n") if crlf else text).encode("utf-8"))
    print("idf_rebuild: patched the platform's ldgen step (long command line)")


def libs_dir():
    d = platform.get_package_dir("framework-arduinoespressif32-libs")
    return Path(d) if d else None


def seed_path(libs):
    return libs / "flex-screen.sdkconfig.defaults"


def seed_checkout():
    libs = libs_dir()
    mine = project_dir / "sdkconfig.defaults"
    if libs and not mine.exists() and (libs / "sdkconfig").exists() and seed_path(libs).exists():
        shutil.copyfile(seed_path(libs), mine)
        print("idf_rebuild: using the ESP-IDF libraries already rebuilt for these settings")


def keep_seed():
    libs = libs_dir()
    mine = project_dir / "sdkconfig.defaults"
    try:
        if libs and (libs / "sdkconfig").exists() and mine.exists():
            with open(mine, encoding="utf-8") as f:
                if f.readline().startswith("# TASMOTA__"):
                    shutil.copyfile(mine, seed_path(libs))
    except OSError:
        pass


patch_ldgen()
seed_checkout()
atexit.register(keep_seed)
