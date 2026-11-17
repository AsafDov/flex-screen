"""Drives the deck simulator from Python: plays the companion's side of the link.

    from deck import Deck
    with Deck.launch() as d:          # starts build/flex-screen-sim.exe --headless
        d.sync()
        d.state('media', {...})
        d.tap(400, 240)
        d.shot('out/today.png')
        assert d.took('media.toggle')

Messages are protocol/v0.md's; the simulator adds "sim.touch" (a finger), "sim.ping" (to
wait until the screen holds still) and reports the deck's USB keys as "sim.key" /
"sim.media" / "sim.type". Only the standard library, so PlatformIO's Python runs it.
"""
from __future__ import annotations

import base64
import json
import os
import socket
import struct
import subprocess
import threading
import time
import zlib

# Windows can reserve a range of ports (Hyper-V, WinNAT) that takes 48160 in: set
# FLEX_SIM_PORT to another then ("can't listen on port").
PORT = int(os.environ.get('FLEX_SIM_PORT', '48160'))

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, 'build', 'flex-screen-sim.exe')
W, H = 800, 480


def write_png(path: str, rgb565: bytes, w: int = W, h: int = H) -> None:
    """RGB565 little-endian, row by row (the deck's frame buffer) to a PNG file."""
    rows = bytearray()
    for y in range(h):
        rows.append(0)  # no filter
        for x in range(w):
            p = rgb565[2 * (y * w + x)] | rgb565[2 * (y * w + x) + 1] << 8
            r, g, b = p >> 11 & 31, p >> 5 & 63, p & 31
            rows += bytes((r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)

    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(rows), 6)) + chunk(b'IEND', b'')
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(png)


def rgb565(r: int, g: int, b: int) -> int:
    return (r >> 3) << 11 | (g >> 2) << 5 | b >> 3


class Deck:
    def __init__(self, port: int = PORT, process: subprocess.Popen | None = None, log_path: str | None = None):
        self.port = port
        self.process = process
        self.log_path = log_path
        self.sock: socket.socket | None = None
        self.lock = threading.Condition()
        self.messages: list[dict] = []  # everything the deck sent, in order
        self.shot_parts: dict[int, bytes] = {}
        self.shot_count = 0
        self.connect()

    # ---- process and connection

    @classmethod
    def launch(cls, port: int = PORT, headless: bool = True, log_path: str | None = None) -> 'Deck':
        args = [EXE, '--port', str(port)] + (['--headless'] if headless else [])
        log = open(log_path, 'w') if log_path else subprocess.DEVNULL
        process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, cwd=HERE)
        return cls(port, process, log_path)

    def connect(self, timeout: float = 10) -> None:
        end = time.time() + timeout
        while True:
            try:
                self.sock = socket.create_connection(('127.0.0.1', self.port), timeout=2)
                break
            except OSError:
                if time.time() > end:
                    raise
                time.sleep(0.1)
        self.sock.settimeout(None)
        threading.Thread(target=self._read, args=(self.sock,), daemon=True).start()
        threading.Thread(target=self._keepalive, args=(self.sock,), daemon=True).start()

    quiet = False  # True: no pings, so the deck decides the PC is gone (5 s)

    def _keepalive(self, sock: socket.socket) -> None:
        """The companion pings every 2 s; after 5 s without a word the deck says "PC offline"."""
        while self.sock is sock:
            if not self.quiet:
                try:
                    self.send({'t': 'ping'})
                except OSError:
                    return
            time.sleep(2)

    def offline(self) -> None:
        """Stops talking until the deck counts the PC as gone, as if the companion quit."""
        self.quiet = True
        time.sleep(5.5)
        self.settle()

    def online(self) -> None:
        self.quiet = False
        self.send({'t': 'ping'})

    def reconnect(self, timeout: float = 15) -> None:
        """After the deck restarted itself (a palette change)."""
        try:
            self.sock.close()
        except OSError:
            pass
        time.sleep(0.5)
        self.connect(timeout)

    def close(self) -> None:
        try:
            self.send({'t': 'sim.quit'})
        except OSError:
            pass
        if self.process:
            try:
                self.process.wait(5)
            except subprocess.TimeoutExpired:
                self.process.kill()
        # A palette change restarts the simulator as a new process: end that one too.
        subprocess.run(['taskkill', '/IM', 'flex-screen-sim.exe', '/F'], capture_output=True)

    def __enter__(self) -> 'Deck':
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def alive(self) -> bool:
        return self.sock is not None and (self.process is None or self.process.poll() is None or self.restarted)

    restarted = False

    def _read(self, sock: socket.socket) -> None:
        buffer = b''
        while True:
            try:
                data = sock.recv(65536)
            except OSError:
                return
            if not data:
                return
            buffer += data
            while b'\n' in buffer:
                line, buffer = buffer.split(b'\n', 1)
                try:
                    message = json.loads(line)
                except ValueError:
                    continue
                with self.lock:
                    if message.get('t') == 'shot':
                        self.shot_parts[message['i']] = base64.b64decode(message['d'])
                        self.shot_count = message['n']
                    else:
                        self.messages.append(message)
                    self.lock.notify_all()

    # ---- sending

    def send(self, message: dict) -> None:
        data = json.dumps(message, ensure_ascii=False, separators=(',', ':')).encode() + b'\n'
        with self.send_lock:
            self.sock.sendall(data)

    send_lock = threading.Lock()  # the keep-alive thread sends too

    def sync(self) -> dict:
        """Says what the companion says on connecting; returns the deck's hello."""
        mark = self.mark()
        self.send({'t': 'sync'})
        hello = self.wait_for(lambda m: m.get('t') == 'hello', since=mark)
        return hello

    def state(self, topic: str, d: dict) -> None:
        self.send({'t': 'state', 'topic': topic, 'd': d})

    def settings(self, palette: str = 'base', clock_s: int = 600, off_s: int = 1800, cheat_s: int = 10) -> None:
        self.send({'t': 'settings', 'palette': palette, 'idle_clock_s': clock_s, 'idle_off_s': off_s, 'cheat_hold_s': cheat_s})

    def pages(self, ids: list[str], palette: str = 'base') -> dict:
        """The profile's pages, as the companion sends them: the deck saves them and restarts into
        them (as for a palette). Reconnects, and returns the new hello."""
        self.send({'t': 'settings', 'palette': palette, 'idle_clock_s': 600, 'idle_off_s': 1800, 'cheat_hold_s': 10, 'pages': ids})
        self.restarted = True
        time.sleep(1.0)
        self.reconnect()
        return self.sync()

    @staticmethod
    def forget_pages() -> None:
        """Back to the six pages as they always were, before the simulator starts: a run that
        stopped halfway through the pages test left its order saved."""
        path = os.path.join(os.path.dirname(EXE), 'sim-prefs.txt')
        try:
            lines = open(path, encoding='utf-8').read().splitlines()
        except OSError:
            return
        kept = [line for line in lines if not line.startswith('deck.pages=')]
        with open(path, 'w', encoding='utf-8') as f:
            f.writelines(line + '\n' for line in kept)

    def clock(self, epoch: int, tz_minutes: int = 120) -> None:
        self.send({'t': 'time', 'epoch': epoch, 'tz': tz_minutes})

    def page(self, i: int) -> None:
        """Shows a page. The simulator's own message, so (unlike the companion's "page") it
        doesn't count as the PC being there: an offline screen stays offline."""
        self.send({'t': 'sim.page', 'i': i})

    def toast(self, text: str, level: str = 'ok', action: str = '', id: str = '') -> None:
        self.send({'t': 'toast', 'level': level, 'text': text, 'action': action, 'id': id})

    def icon(self, id: str, color: tuple[int, int, int], letter_bar: bool = True) -> None:
        """A 32x32 RGB565A8 window icon: a rounded square in that color."""
        px = bytearray()
        alpha = bytearray()
        for y in range(32):
            for x in range(32):
                inside = (min(x, 31 - x) + min(y, 31 - y)) >= 3
                c = color if not (letter_bar and 12 <= x <= 19 and 8 <= y <= 23) else (255, 255, 255)
                px += struct.pack('<H', rgb565(*c))
                alpha.append(255 if inside else 0)
        self.send({'t': 'img', 'kind': 'icon', 'id': id, 'w': 32, 'h': 32, 'fmt': 'rgb565a8', 'part': 0, 'parts': 1,
                   'off': 0, 'b64': base64.b64encode(bytes(px + alpha)).decode()})

    def art(self, id: str, size: int = 180) -> None:
        """Album art: a diagonal gradient, in 3 KB parts as the companion sends it."""
        px = bytearray()
        for y in range(size):
            for x in range(size):
                px += struct.pack('<H', rgb565(40 + x * 200 // size, 30 + y * 120 // size, 160 - (x + y) * 60 // size))
        parts = (len(px) + 3071) // 3072
        for part in range(parts):
            chunk = px[part * 3072:(part + 1) * 3072]
            self.send({'t': 'img', 'id': id, 'w': size, 'h': size, 'fmt': 'rgb565', 'part': part, 'parts': parts,
                       'off': part * 3072, 'b64': base64.b64encode(bytes(chunk)).decode()})
            time.sleep(0.005)

    def load(self, topics: dict[str, dict], icons: dict[str, tuple] | None = None, now: int | None = None,
             tz_minutes: int = 120) -> None:
        """What the companion sends after hello: time, images first, then every topic."""
        if now:
            self.clock(now, tz_minutes)
        for id, color in (icons or {}).items():
            self.icon(id, color)
        art = topics.get('media', {}).get('art')
        if art:
            self.art(art)
        for topic, d in topics.items():
            self.state(topic, d)
            time.sleep(0.01)
        self.settle()

    # ---- touch

    def touch(self, x: int, y: int, down: bool) -> None:
        self.send({'t': 'sim.touch', 'x': x, 'y': y, 'down': down})

    def tap(self, x: int, y: int, ms: int = 80) -> None:
        self.touch(x, y, True)
        time.sleep(ms / 1000)
        self.touch(x, y, False)
        self.settle()

    def hold(self, x: int, y: int, ms: int = 900) -> None:
        self.touch(x, y, True)
        time.sleep(ms / 1000)
        self.touch(x, y, False)
        self.settle()

    def drag(self, x0: int, y0: int, x1: int, y1: int, steps: int = 12, ms: int = 300) -> None:
        # Down and still for a moment first, as a finger is: moving at once sometimes left the
        # deck's touch reading only the moved points (a fader drag went unnoticed in 1 of 5 runs).
        self.touch(x0, y0, True)
        time.sleep(0.1)
        for i in range(steps + 1):
            self.touch(x0 + (x1 - x0) * i // steps, y0 + (y1 - y0) * i // steps, True)
            time.sleep(ms / 1000 / steps)
        self.touch(x1, y1, False)
        self.settle()

    # ---- waiting and reading

    def mark(self) -> int:
        with self.lock:
            return len(self.messages)

    def wait_for(self, match, since: int = 0, timeout: float = 5) -> dict:
        end = time.time() + timeout
        with self.lock:
            while True:
                for m in self.messages[since:]:
                    if match(m):
                        return m
                left = end - time.time()
                if left <= 0:
                    raise TimeoutError('no matching message from the deck')
                self.lock.wait(left)

    def ping(self) -> dict:
        mark = self.mark()
        token = int(time.time() * 1000) % 1000000
        self.send({'t': 'sim.ping', 'id': token})
        return self.wait_for(lambda m: m.get('t') == 'sim.pong' and m.get('id') == token, since=mark)

    def settle(self, least: float = 0.15, quiet: float = 0.25, timeout: float = 5) -> None:
        """Waits until no new frame has been drawn for `quiet` seconds (animations done)."""
        time.sleep(least)
        end = time.time() + timeout
        last = self.ping()['frames']
        still_since = time.time()
        while time.time() < end:
            time.sleep(0.05)
            frames = self.ping()['frames']
            if frames != last:
                last, still_since = frames, time.time()
            elif time.time() - still_since >= quiet:
                return

    def tree(self) -> list[dict]:
        """Every object on the screen and the top layer, depth first: d, c (class), x, y, w, h,
        hidden, text (labels). Hidden objects' children are listed too (they don't show)."""
        self.settle()
        mark = self.mark()
        token = int(time.time() * 1000) % 1000000
        self.send({'t': 'sim.dump', 'id': token})
        reply = self.wait_for(lambda m: m.get('t') == 'sim.tree' and m.get('id') == token, since=mark)
        return reply['screen'] + reply['top']

    def find(self, text: str, visible: bool = True) -> list[dict]:
        """Labels whose text contains `text` (only those actually showing, by default)."""
        nodes, hidden_depth, found = self.tree(), None, []
        for n in nodes:
            if hidden_depth is not None and n['d'] > hidden_depth:
                continue
            hidden_depth = n['d'] if n.get('hidden') else None
            if hidden_depth is not None and visible:
                continue
            if text in n.get('text', ''):
                found.append(n)
        return found

    def acts(self, since: int = 0) -> list[dict]:
        with self.lock:
            return [m for m in self.messages[since:] if m.get('t') == 'act']

    def took(self, action: str, since: int = 0, **fields) -> dict | None:
        """The first act of that name since `since` whose fields match, or None."""
        for m in self.acts(since):
            if m.get('a') == action and all(m.get(k) == v for k, v in fields.items()):
                return m
        return None

    def keys(self, since: int = 0) -> list[dict]:
        with self.lock:
            return [m for m in self.messages[since:] if m.get('t', '').startswith('sim.') and m['t'] != 'sim.pong']

    def logs(self, since: int = 0) -> list[str]:
        with self.lock:
            return [m.get('msg', '') for m in self.messages[since:] if m.get('t') == 'log']

    def shot(self, path: str, timeout: float = 10) -> None:
        """Saves the screen as it is now as a PNG."""
        self.settle()
        with self.lock:
            self.shot_parts.clear()
            self.shot_count = 0
        self.send({'t': 'sim.shot'})  # like page(): not a sign of the PC
        end = time.time() + timeout
        with self.lock:
            while not self.shot_count or len(self.shot_parts) < self.shot_count:
                left = end - time.time()
                if left <= 0:
                    raise TimeoutError(f'shot: {len(self.shot_parts)} of {self.shot_count} parts')
                self.lock.wait(left)
            data = b''.join(self.shot_parts[i] for i in range(self.shot_count))
        write_png(path, data)
