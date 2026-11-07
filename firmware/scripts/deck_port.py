# Upload helpers for `pio run -t upload`.
#
# The companion app holds the deck's serial port open, which would block any upload, so
# it's stopped for the upload and started again afterwards.
#
# For the esptool (recovery) environment, this also puts the deck into its ROM bootloader:
# a 1200-baud open of the deck's serial link (1209:0001) reboots it there, and it comes
# back as Espressif's USB-Serial-JTAG port (303A:1001). PlatformIO's own touch handed that
# port to esptool the instant it appeared and esptool then reset the chip again; that
# combination kept wedging the port until a replug, so this does the touch itself, lets
# the port settle, and has esptool skip its reset.
Import("env")  # noqa: F821 (provided by SCons)

import glob  # noqa: E402
import os  # noqa: E402
import subprocess  # noqa: E402
import time  # noqa: E402

from SCons.Script import COMMAND_LINE_TARGETS  # noqa: E402

DECK = (0x1209, 0x0001)
ESP_BOOTLOADER = (0x303A, 0x1001)
COMPANION_EXE = "flex-screen-companion.exe"


def _find(vid_pid):
    from serial.tools import list_ports

    return next((p.device for p in list_ports.comports() if (p.vid, p.pid) == vid_pid), None)


def _stop_companion():
    result = subprocess.run(["taskkill", "/IM", COMPANION_EXE, "/F"], capture_output=True)
    if result.returncode == 0:
        time.sleep(0.5)  # let Windows release the port
    return result.returncode == 0


def _start_companion(source, target, env):
    project = env.subst("$PROJECT_DIR")
    builds = glob.glob(os.path.join(project, "..", "companion", "bin", "*", "*", COMPANION_EXE))
    if not builds:
        print("deck_port: companion build not found; start it yourself")
        return
    exe = os.path.normpath(max(builds, key=os.path.getmtime))
    # Break away from the job PlatformIO runs in, or the companion dies with the upload.
    flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    try:
        subprocess.Popen([exe], cwd=os.path.dirname(exe), creationflags=flags | subprocess.CREATE_BREAKAWAY_FROM_JOB)
    except OSError:  # the job doesn't allow breakaway
        subprocess.Popen([exe], cwd=os.path.dirname(exe), creationflags=flags)
    print(f"deck_port: restarted the companion ({exe})")


def _enter_bootloader(source, target, env):
    import serial

    bootloader = _find(ESP_BOOTLOADER)
    if not bootloader:
        deck = _find(DECK)
        if not deck:
            print("deck_port: no deck or bootloader port; hold BOOT, tap RESET, release BOOT, or pass --upload-port")
            env.Exit(1)
        print(f"deck_port: rebooting the deck on {deck} into its bootloader")
        try:
            serial.Serial(deck, 1200).close()
        except serial.SerialException:
            pass  # it reboots mid-open
        deadline = time.time() + 10
        while not bootloader and time.time() < deadline:
            time.sleep(0.25)
            bootloader = _find(ESP_BOOTLOADER)
        if not bootloader:
            print("deck_port: the bootloader port didn't appear")
            env.Exit(1)
    time.sleep(1.5)  # let Windows finish setting the new port up before esptool writes
    print(f"deck_port: bootloader on {bootloader}")
    env.Replace(UPLOAD_PORT=bootloader)
    env.Replace(UPLOADERFLAGS=["no-reset" if f in ("default-reset", "default_reset") else f
                               for f in env["UPLOADERFLAGS"]])


if "upload" in COMMAND_LINE_TARGETS:
    if _stop_companion():
        print("deck_port: stopped the companion for the upload")
        env.AddPostAction("upload", _start_companion)  # noqa: F821
    if env.GetProjectOption("upload_protocol") == "esptool" and not env.subst("$UPLOAD_PORT"):  # noqa: F821
        env.AddPreAction("upload", _enter_bootloader)  # noqa: F821
