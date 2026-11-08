"""Puts a set's six page screenshots on one sheet (3 x 2), for looking over several at once.

    python sheet.py day-ember        # out/tour/day-ember-*.png -> out/tour/sheet-day-ember.png
"""
import os
import struct
import sys
import zlib

from deck import HERE

PAGES = ['today', 'audio', 'dev', 'windows', 'capture', 'numpad']
TOUR = os.path.join(HERE, 'out', 'tour')


def read_png(path: str) -> tuple[int, int, bytes]:
    """The PNGs deck.py writes: 8-bit RGB, filter 0 on every row."""
    data = open(path, 'rb').read()
    pos, idat, w, h = 8, b'', 0, 0
    while pos < len(data):
        n, kind = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + n]
        if kind == b'IHDR':
            w, h = struct.unpack('>II', body[:8])
        elif kind == b'IDAT':
            idat += body
        pos += 12 + n
    raw = zlib.decompress(idat)
    return w, h, b''.join(raw[y * (3 * w + 1) + 1:(y + 1) * (3 * w + 1)] for y in range(h))


def write_rgb_png(path: str, w: int, h: int, rgb: bytes) -> None:
    rows = b''.join(b'\0' + rgb[y * 3 * w:(y + 1) * 3 * w] for y in range(h))

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xFFFFFFFF)

    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) +
                           chunk(b'IDAT', zlib.compress(rows, 6)) + chunk(b'IEND', b''))


def sheet_files(paths: list[str], out_path: str) -> str:
    """Up to six screenshots, three to a row."""
    gap = 8
    w, h = 800, 480
    rows = (len(paths) + 2) // 3
    sw, sh = 3 * w + 2 * gap, rows * h + (rows - 1) * gap
    out = bytearray(b'\x40' * (sw * sh * 3))
    for i, path in enumerate(paths):
        _, _, px = read_png(path)
        ox, oy = (i % 3) * (w + gap), (i // 3) * (h + gap)
        for y in range(h):
            start = ((oy + y) * sw + ox) * 3
            out[start:start + 3 * w] = px[y * 3 * w:(y + 1) * 3 * w]
    write_rgb_png(out_path, sw, sh, bytes(out))
    return out_path


def sheet(prefix: str) -> str:
    return sheet_files([os.path.join(TOUR, f'{prefix}-{page}.png') for page in PAGES],
                       os.path.join(TOUR, f'sheet-{prefix}.png'))


if __name__ == '__main__':
    # Either tour prefixes ("day-base"), or "-o out.png" and up to six PNG files.
    if len(sys.argv) > 2 and sys.argv[1] == '-o':
        print(sheet_files(sys.argv[3:], sys.argv[2]))
    else:
        for prefix in sys.argv[1:]:
            print(sheet(prefix))
