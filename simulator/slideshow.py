"""The README's slideshow: docs/screens/slideshow.png, an animated PNG of the deck's features,
each screenshot with a caption under it.

It's put together from the simulator's screenshots, so take those first (PlatformIO's Python):

    python run_some.py test_today test_sessions test_approval test_question test_audio test_tile_drag test_tasks test_day test_call test_zoom test_context_bar test_launcher test_numpad
    python tour.py day base      # and the other themes: vibrant, ember, violet, pastel

then build it with a Python that has Pillow (pip install pillow); the captions use Windows'
Segoe UI:

    python slideshow.py          # -> docs/screens/slideshow.png
"""
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'out')
DEST = os.path.join(HERE, '..', 'docs', 'screens', 'slideshow.png')

# (screenshot, or several for a grid; title; one line under it)
SLIDES = [
    ('tests/today.png', 'Today', "The clock, what's playing, your next meeting and what you just did."),
    ('tests/sessions.png', 'Every Claude session at a glance',
     'The Claude app, VS Code, terminals and Remote-SSH, with your 5-hour usage.'),
    ('tests/approval.png', 'Approve Claude from the deck',
     "Claude Code's permission requests on any page: Approve, Always or Deny."),
    ('tests/question.png', 'Claude asks, you tap',
     "Claude's questions show with their options, and its notes when it's done."),
    ('tests/audio.png', 'Audio', 'Now playing with its progress, your speakers and mics, and a fader for every app.'),
    ('tour/day-base-dev.png', 'Dev', 'What Claude is doing, your recent terminal commands, git and the last build.'),
    ('tests/windows-tile-drag.png', 'Snap windows into zones',
     "Drag a window's tile onto the desktop map: into a FancyZones zone, or full screen."),
    ('tests/tasks.png', 'Tasks, clipboard and Obsidian',
     'Google Tasks, To Do, Jira and GitHub in one list, beside your clipboard and notes.'),
    ('tests/day.png', 'The whole day', "Tap Up next for today's events and tomorrow's, with Join on every meeting."),
    ('tests/call-teams.png', 'Calls', 'When an app is using the mic: mute, camera, raise hand, reactions and leave.'),
    ('tests/zoom-whiteboard.png', 'Draw in Zoom', "Pen, marker, laser and colors for Zoom's whiteboard and screen annotation."),
    ('tests/context-bar-chrome.png', 'Keys for the app in front',
     "The context bar follows the app or site on the PC. Here it's Chrome's."),
    ('tests/launcher.png', 'Shortcuts', 'Up to 12 apps, links and key combos, each with its own icon.'),
    ('tests/numpad.png', 'Numpad', 'A real USB numpad, or a calculator that types its result, over any page.'),
    ([f'tour/day-{p}-today.png' for p in ('base', 'vibrant', 'ember', 'violet', 'pastel')],
     'Five themes', 'Base, Vibrant, Ember, Violet and Pastel.'),
]

W, H = 800, 480  # the deck's screen
PAD = 14         # bezel around it
CAP = 92         # caption band under the bezel
FW, FH = W + 2 * PAD, H + 2 * PAD + CAP
SECONDS = 3.2    # per slide

BG, BEZEL, TITLE, TEXT, DOT_ON, DOT_OFF = (
    (13, 17, 23), (30, 34, 41), (240, 243, 246), (159, 167, 178), (240, 243, 246), (72, 79, 90))
FONTS = os.path.join(os.environ.get('WINDIR', r'C:\Windows'), 'Fonts')
TITLE_FONT = ImageFont.truetype(os.path.join(FONTS, 'seguisb.ttf'), 25)
TEXT_FONT = ImageFont.truetype(os.path.join(FONTS, 'segoeui.ttf'), 17)


def rounded(img: Image.Image, r: int) -> Image.Image:
    mask = Image.new('L', img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, img.size[0] - 1, img.size[1] - 1], r, fill=255)
    out = Image.new('RGBA', img.size, (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    return out


def screen(shot) -> Image.Image:
    if isinstance(shot, str):
        img = Image.open(os.path.join(OUT, shot)).convert('RGB')
        assert img.size == (W, H), (shot, img.size)
        return img
    # Several on one screen, three to a row, each row centered.
    img = Image.new('RGB', (W, H), BEZEL)
    cols, gap = 3, 14
    tw = (W - gap * (cols + 1)) // cols
    th = tw * H // W
    rows = (len(shot) + cols - 1) // cols
    top = (H - rows * th - (rows - 1) * gap) // 2
    for i, path in enumerate(shot):
        r, c = divmod(i, cols)
        n = min(cols, len(shot) - r * cols)
        left = (W - n * tw - (n - 1) * gap) // 2
        thumb = rounded(Image.open(os.path.join(OUT, path)).convert('RGB').resize((tw, th), Image.LANCZOS), 8)
        img.paste(thumb, (left + c * (tw + gap), top + r * (th + gap)), thumb)
    return img


def frame(index: int) -> Image.Image:
    shot, title, text = SLIDES[index]
    img = Image.new('RGB', (FW, FH), BG)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, FW - 1, H + 2 * PAD - 1], 22, fill=BEZEL)
    s = rounded(screen(shot), 10)
    img.paste(s, (PAD, PAD), s)
    y = H + 2 * PAD + 14
    d.text((PAD + 4, y), title, font=TITLE_FONT, fill=TITLE)
    d.text((PAD + 4, y + 36), text, font=TEXT_FONT, fill=TEXT)
    # Dots at the right of the title: where this slide is in the show.
    r, step = 4, 14
    x0 = FW - PAD - 4 - (len(SLIDES) - 1) * step - 2 * r
    for i in range(len(SLIDES)):
        x = x0 + i * step
        d.ellipse([x, y + 16 - r, x + 2 * r, y + 16 + r], fill=DOT_ON if i == index else DOT_OFF)
    room = x0 - PAD - 16
    if TITLE_FONT.getlength(title) > room or TEXT_FONT.getlength(text) > FW - 2 * PAD - 8:
        print(f'too long for the slide: {title}')
    return img


def main() -> None:
    frames = [frame(i) for i in range(len(SLIDES))]
    frames[0].save(DEST, save_all=True, append_images=frames[1:], duration=int(SECONDS * 1000),
                   loop=0, optimize=True, disposal=0, blend=0)
    print(f'{os.path.normpath(DEST)}: {len(frames)} slides, {os.path.getsize(DEST) // 1024} KB')


if __name__ == '__main__':
    main()
