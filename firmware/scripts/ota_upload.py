"""Uploads firmware to the deck over its own serial link (protocol/v0.md, "Firmware updates").

    python ota_upload.py firmware.bin

The running firmware writes the image into its spare app slot and restarts into it, so
this never touches the chip's ROM bootloader. PlatformIO runs it for `pio run -t upload`.
"""
import base64
import json
import sys
import time

import serial
from serial.tools import list_ports

DECK = (0x1209, 0x0001)
CHUNK = 4096  # one flash sector per line: 5.5 KB of base64, under the deck's 8 KB line limit


def offset_field(off, fw):
    """The offset to put in an ota_data message. Firmware 0.2.2 checked it against Arduino's
    Update progress, which trails one sector behind what's been sent, instead of its own
    count; it doesn't use the offset otherwise. 0.2.3 and later count bytes themselves."""
    return max(0, off - CHUNK) if fw == "0.2.2" else off


def find_deck(timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for p in list_ports.comports():
            if (p.vid, p.pid) == DECK:
                return p.device
        time.sleep(0.5)
    return None


def send(port, msg):
    port.write((json.dumps(msg, separators=(",", ":")) + "\n").encode())


def wait_for(port, kinds, timeout):
    """The next message of one of these types; anything else (logs, acts) is skipped."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        line = port.readline()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if msg.get("t") in kinds:
            return msg
    raise TimeoutError(f"the deck didn't send {' or '.join(sorted(kinds))} within {timeout} s")


def handshake(port):
    """Asks for hello until the deck answers. Unlike the companion, this doesn't drop and
    raise DTR/RTS: from pyserial on Windows that leaves the deck seeing the port as closed,
    and a plain open is enough once the deck has finished booting."""
    time.sleep(0.3)
    for _ in range(5):
        send(port, {"t": "sync"})
        try:
            return wait_for(port, {"hello"}, 2)
        except TimeoutError:
            pass
    raise TimeoutError("the deck didn't answer (is it running the flex-screen firmware?)")


def upload(path):
    data = open(path, "rb").read()
    name = find_deck(10)
    if not name:
        sys.exit("ota: deck not found (is it plugged in and running?). "
                 "For a deck that won't boot, use: pio run -e deck-esptool -t upload")
    with serial.Serial(name, 115200, timeout=0.5) as port:
        hello = handshake(port)
        print(f"ota: deck on {name}, firmware {hello.get('fw')}; sending {len(data)} bytes")

        send(port, {"t": "ota", "size": len(data)})
        msg = wait_for(port, {"ota_ready", "ota_error"}, 30)  # erasing the slot takes a moment
        if msg["t"] == "ota_error":
            sys.exit(f"ota: the deck refused: {msg.get('msg')}")

        started = time.time()
        shown = -1
        for off in range(0, len(data), CHUNK):
            send(port, {"t": "ota_data", "off": offset_field(off, hello.get("fw")),
                        "b64": base64.b64encode(data[off:off + CHUNK]).decode()})
            msg = wait_for(port, {"ota_ack", "ota_error"}, 10)
            if msg["t"] == "ota_error":
                sys.exit(f"ota: failed at byte {off}: {msg.get('msg')}")
            percent = min(100, (off + CHUNK) * 100 // len(data))
            if percent // 10 != shown:
                shown = percent // 10
                print(f"ota: {percent}%", flush=True)

        send(port, {"t": "ota_end"})
        msg = wait_for(port, {"ota_done", "ota_error"}, 20)
        if msg["t"] == "ota_error":
            sys.exit(f"ota: the deck rejected the image: {msg.get('msg')}")
        print(f"ota: done in {time.time() - started:.1f} s, the deck is restarting")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    try:
        upload(sys.argv[1])
    except (TimeoutError, serial.SerialException) as e:
        sys.exit(f"ota: {e}")
