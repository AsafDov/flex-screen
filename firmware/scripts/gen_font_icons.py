"""Generates src/fonts/font_icons.c: the context bar's key icons, by name.

LVGL's symbols are a few dozen FontAwesome glyphs baked into Montserrat; the context bar
needs others (search, split, history, terminal…). This renders them from the FontAwesome 5
file LVGL ships (the one its symbols come from) at 15 px into LVGL's plain 4 bpp font
format, plus a table of names, so the companion can name an icon ("search") and the deck
look it up. "split-down" is "split-right" turned a quarter.

Run from firmware/ with any Python that has Pillow:
    python scripts/gen_font_icons.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

TTF = Path(".pio/libdeps/deck/lvgl/scripts/generators/built_in_font/FontAwesome5-Solid+Brands+Regular.woff")
OUT = Path("src/fonts/font_icons.c")
SIZE = 15
LINE_HEIGHT, BASE_LINE = 16, 2  # FontAwesome's em box: 7/8 above the baseline, 1/8 below
ROTATED = 0xE000                # where split-down goes (a private-use code point FontAwesome leaves free)

ICONS = {
    "play": 0xF04B, "pause": 0xF04C, "stop": 0xF04D, "prev-track": 0xF048, "next-track": 0xF051,
    "shuffle": 0xF074, "music": 0xF001, "terminal": 0xF120, "keyboard": 0xF11C, "code": 0xF121,
    "file": 0xF15B, "folder": 0xF07C, "folder-plus": 0xF65E, "search": 0xF002,
    "split-right": 0xF0DB, "split-down": ROTATED, "window": 0xF2D0, "expand": 0xF065,
    "back": 0xF060, "forward": 0xF061, "up": 0xF062, "down": 0xF063, "prev": 0xF053, "next": 0xF054,
    "reload": 0xF01E, "undo": 0xF0E2, "redo": 0xF2F9, "history": 0xF1DA, "plus": 0xF067,
    "minus": 0xF068, "close": 0xF00D, "check": 0xF00C, "copy": 0xF0C5, "paste": 0xF0EA,
    "cut": 0xF0C4, "save": 0xF0C7, "edit": 0xF044, "trash": 0xF1F8, "calendar": 0xF783,
    "graph": 0xF542, "list": 0xF03A, "bookmark": 0xF02E, "star": 0xF005, "heart": 0xF004,
    "home": 0xF015, "link": 0xF0C1, "external": 0xF35D, "lock": 0xF023, "settings": 0xF013,
    "image": 0xF03E, "camera": 0xF030, "share": 0xF14D, "screen": 0xF108, "download": 0xF019,
    "upload": 0xF093, "print": 0xF02F, "eye": 0xF06E, "bolt": 0xF0E7, "mic": 0xF130,
    "mic-off": 0xF131, "video": 0xF03D, "hand": 0xF256, "phone": 0xF095, "leave": 0xF3DD,
    "chat": 0xF086, "comment": 0xF075, "bell": 0xF0F3, "headphones": 0xF025, "hashtag": 0xF292,
    "switch": 0xF362, "unread": 0xF103, "at": 0xF1FA, "bug": 0xF188, "branch": 0xF126,
    "cursor": 0xF246, "format": 0xF036, "line": 0xF0CB, "target": 0xF05B, "bars": 0xF0C9,
    "pin": 0xF08D, "smile": 0xF118, "users": 0xF0C0, "zoom-in": 0xF00E, "zoom-out": 0xF010,
    "tab": 0xF2D2, "moon": 0xF186,
}


def render(font: ImageFont.FreeTypeFont, cp: int):
    """Returns (adv_w in 1/16 px, box_w, box_h, ofs_x, ofs_y, 4-bit pixels row by row)."""
    source = ICONS["split-right"] if cp == ROTATED else cp
    ch = chr(source)
    adv = round(font.getlength(ch) * 16)
    canvas = Image.new("L", (SIZE * 4, SIZE * 4), 0)
    ox, oy = SIZE, SIZE * 3  # pen position: left edge, baseline
    ImageDraw.Draw(canvas).text((ox, oy), ch, font=font, fill=255, anchor="ls")
    box = canvas.getbbox()
    if box is None:
        raise SystemExit(f"U+{source:04X} is empty in {TTF.name}")
    left, top, right, bottom = box
    glyph = canvas.crop(box)
    ofs_x, ofs_y = left - ox, oy - bottom
    if cp == ROTATED:
        middle = oy - (top + bottom) / 2  # keep it as high above the baseline as the original
        glyph = glyph.rotate(90, expand=True)
        ofs_x = 0
        ofs_y = round(middle - glyph.height / 2)
        adv = max(adv, (glyph.width + 1) * 16)
    w, h = glyph.size
    pixels = [(glyph.getpixel((x, y)) + 8) // 17 for y in range(h) for x in range(w)]
    return adv, w, h, ofs_x, ofs_y, pixels


def pack(pixels: list[int]) -> list[int]:
    """LVGL's plain format: one continuous stream of nibbles, high nibble first, no row padding."""
    if len(pixels) % 2:
        pixels = pixels + [0]
    return [(pixels[i] << 4) | pixels[i + 1] for i in range(0, len(pixels), 2)]


def utf8(cp: int) -> str:
    return "".join(f"\\x{b:02X}" for b in chr(cp).encode("utf-8"))


def main() -> None:
    font = ImageFont.truetype(str(TTF), SIZE)
    codepoints = sorted(set(ICONS.values()))
    bitmap: list[int] = []
    glyphs = ["    {.bitmap_index = 0, .adv_w = 0, .box_w = 0, .box_h = 0, .ofs_x = 0, .ofs_y = 0} /* id = 0 reserved */,"]
    for cp in codepoints:
        adv, w, h, ox, oy, pixels = render(font, cp)
        glyphs.append(f"    {{.bitmap_index = {len(bitmap)}, .adv_w = {adv}, .box_w = {w}, .box_h = {h}, "
                      f".ofs_x = {ox}, .ofs_y = {oy}}}, /* U+{cp:04X} */")
        bitmap += pack(pixels)
    start = codepoints[0]
    offsets = [cp - start for cp in codepoints]
    rows = lambda values, width, digits: "\n".join(
        "    " + ", ".join(f"0x{v:0{digits}x}" for v in values[i:i + width]) + "," for i in range(0, len(values), width))
    names = "\n".join(f'    {{"{name}", "{utf8(cp)}"}},' for name, cp in sorted(ICONS.items()))
    text = f"""/* Generated by scripts/gen_font_icons.py from LVGL's FontAwesome 5; don't edit by hand.
 * The context bar's key icons at {SIZE} px, and their names. */
#include "font_icons.h"

static const uint16_t unicode_list[] = {{
{rows(offsets, 12, 4)}
}};

static const uint8_t bitmap[] = {{
{rows(bitmap, 16, 2)}
}};

static const lv_font_fmt_txt_glyph_dsc_t glyphs[] = {{
{chr(10).join(glyphs)}
}};

static const lv_font_fmt_txt_cmap_t cmaps[] = {{
    {{.range_start = {start}, .range_length = {codepoints[-1] - start + 1}, .glyph_id_start = 1,
     .unicode_list = unicode_list, .glyph_id_ofs_list = NULL, .list_length = {len(offsets)},
     .type = LV_FONT_FMT_TXT_CMAP_SPARSE_TINY}},
}};

static const lv_font_fmt_txt_dsc_t dsc = {{
    .glyph_bitmap = bitmap,
    .glyph_dsc = glyphs,
    .cmaps = cmaps,
    .kern_dsc = NULL,
    .kern_scale = 0,
    .cmap_num = 1,
    .bpp = 4,
    .kern_classes = 0,
    .bitmap_format = 0,
}};

const lv_font_t font_icons_{SIZE} = {{
    .get_glyph_dsc = lv_font_get_glyph_dsc_fmt_txt,
    .get_glyph_bitmap = lv_font_get_bitmap_fmt_txt,
    .line_height = {LINE_HEIGHT},
    .base_line = {BASE_LINE},
    .subpx = LV_FONT_SUBPX_NONE,
    .underline_position = -1,
    .underline_thickness = 1,
    .static_bitmap = 1,
    .dsc = &dsc,
    .fallback = NULL,
    .user_data = NULL,
}};

/* By name, sorted, for font_icon(). */
const font_icon_t font_icon_names[] = {{
{names}
}};
const int font_icon_count = {len(ICONS)};
"""
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"{OUT}: {len(ICONS)} icons at {SIZE} px")


if __name__ == "__main__":
    main()
