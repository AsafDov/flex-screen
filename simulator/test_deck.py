"""Every feature of the deck, driven in the simulator: taps, holds and drags on each page,
checked against what the deck sends the companion (or types over USB), plus the overlays
(toast, call panel, cheat sheet, idle screen) and going offline. Screenshots of the states
worth a look land in out/tests/.

    python test_deck.py        # prints PASS/FAIL per check; exit code 1 if any failed
"""
from __future__ import annotations

import base64
import hashlib
import os
import re
import sys
import time
import traceback

import fixtures
from deck import Deck, HERE

OUT = os.path.join(HERE, 'out', 'tests')
PREV, PLAY, PAUSE, NEXT = '', '', '', ''
CLOSE = ''  # LV_SYMBOL_CLOSE
SHUFFLE, LOOP = '', ''
results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = '') -> None:
    results.append((name, bool(ok), detail))
    print(('PASS ' if ok else 'FAIL ') + name + (f'  ({detail})' if detail and not ok else ''), flush=True)


def shown(d: Deck) -> list[dict]:
    """The objects actually on screen: not hidden (nor inside something hidden), and on the
    visible page (the tabview keeps the others beside it, off screen)."""
    out, hidden_depth = [], None
    for n in d.tree():
        if hidden_depth is not None and n['d'] > hidden_depth:
            continue
        hidden_depth = n['d'] if n.get('hidden') else None
        if hidden_depth is None and 0 <= n['x'] < 800 and 0 <= n['y'] < 480:
            out.append(n)
    return out


def label(d: Deck, text: str, exact: bool = False, below: int = 0) -> dict | None:
    for n in shown(d):
        t = n.get('text', '')
        if (t == text if exact else text in t) and n['y'] >= below:
            return n
    return None


def on_screen(d: Deck, text: str, exact: bool = False) -> bool:
    return label(d, text, exact) is not None


def tap(d: Deck, text: str, exact: bool = False, dx: int = 0, dy: int = 0, hold: bool = False, below: int = 0) -> bool:
    n = label(d, text, exact, below)
    if not n:
        return False
    x, y = n['x'] + n['w'] // 2 + dx, n['y'] + n['h'] // 2 + dy
    (d.hold if hold else d.tap)(x, y)
    return True


CHIPS_LEFT, CHIPS_RIGHT = 98, 474  # the Tasks card's chip row, on Capture
RAIL_W, RAIL_H = 76, 422  # the page rail down the left, above the context bar
LV_HOME = ''  # LV_SYMBOL_HOME, before the primary monitor's name


def rail_tab(d: Deck, name: str) -> dict | None:
    """A page's button (its name) in the rail."""
    return next((n for n in shown(d) if n.get('text') == name and n['x'] < RAIL_W and n['y'] < RAIL_H), None)


def tap_chip(d: Deck, text: str, exact: bool = False) -> bool:
    """A chip on the Tasks card. Its row scrolls sideways, so a chip past the card's edge (the
    Clipboard card covers it there) is swiped into view first, as a finger would."""
    for _ in range(6):
        n, hidden_depth = None, None
        for o in d.tree():  # anywhere along the row, not just on screen
            if hidden_depth is not None and o['d'] > hidden_depth:
                continue
            hidden_depth = o['d'] if o.get('hidden') else None
            t = o.get('text', '')
            if hidden_depth is None and (t == text if exact else text in t) and 100 <= o['y'] < 160 and -800 < o['x'] < 800:
                n = o
                break
        if not n:
            return False
        x, y = n['x'] + n['w'] // 2, n['y'] + n['h'] // 2
        if CHIPS_LEFT + 10 <= x <= CHIPS_RIGHT - 10:
            d.tap(x, y)
            return True
        if x > CHIPS_RIGHT - 10:
            d.drag(376, y, 156, y)
        else:
            d.drag(156, y, 376, y)
    return False


def did(d: Deck, mark: int, action: str, timeout: float = 2, **fields) -> dict | None:
    end = time.time() + timeout
    while time.time() < end:
        hit = d.took(action, since=mark, **fields)
        if hit:
            return hit
        time.sleep(0.05)
    return None


def expect_act(d: Deck, name: str, target: str, action: str, exact: bool = False, dx: int = 0, dy: int = 0,
               hold: bool = False, below: int = 0, **fields) -> None:
    mark = d.mark()
    if not tap(d, target, exact, dx, dy, hold, below):
        check(name, False, f'"{target}" not on screen')
        return
    hit = did(d, mark, action, **fields)
    check(name, hit is not None, f'sent {[ (a.get("a"), {k: v for k, v in a.items() if k not in ("t", "a")}) for a in d.acts(mark)]}')


def keys_since(d: Deck, mark: int) -> list[dict]:
    return [k for k in d.keys(mark) if k['t'] in ('sim.key', 'sim.media', 'sim.type')]


# ---- pages

def test_day(d: Deck) -> None:
    d.page(0)
    now, hour, minute = fixtures.NOW, 3600, 60
    midnight = now - (now + fixtures.TZ_MIN * 60) % 86400  # today's, local
    d.state('calendar', {'status': 'ok', 'events': [  # as the companion sends them to a deck from 0.24.0: not over yet first
        {'id': 'g1', 'title': 'Planning', 'start': now - 10 * minute, 'end': now + 20 * minute, 'allday': False, 'join': True, 'cal': 'Work',
         'color': 0x7986CB},
        {'id': 'e1', 'title': 'Standup', 'start': now + 18 * minute, 'end': now + 33 * minute, 'allday': False, 'join': True},
        {'id': 'e2', 'title': 'Design review with Dana', 'start': now + 3 * hour, 'end': now + 4 * hour, 'allday': False, 'join': False},
        {'id': 'a1', 'title': "Dana's birthday", 'start': midnight, 'end': midnight + 86400, 'allday': True, 'join': False, 'cal': 'Family'},
        {'id': 't1', 'title': 'Dentist', 'start': now + 26 * hour, 'end': now + 27 * hour, 'allday': False, 'join': False},
        {'id': 'p1', 'title': 'Inbox zero', 'start': now - 2 * hour, 'end': now - 90 * minute, 'allday': False, 'join': False},
    ]})
    check('day: Up next is the meeting going on', on_screen(d, 'Now · until 10:32', exact=True))
    tap(d, 'UP NEXT', exact=True)
    check('day: tapping Up next opens the day', on_screen(d, 'TODAY', exact=True) and on_screen(d, 'Sun 8 Nov', exact=True)
          and on_screen(d, 'Close', exact=True))
    check('day: the meeting going on, picked out, with its calendar', on_screen(d, 'Now, until 10:32  ·  Work', exact=True))
    check('day: the next one, with how soon', on_screen(d, 'In 18 min', exact=True))
    check('day: a meeting over is still there, above', on_screen(d, 'Inbox zero', exact=True) and on_screen(d, '08:12', exact=True))
    check('day: all-day events in a strip under the date', on_screen(d, 'ALL DAY', exact=True)
          and on_screen(d, "Dana's birthday  ·  Family", exact=True))
    check('day: then tomorrow', on_screen(d, 'TOMORROW  ·  Mon 9 Nov', exact=True) and on_screen(d, 'Dentist'))
    def top(text: str) -> dict | None:  # the panel's, over Today's own Up next title
        return next((n for n in reversed(shown(d)) if n.get('text') == text), None)
    order = [top(t) for t in ("Dana's birthday  ·  Family", 'Inbox zero', 'Planning', 'Standup', 'Design review with Dana', 'Dentist')]
    check('day: in order of the day', all(order) and [n['y'] for n in order] == sorted(n['y'] for n in order),
          str([(n or {}).get('y') for n in order]))
    d.shot(os.path.join(OUT, 'day.png'))
    planning = top('Planning')
    join = next((n for n in shown(d) if n.get('text', '').endswith('Join') and planning and abs(n['y'] - planning['y']) < 30
                 and n['x'] > 500), None)
    mark = d.mark()
    if join:
        d.tap(join['x'] + join['w'] // 2, join['y'] + join['h'] // 2)
    check("day: a meeting's Join joins it", did(d, mark, 'cal.join', id='g1') is not None, str(d.acts(mark)))
    check('day: and closes the day', not on_screen(d, 'TOMORROW  ·', exact=False))
    tap(d, 'UP NEXT', exact=True)
    tap(d, 'Close', exact=True)
    check('day: Close closes it', not on_screen(d, 'TOMORROW  ·', exact=False))
    tap(d, 'UP NEXT', exact=True)
    mark = d.mark()
    d.tap(20, 460)  # beside the panel
    check('day: a tap beside it closes it, and reaches nothing underneath',
          not on_screen(d, 'TOMORROW  ·', exact=False) and not d.acts(mark), str(d.acts(mark)))
    d.state('google', {'status': 'setup', 'events': []})
    tap(d, 'UP NEXT', exact=True)
    check('day: with no calendar, it says where to connect one', on_screen(d, '"Calendars"'))
    tap(d, 'Close', exact=True)


def test_pages(d: Deck) -> None:
    def tab(name: str) -> dict | None:
        return rail_tab(d, name)

    # A 0.24.0 companion sends the profile's "pages"; the deck keeps its five pages, in their order.
    d.send({'t': 'settings', 'palette': 'base', 'idle_clock_s': 600, 'idle_off_s': 1800, 'cheat_hold_s': 10,
            'pages': ['today', 'panels', 'dev']})
    time.sleep(1.5)
    try:
        tabs = [tab(n) for n in ('Today', 'Audio', 'Dev', 'Windows', 'Capture')]
        others = [tab(n) for n in ('Panels', 'Numpad')]
    except OSError:  # reading the screen needs the same connection: a restart would have closed it
        tabs, others = [None], [None]
    check('pages: the five pages stay, in their order, whatever the settings say',
          all(tabs) and [t['y'] for t in tabs] == sorted(t['y'] for t in tabs), str([(t or {}).get('y') for t in tabs]))
    check('pages: no Panels or Numpad in the rail', not any(others), str(others))

    # Swipes, on Today's clock card: up is the next page, down the one before; sideways too.
    d.page(0)
    d.drag(230, 145, 230, 55)
    check('pages: a swipe up goes to the next page', on_screen(d, 'Microphone Array'))
    d.drag(300, 110, 300, 200)
    check('pages: a swipe down goes back', on_screen(d, 'UP NEXT', exact=True))
    d.drag(330, 110, 130, 110)
    check('pages: a swipe left still goes to the next page', on_screen(d, 'Microphone Array'))
    d.drag(130, 110, 330, 110)
    check('pages: and right back', on_screen(d, 'UP NEXT', exact=True))
    tap_node(d, rail_tab(d, 'Dev'))
    check('pages: the rail goes straight to a page', on_screen(d, 'TERMINAL'))
    d.page(0)


def test_icon_asks(d: Deck) -> None:
    # A Recent tile whose icon never comes (its window closed before the companion
    # restarted): asked for again after 3 s, then 6 s and so on, not every 3 s.
    d.page(0)
    mark = d.mark()
    d.state('actions', {'list': [{'id': 'z1', 'kind': 'window', 'name': 'Zoom', 'title': 'Zoom Meeting', 'icon': 'feedfacecafe'}]})
    time.sleep(10.5)
    asks = [m for m in d.acts(mark) if m.get('a') == 'icon.need' and m.get('id') == 'feedfacecafe']
    check('icons: one that never comes is asked for less and less often (3 in 10 s, at 0, 3 and 9)', len(asks) == 3, str(len(asks)))


def test_today(d: Deck) -> None:
    d.page(0)
    expect_act(d, 'today: Join opens the next meeting', 'Join', 'cal.join', id='e1')
    expect_act(d, 'today: play/pause', PAUSE, 'media.toggle', exact=True, below=180)
    expect_act(d, 'today: next track', NEXT, 'media.next', exact=True, below=180)
    expect_act(d, 'today: previous track', PREV, 'media.prev', exact=True, below=180)
    expect_act(d, 'today: Recent tile runs it again', 'Obsidian', 'recent.run', id='a2', below=180)
    mark = d.mark()
    slider = next((n for n in shown(d) if n.get('c') == 'slider' and n['x'] < 400), None) or {'x': 120, 'y': 340, 'w': 200, 'h': 8}
    y = slider['y'] + slider['h'] // 2
    d.drag(slider['x'] + 10, y, slider['x'] + slider['w'] - 30, y)
    check('today: volume slider sets the volume', did(d, mark, 'vol.set') is not None)
    check('today: the tasks pill counts open and due', on_screen(d, '10 to do · 4 due'))
    tap(d, '10 to do')
    check('today: the tasks pill opens Capture', on_screen(d, 'ALL TASKS', exact=True))
    d.page(0)
    d.shot(os.path.join(OUT, 'today.png'))


def test_status_bar(d: Deck) -> None:
    d.page(0)
    expect_act(d, 'bar: volume button mutes', '42%', 'vol.mute')
    mark = d.mark()
    d.tap(767, 21)
    check('bar: mic button mutes the mic', did(d, mark, 'mic.mute') is not None)
    tap(d, 'Claude working', exact=True)
    check('bar: Claude pill opens the session board', on_screen(d, 'Claude sessions', exact=True))
    tap(d, CLOSE, exact=True)
    check('bar: and its X closes it', not on_screen(d, 'Claude sessions', exact=True))
    tap(d, 'Build passed')
    check('bar: build pill opens Dev', on_screen(d, 'TERMINAL'))
    tap(d, 'Standup ·')
    check('bar: meeting pill opens Today', on_screen(d, 'UP NEXT'))
    tap(d, 'BASE', exact=True)
    check('bar: layer pill pins the cheat sheet', on_screen(d, 'Tap to close'))
    d.shot(os.path.join(OUT, 'cheat-pinned.png'))
    tap(d, 'Tap to close')
    tap(d, 'BASE', exact=True)  # the pill toggles it too
    tap(d, 'BASE', exact=True)
    check('bar: layer pill again closes it', not on_screen(d, 'Tap to close'))


def test_audio(d: Deck) -> None:
    d.page(1)
    expect_act(d, 'audio: picking an output', 'Headphones', 'dev.out', id='o2')
    d.state('devices', fixtures.day()['devices'])  # the PC answers: back to Speakers
    d.settle()
    expect_act(d, 'audio: shuffle', SHUFFLE, 'media.shuffle', exact=True)
    expect_act(d, 'audio: repeat', LOOP, 'media.repeat', exact=True)
    expect_act(d, 'audio: play/pause', PAUSE, 'media.toggle', exact=True)
    expect_act(d, 'audio: Teams name mutes it', 'Teams', 'mixer.mute', exact=True, app='ms-teams')
    spotify = label(d, 'Spotify', exact=True, below=150)
    mark = d.mark()
    if spotify:
        x = spotify['x'] + spotify['w'] // 2
        d.drag(x, 250, x, 200)
    hit = did(d, mark, 'mixer.set', app='spotify')
    check('audio: dragging a fader sets that app', hit is not None, str(d.acts(mark)))
    check('audio: and stays on the page (it isn\'t a swipe)', on_screen(d, 'Microphone Array'))
    d.shot(os.path.join(OUT, 'audio.png'))


def seek_bar(d: Deck) -> dict | None:
    """Audio's progress bar: the slider in the Now Playing band, between the two times."""
    return next((n for n in shown(d) if n.get('c') == 'slider' and n['y'] < 180 and n['w'] > 200), None)


def near(a: tuple[int, int, int], b: int, tolerance: int = 12) -> bool:
    """A frame's pixel against 0xRRGGBB, give or take RGB565's rounding."""
    return all(abs(a[i] - (b >> (16 - 8 * i) & 0xFF)) <= tolerance for i in range(3))


def elapsed_s(d: Deck, bar: dict) -> int | None:
    """The time left of Audio's progress bar, in seconds."""
    n = next((n for n in shown(d) if n.get('text', '').count(':') == 1 and n['y'] < 180 and n['x'] < bar['x']), None)
    if not n:
        return None
    m, sec = n['text'].split(':')
    return int(m) * 60 + int(sec)


def test_seek(d: Deck) -> None:
    d.page(1)
    bar = seek_bar(d)
    if not bar:
        check('seek: the progress bar is there', False, 'no slider in Now Playing')
        return
    y = bar['y'] + bar['h'] // 2
    x0, x1 = bar['x'] + bar['w'] // 2, bar['x'] + bar['w'] * 3 // 4
    want = 0.75 * 489
    mark = d.mark()
    d.touch(x0, y, True)
    for i in range(1, 9):
        time.sleep(0.03)
        d.touch(x0 + (x1 - x0) * i // 8, y, True)
    d.settle()
    shown_s = elapsed_s(d, bar)
    check('seek: dragging shows the time it would go to', shown_s is not None and abs(shown_s - want) < 15, str(shown_s))
    check('seek: and sends nothing until the finger lifts', d.took('media.seek', since=mark) is None, str(d.acts(mark)))
    d.shot(os.path.join(OUT, 'seek-drag.png'))
    d.touch(x1, y, False)
    hit = did(d, mark, 'media.seek')
    check('seek: letting go goes there', hit is not None and abs(hit.get('pos', 0) - want) < 15, str(hit))
    check("seek: and stays on Audio (it isn't a swipe)", on_screen(d, 'Microphone Array'))
    # The PC's next report still has the old place (the app takes a moment): the bar holds.
    d.state('media', dict(fixtures.day()['media'], pos=192))
    d.settle()
    shown_s = elapsed_s(d, bar)
    check("seek: a late report doesn't pull the bar back", shown_s is not None and abs(shown_s - want) < 15, str(shown_s))

    mark = d.mark()
    tap_x = bar['x'] + bar['w'] // 4
    d.tap(tap_x, y)
    hit = did(d, mark, 'media.seek')
    check('seek: a tap on the bar jumps there', hit is not None and abs(hit.get('pos', 0) - 0.25 * 489) < 15, str(hit))

    d.state('media', dict(fixtures.day()['media'], seek=False))
    d.settle()
    d.page(1)
    mark = d.mark()
    d.tap(tap_x, y)
    time.sleep(0.3)
    check("seek: an app that can't seek gets a plain bar", d.took('media.seek', since=mark) is None, str(d.acts(mark)))
    d.page(1)


def test_media_accent(d: Deck) -> None:
    """Now Playing in the cover's color: the card's tint, Audio's bar and app name, Today's volume."""
    accent = fixtures.day()['media']['accent']
    d.page(1)
    bar = seek_bar(d)
    if not bar:
        check('accent: the progress bar is there', False)
        return
    at = (bar['x'] + 4, bar['y'] + bar['h'] // 2)
    card_at = (bar['x'] + bar['w'] // 2, bar['y'] + 20)  # Now Playing's card, under the bar
    frame = d.frame()
    fill, card_with = d.pixel(frame, *at), d.pixel(frame, *card_at)
    check("accent: Audio's progress is in the cover's color", near(fill, accent), f'{fill} vs {accent:06X}')
    d.shot(os.path.join(OUT, 'audio-accent.png'))
    d.state('media', dict(fixtures.day()['media'], accent=0))
    frame = d.frame()
    fill, card_without = d.pixel(frame, *at), d.pixel(frame, *card_at)
    check("accent: none from the PC keeps the theme's", not near(fill, accent), f'{fill}')
    check('accent: the card is tinted with it, and plain without',
          card_with != card_without and card_with[2] > card_without[2], f'{card_with} vs {card_without}')
    d.state('media', fixtures.day()['media'])
    d.page(0)
    d.shot(os.path.join(OUT, 'today-accent.png'))
    slider = next((n for n in shown(d) if n.get('c') == 'slider' and n['x'] < 400), None)
    if not slider:
        check("accent: Today's volume slider is there", False)
        return
    at = (slider['x'] + 4, slider['y'] + slider['h'] // 2)
    fill = d.pixel(d.frame(), *at)
    check("accent: Today's volume is in the cover's color", near(fill, accent), f'{fill} vs {accent:06X}')
    d.offline()
    fill = d.pixel(d.frame(), *at)
    check("accent: offline it's the theme's again", not near(fill, accent), f'{fill}')
    d.online()
    d.sync()


def test_dev(d: Deck) -> None:
    d.page(2)
    expect_act(d, 'dev: a command row runs it again', 'npm run lint', 'term.replay', cmd='npm run lint')
    for op in ('Pull', 'Push', 'Fetch', 'Stash', 'Commit'):
        expect_act(d, f'dev: git {op}', op, f'git.{op.lower()}')
    expect_act(d, 'dev: Open brings up Claude', 'Open', 'claude.open', id='s1', below=60)


def test_windows(d: Deck) -> None:
    d.page(3)
    expect_act(d, 'windows: tap a tile focuses it', 'Chrome', 'win.focus', exact=True, id='102')
    expect_act(d, 'windows: tap a minimized tile brings it back', 'Obsidian', 'win.focus', exact=True, id='104')
    expect_act(d, 'windows: hold a tile closes it', 'Explorer', 'win.close', exact=True, hold=True, id='105')
    d.shot(os.path.join(OUT, 'windows-closing.png'))
    d.state('windows', {'list': [w for w in fixtures.day()['windows']['list'] if w['id'] != '105']})
    d.settle()
    check('windows: the closed window is gone', not on_screen(d, 'Explorer', exact=True))
    mark = d.mark()
    tap(d, 'Show desktop')
    keys = keys_since(d, mark)
    check('windows: Show desktop is Win+D over USB',
          any(k.get('k') == 0x83 for k in keys) and any(k.get('k') == ord('d') for k in keys), str(keys))
    expect_act(d, 'windows: Lock PC', 'Lock PC', 'pc.lock')
    expect_act(d, 'windows: a FancyZones layout', 'Rows', 'zone.layout', exact=True, id='rows', mon=0)
    expect_act(d, 'windows: tapping a zone snaps into it', '2', 'zone.snap', exact=True, mon=0, zone=1, below=200)
    tap(d, 'LEN T24i-10')
    check('windows: picking the other monitor shows its zones', on_screen(d, 'Chrome', exact=True))
    d.shot(os.path.join(OUT, 'windows-monitor2.png'))
    # Each layout button's thumbnail: the PC's 40x26 cells scaled into the button's box, a
    # pixel apart (placed as sent, only the top-left corner of them showed).
    for name, count in (('Columns', 3), ('Rows', 2), ('Priority', 3)):
        thumb, cells = layout_thumb(d, name)
        inside = bool(thumb) and all(thumb['x'] <= c['x'] and c['x'] + c['w'] <= thumb['x'] + thumb['w'] and
                                     thumb['y'] <= c['y'] and c['y'] + c['h'] <= thumb['y'] + thumb['h'] for c in cells)
        right = max((c['x'] + c['w'] for c in cells), default=0)
        bottom = max((c['y'] + c['h'] for c in cells), default=0)
        fills = bool(thumb) and right >= thumb['x'] + thumb['w'] - 2 and bottom >= thumb['y'] + thumb['h'] - 2
        apart = all(not (a['x'] < b['x'] + b['w'] and b['x'] < a['x'] + a['w'] and a['y'] < b['y'] + b['h'] and b['y'] < a['y'] + a['h'])
                    for i, a in enumerate(cells) for b in cells[i + 1:])
        check(f'windows: the {name} thumbnail shows its whole layout, to scale',
              len(cells) == count and inside and fills and apart, f'{thumb} {cells}')


def layout_thumb(d: Deck, name: str) -> tuple[dict | None, list[dict]]:
    """A FancyZones layout button's thumbnail box and its cells (those showing), by the name
    beside it: the box and the name are the button's children, the cells the box's."""
    tree = d.tree()
    i = next((k for k, n in enumerate(tree) if n.get('text', '').startswith(name) and n['x'] > 560 and not n.get('hidden')), None)
    if i is None:
        return None, []
    depth, cells, k = tree[i]['d'], [], i - 1
    while k >= 0 and tree[k]['d'] > depth:
        if tree[k]['d'] == depth + 1 and not tree[k].get('hidden'):
            cells.append(tree[k])
        k -= 1
    thumb = tree[k] if k >= 0 and tree[k]['d'] == depth else None
    return thumb, cells[::-1]


def on_map(d: Deck, text: str) -> dict | None:
    """A label on the desktop map (the open windows card, left of the FancyZones panel): its
    text, or its start when the window is too narrow for all of it ("Termin…")."""
    for n in shown(d):
        t = n.get('text', '')
        cut = t.rstrip('.…')
        if (t == text or (len(cut) >= 4 and len(cut) < len(t) and text.startswith(cut))) and RAIL_W < n['x'] < 460 and 80 < n['y'] < 420:
            return n
    return None


def test_desktop_map(d: Deck) -> None:
    d.page(3)
    check('map: the tiles show first', on_screen(d, 'Show desktop'))
    tap(d, 'Map', exact=True)
    d.settle()
    check('map: Map shows the desktop map instead of the tiles',
          not on_screen(d, 'Show desktop') and on_screen(d, 'Tiles', exact=True) and on_screen(d, 'Drag: move', exact=True))
    d.shot(os.path.join(OUT, 'windows-map.png'))
    dell, lenovo = on_map(d, LV_HOME + ' DELL P2722H'), on_map(d, 'LEN T24i-10')
    check('map: both monitors, side by side', bool(dell and lenovo) and lenovo['x'] > dell['x'], f'{dell} {lenovo}')
    check('map: a minimized window is left off it', not on_map(d, 'Obsidian') and not on_map(d, 'Spotify'))
    vscode = on_map(d, 'VS Code')
    mark = d.mark()
    if vscode:
        d.tap(vscode['x'] + 10, vscode['y'] + 6)
    check('map: tap a window focuses it', bool(did(d, mark, 'win.focus', id='101')))
    terminal = on_map(d, 'Terminal')
    check('map: the windows show their names', bool(terminal and vscode))
    if terminal and lenovo:
        # Into the portrait monitor's lower zone (Claude's), just above its name at the bottom.
        mark = d.mark()
        d.drag(terminal['x'] + 10, terminal['y'] + 6, lenovo['x'] + lenovo['w'] // 2, lenovo['y'] - 20, steps=16, ms=600)
        hit = did(d, mark, 'zone.snap')
        check('map: drag a window into a zone snaps it there', bool(hit) and hit.get('mon') == 1 and hit.get('zone') == 1 and
              hit.get('hwnd') == 103, str(hit))
        check('map: the drag stays on the Windows page', on_screen(d, 'Tiles', exact=True))
    explorer = on_map(d, 'Explorer') or on_map(d, 'PrusaSlicer')
    check('map: a window behind others is on it too', bool(explorer))
    if explorer and dell:
        # Below the landscape monitor, where there's no screen: nothing moves.
        mark = d.mark()
        d.drag(explorer['x'] + 10, explorer['y'] + 6, dell['x'] + dell['w'] // 2, dell['y'] + 40, steps=16, ms=600)
        time.sleep(0.5)
        check('map: let go off the monitors, nothing moves', not d.took('zone.snap', since=mark) and not d.took('win.move', since=mark))
    # A monitor without zones: the window goes where it's let go.
    no_zones = fixtures.zones()
    no_zones['mons'][1]['zones'] = []
    d.state('zones', no_zones)
    d.settle()
    vscode, lenovo = on_map(d, 'VS Code'), on_map(d, 'LEN T24i-10')
    if vscode and lenovo:
        mark = d.mark()
        d.drag(vscode['x'] + 10, vscode['y'] + 6, lenovo['x'] + 10, lenovo['y'] - 120, steps=16, ms=600)
        hit = did(d, mark, 'win.move')
        check('map: on a monitor without zones it moves where it was let go', bool(hit) and hit.get('id') == '101' and
              hit.get('mon') == 1, str(hit))
    tap(d, 'Tiles', exact=True)
    d.settle()
    check('map: Tiles shows the tiles again', on_screen(d, 'Show desktop'))


def tile(d: Deck, app: str) -> dict | None:
    """A window's tile on the Windows page: its app's name, in the open windows card."""
    return next((n for n in shown(d) if n.get('text') == app and RAIL_W < n['x'] < 460), None)


def test_tile_drag(d: Deck) -> None:
    d.page(3)
    # Where the map puts the monitors, to aim at: the map comes up in the same place.
    tap(d, 'Map', exact=True)
    d.settle()
    dell, lenovo = on_map(d, LV_HOME + ' DELL P2722H'), on_map(d, 'LEN T24i-10')
    tap(d, 'Tiles', exact=True)
    d.settle()
    check('tile drag: aiming at the map', bool(dell and lenovo), f'{dell} {lenovo}')
    terminal = tile(d, 'Terminal')
    if not (dell and lenovo and terminal):
        check('tile drag: the Terminal tile', False, str(terminal))
        return

    # Into the portrait monitor's lower zone, slowly (past the hold's 400 ms): the map comes up
    # mid-drag, and letting go snaps the window there, no close and no focus.
    mark = d.mark()
    x0, y0 = terminal['x'] + 10, terminal['y'] + 6
    x1, y1 = lenovo['x'] + lenovo['w'] // 2, lenovo['y'] - 20
    d.touch(x0, y0, True)
    time.sleep(0.1)
    for i in range(1, 9):
        d.touch(x0 + (x1 - x0) * i // 16, y0 + (y1 - y0) * i // 16, True)
        time.sleep(0.04)
    d.settle()
    check('tile drag: the map comes up while a tile is dragged',
          not on_screen(d, 'Show desktop') and on_screen(d, 'Let go on a zone', exact=True) and bool(on_map(d, 'LEN T24i-10')))
    check('tile drag: a chip of the window follows the finger', sum(1 for n in shown(d) if n.get('text') == 'Terminal') >= 1)
    d.shot(os.path.join(OUT, 'windows-tile-drag.png'))
    for i in range(9, 17):
        d.touch(x0 + (x1 - x0) * i // 16, y0 + (y1 - y0) * i // 16, True)
        time.sleep(0.04)
    time.sleep(0.3)
    d.touch(x1, y1, False)
    d.settle()
    hit = did(d, mark, 'zone.snap')
    check('tile drag: let go on a zone snaps that window there', bool(hit) and hit.get('mon') == 1 and hit.get('zone') == 1 and
          hit.get('hwnd') == 103, str(hit))
    check('tile drag: a drag is not a hold (no close) nor a tap (no focus)',
          not d.took('win.close', since=mark) and not d.took('win.focus', since=mark), str(d.acts(mark)))
    check('tile drag: the tiles come back after', on_screen(d, 'Show desktop') and on_screen(d, 'Map', exact=True) and
          not on_screen(d, 'Let go on a zone', exact=True))
    check('tile drag: the drag stays on the Windows page', on_screen(d, 'Lock PC'))

    # A minimized window's tile too, into the landscape monitor's first zone.
    obsidian = tile(d, 'Obsidian')
    if obsidian:
        mark = d.mark()
        # Its name is at the foot of the monitor, in the middle: left of that is the big zone.
        d.drag(obsidian['x'] + 10, obsidian['y'] + 6, dell['x'] + dell['w'] // 2 - 20, dell['y'] - 60, steps=16, ms=500)
        hit = did(d, mark, 'zone.snap')
        check('tile drag: a minimized window snaps too', bool(hit) and hit.get('mon') == 0 and hit.get('hwnd') == 104, str(hit))
    else:
        check('tile drag: the Obsidian tile', False)

    # Let go off the monitors: nothing moves, the tiles come back.
    explorer = tile(d, 'Explorer')
    if explorer:
        mark = d.mark()
        d.drag(explorer['x'] + 10, explorer['y'] + 6, dell['x'] + dell['w'] // 2, dell['y'] + 40, steps=16, ms=500)
        time.sleep(0.5)
        check('tile drag: let go off the monitors, nothing moves',
              not d.took('zone.snap', since=mark) and not d.took('win.move', since=mark) and not d.took('win.close', since=mark),
              str(d.acts(mark)))
        check('tile drag: and the tiles come back', on_screen(d, 'Show desktop'))

    # A monitor without zones: the window's middle goes where it's let go.
    no_zones = fixtures.zones()
    no_zones['mons'][1]['zones'] = []
    d.state('zones', no_zones)
    d.settle()
    chrome = tile(d, 'Chrome')
    if chrome:
        mark = d.mark()
        d.drag(chrome['x'] + 10, chrome['y'] + 6, lenovo['x'] + lenovo['w'] // 2, lenovo['y'] - 120, steps=16, ms=500)
        hit = did(d, mark, 'win.move')
        check('tile drag: on a monitor without zones it moves there', bool(hit) and hit.get('id') == '102' and hit.get('mon') == 1, str(hit))

    # After all that a tap is still a tap, and a hold still closes.
    d.settle()
    expect_act(d, 'tile drag: a tap still focuses', 'VS Code', 'win.focus', exact=True, id='101')
    expect_act(d, 'tile drag: a hold still closes', 'Claude', 'win.close', exact=True, hold=True, id='106')


def test_capture(d: Deck) -> None:
    d.page(4)
    expect_act(d, 'capture: tap a clip pastes it', 'ssh deck', 'clip.paste', id='c1')
    expect_act(d, 'capture: hold a clip pins it', 'lv_obj_set_style', 'clip.pin', hold=True, id='c3')
    # Three show at a time above Obsidian; the list scrolls to the rest.
    clip = label(d, 'lv_obj_set_style')
    if clip:
        d.drag(clip['x'] + 40, clip['y'] + 8, clip['x'] + 40, clip['y'] - 80, ms=500)
    expect_act(d, 'capture: the clips scroll to the rest', '#5AB4FF', 'clip.pin', hold=True, id='c4')
    expect_act(d, 'capture: Clear', 'Clear', 'clip.clear', exact=True)
    check('capture: no mail tasks any more', not on_screen(d, 'FROM MAIL') and not on_screen(d, 'Open Gmail'))
    expect_act(d, "capture: Obsidian today's note", "Today's note", 'obs.today')
    expect_act(d, 'capture: a recent note', '2026-11-08', 'obs.open', exact=True, path='Daily/2026-11-08.md')
    d.shot(os.path.join(OUT, 'capture.png'))


def keyboard_key(d: Deck, row: int, units_before: float, units: float, row_units: float) -> tuple[int, int] | None:
    """Where a key of LVGL's keyboard is: its row (of 4), and its place in the row in the
    keyboard's width units (letters are 4, "1#" 5, backspace 7 on the top row of 52)."""
    kb = next((n for n in shown(d) if n['c'] == 'keyboard'), None)
    if not kb:
        return None
    return (kb['x'] + int(kb['w'] * (units_before + units / 2) / row_units), kb['y'] + int(kb['h'] * (row + 0.5) / 4))


PLUS, REFRESH = '\uf067', '\uf021'


def test_tasks(d: Deck) -> None:
    d.page(4)
    check('tasks: Capture opens on All tasks, the lists on the deck grouped by due day',
          all(on_screen(d, t) for t in ('ALL TASKS', 'OVERDUE', 'TODAY', 'Reply to the landlord', 'Send the signed lease')))
    check('tasks: All tasks, Scheduled and the lists on the deck are filters, All tasks first',
          all(on_screen(d, t, exact=True) for t in ('All tasks  9', 'Scheduled  8', 'My Tasks  3', 'Home  3'))
          and label(d, 'All tasks  9', exact=True)['x'] < label(d, 'Scheduled  8', exact=True)['x'])
    check('tasks: All tasks leaves out a list kept off the deck', not on_screen(d, 'Call the plumber'))
    check('tasks: an All tasks row says its list', on_screen(d, 'Home  ·  6 Nov  ·  From'))
    for _ in range(2):  # the second drag starts on a heading: that scrolls too
        d.drag(200, 330, 200, 160, steps=20, ms=600)
    undated = label(d, 'Fix the balcony light')
    check('tasks: the undated come after the dated, and the list scrolls to them',
          on_screen(d, 'NO DATE', exact=True) and undated is not None and undated['y'] + undated['h'] <= 411, str(undated))
    d.shot(os.path.join(OUT, 'tasks-all.png'))
    tap_chip(d, 'Scheduled  8', exact=True)
    check('tasks: Scheduled starts at the top, the dated tasks grouped by day',
          all(on_screen(d, t) for t in ('SCHEDULED', 'OVERDUE', 'TODAY', 'TOMORROW', 'Reply to the landlord', 'Send the signed lease')))
    check('tasks: a list kept off the deck has no chip', not on_screen(d, 'Someday'))
    check('tasks: but its dated tasks are under Scheduled', on_screen(d, 'Call the plumber'))
    check('tasks: Scheduled leaves out the undated', not on_screen(d, 'Fix the balcony light'))
    check('tasks: a Scheduled row is its title, due day and status',
          on_screen(d, 'Fri 6 Nov  ·  2 days overdue', exact=True) and on_screen(d, 'Sun 8 Nov  ·  To do', exact=True)
          and not on_screen(d, 'From: Yossi Cohen'))
    d.shot(os.path.join(OUT, 'tasks.png'))

    def tick_box(title: str) -> None:  # the box left of the title
        n = label(d, title)
        if n:
            d.tap(n['x'] - 26, n['y'] + n['h'] // 2)

    mark = d.mark()
    tick_box('Send the signed lease')
    check('tasks: the box ticks a task off', did(d, mark, 'task.done', src='google', list='L1', id='t2', done=True) is not None,
          str(d.acts(mark)))
    check('tasks: a ticked task moves to Done', on_screen(d, 'DONE', exact=True) and on_screen(d, 'Sun 8 Nov  ·  Done', exact=True))
    check('tasks: and opens nothing', not on_screen(d, 'Mark not done'))
    mark = d.mark()
    tick_box('Send the signed lease')
    check('tasks: ticking it again unticks it', did(d, mark, 'task.done', id='t2', done=False) is not None)

    expect_act(d, 'tasks: tapping a task opens it', 'Send the signed lease', 'task.open', src='google', list='L1', id='t2')
    check('tasks: the popup says its list, due day and status',
          on_screen(d, 'My Tasks  ·  Due Sun 8 Nov  ·  To do', exact=True) and on_screen(d, 'Mark done') and on_screen(d, 'Loading'))
    d.state('task', {'src': 'google', 'list': 'L1', 'id': 't2', 'found': True, 'title': 'Send the signed lease to the agent',
                     'notes': 'From: Dana Levi\nSign pages 3 and 7, then scan it.'})
    check('tasks: then its whole title and notes',
          on_screen(d, 'Send the signed lease to the agent', exact=True) and on_screen(d, 'Sign pages 3 and 7'))
    d.shot(os.path.join(OUT, 'tasks-open.png'))
    expect_act(d, 'tasks: Mark done ticks it off', 'Mark done', 'task.done', id='t2', done=True)
    check('tasks: and closes the popup', not on_screen(d, 'Sign pages 3 and 7'))
    mark = d.mark()
    tick_box('Send the signed lease')
    did(d, mark, 'task.done', id='t2', done=False)

    expect_act(d, 'tasks: an overdue task opens too', 'Reply to the landlord', 'task.open', id='t1')
    d.state('task', {'src': 'google', 'list': 'L2', 'id': 't1', 'found': True, 'title': 'Reply to the landlord about the boiler', 'notes': ''})
    check('tasks: it says how late it is, and when there are no notes',
          on_screen(d, 'Home  ·  Due Fri 6 Nov  ·  2 days overdue', exact=True) and on_screen(d, 'No notes.', exact=True))
    mark = d.mark()
    d.tap(30, 240)
    check('tasks: a tap beside it closes it, and reaches nothing underneath',
          not on_screen(d, 'No notes.', exact=True) and not d.acts(mark), str(d.acts(mark)))
    tap(d, 'Reply to the landlord')
    tap(d, 'Close', exact=True)
    check('tasks: Close closes it', not on_screen(d, 'No notes.', exact=True))
    expect_act(d, 'tasks: refresh asks the companion', REFRESH, 'task.refresh', exact=True)

    tap_chip(d, 'Home  3', exact=True)
    check('tasks: a list on its own, undated too', on_screen(d, 'HOME', exact=True) and on_screen(d, 'Pay the Arnona bill')
          and on_screen(d, 'Fix the balcony light') and not on_screen(d, 'Send the signed lease'))

    tap(d, PLUS, exact=True)
    check('tasks: New task opens the form with a keyboard', on_screen(d, 'Tomorrow', exact=True)
          and any(n['c'] == 'keyboard' for n in shown(d)))
    d.shot(os.path.join(OUT, 'tasks-new.png'))
    for i in range(3):  # q, w, e: the top row's second to fourth keys
        key = keyboard_key(d, 0, 5 + 4 * i, 4, 52)
        if key:
            d.tap(*key)
    check('tasks: the keys type into it', on_screen(d, 'qwe', exact=True))
    tap(d, 'Tomorrow', exact=True)
    expect_act(d, 'tasks: Add sends it to the list on show, due tomorrow', 'Add', 'task.add',
               src='google', list='L2', title='qwe', due='2026-11-09')
    check('tasks: and the form closes', not on_screen(d, 'Tomorrow', exact=True))

    tap(d, PLUS, exact=True)
    mark = d.mark()
    tap(d, 'Cancel', exact=True)
    check('tasks: Cancel closes the form and adds nothing',
          not on_screen(d, 'Tomorrow', exact=True) and did(d, mark, 'task.add', timeout=0.5) is None)
    tap_chip(d, 'Scheduled  ')
    tap(d, PLUS, exact=True)
    for i in range(3):
        key = keyboard_key(d, 0, 5 + 4 * i, 4, 52)
        if key:
            d.tap(*key)
    expect_act(d, 'tasks: from Scheduled, Add goes to the first list, due today', 'Add', 'task.add',
               src='google', list='L1', title='qwe', due='2026-11-08')

    tap_chip(d, 'Home  3', exact=True)
    hidden = fixtures.tasks()
    hidden['rev'] = 8
    hidden['lists'][1]['show'] = False
    d.state('tasks', hidden)
    check('tasks: a list taken off the deck loses its chip, back to All tasks',
          on_screen(d, 'ALL TASKS', exact=True) and not on_screen(d, 'Home  3', exact=True))
    d.state('tasks', fixtures.tasks())

    d.state('tasks', {'rev': 9, 'status': 'signin', 'from': 'Google Tasks', 'lists': [], 'tasks': [], 'open': 0})
    check('tasks: signed out, it says which source to sign in to', on_screen(d, 'Sign in to Google Tasks'))
    d.shot(os.path.join(OUT, 'tasks-signin.png'))
    d.state('tasks', {'rev': 10, 'status': 'setup', 'lists': [], 'tasks': [], 'open': 0})
    check('tasks: nothing connected, it says where to connect', on_screen(d, '"Task sources"'))
    signed_out = fixtures.tasks()
    signed_out.update(rev=11, warn='Google Tasks')
    d.state('tasks', signed_out)
    check('tasks: one source signed out, the others still show, with a word about it',
          on_screen(d, 'Sign in to Google Tasks again') and on_screen(d, 'Send the signed lease'))
    d.shot(os.path.join(OUT, 'tasks-warn.png'))


def test_task_sources(d: Deck) -> None:
    d.page(4)
    d.state('tasks', fixtures.task_sources())
    tap_chip(d, 'Scheduled  ')
    check('sources: a Jira issue is under Scheduled by its due day, with its key, priority and status',
          on_screen(d, 'Checkout fails on Safari') and on_screen(d, 'Mon 9 Nov  ·  ACME-142  ·  High'))  # the status cut short on the card
    n = label(d, 'Checkout fails on Safari')
    mark = d.mark()
    if n:
        d.tap(n['x'] - 26, n['y'] + n['h'] // 2)  # where a task's box is
    check('sources: an issue has no box: a tap there opens it, ticking nothing',
          did(d, mark, 'task.open', src='jira', list='assigned', id='ACME-142') is not None
          and did(d, mark, 'task.done', timeout=0.3) is None, str(d.acts(mark)))
    check('sources: its popup says its list and due day, and has no Mark done',
          on_screen(d, 'Jira  ·  Due Mon 9 Nov', exact=True) and not on_screen(d, 'Mark done') and not on_screen(d, 'Open in'))
    d.state('task', {'src': 'jira', 'list': 'assigned', 'id': 'ACME-142', 'found': True, 'title': 'Checkout fails on Safari 17',
                     'notes': 'Steps: add to cart, pay with Apple Pay.', 'meta': 'Acme Web  ·  ACME-142  ·  High  ·  In Progress',
                     'link': 'Open in Jira'})
    check('sources: then what Jira says about it, and a button to open it',
          on_screen(d, 'Acme Web  ·  ACME-142  ·  High  ·  In Progress', exact=True) and on_screen(d, 'Open in Jira', exact=True)
          and on_screen(d, 'Steps: add to cart'))
    d.shot(os.path.join(OUT, 'tasks-jira.png'))
    expect_act(d, 'sources: Open in Jira opens it on the PC', 'Open in Jira', 'task.link', exact=True,
               src='jira', list='assigned', id='ACME-142')
    check('sources: and closes the popup', not on_screen(d, 'Open in Jira', exact=True))

    tap(d, 'Checkout fails on Safari')
    d.state('task', {'src': 'jira', 'list': 'assigned', 'id': 'ACME-142', 'found': True,
                     'title': 'Checkout fails on Safari 17 when the cart has more than twenty items and a discount code ' * 3,
                     'notes': '\n'.join(f'Step {i}: something to check' for i in range(30)),
                     'meta': 'Acme Web  ·  ACME-142  ·  High  ·  In Progress', 'link': 'Open in Jira'})
    close = label(d, 'Close', exact=True)
    check('sources: a long title and notes scroll, the buttons stay on the panel',
          close is not None and close['y'] + close['h'] <= 450 and on_screen(d, 'Open in Jira', exact=True), str(close))
    d.shot(os.path.join(OUT, 'tasks-long.png'))
    tap(d, 'Close', exact=True)

    tap_chip(d, 'Jira  2', exact=True)
    check('sources: Jira is a list of its own, undated issues too',
          on_screen(d, 'JIRA', exact=True) and on_screen(d, 'Upgrade the payment SDK') and on_screen(d, 'ACME-77  ·  Medium  ·  To Do'))
    tap(d, PLUS, exact=True)
    check('sources: + from Jira offers only the lists that take new tasks',
          on_screen(d, 'Tomorrow', exact=True) and on_screen(d, 'Daily note', exact=True) and on_screen(d, 'My Tasks', exact=True)
          and not on_screen(d, 'Jira', exact=True))
    for i in range(3):  # q, w, e
        key = keyboard_key(d, 0, 5 + 4 * i, 4, 52)
        if key:
            d.tap(*key)
    expect_act(d, 'sources: and adds to the first of them', 'Add', 'task.add', src='google', list='L1', title='qwe', due='')

    tap_chip(d, 'Scheduled  ')
    n = label(d, 'Water the plants')
    mark = d.mark()
    if n:
        d.tap(n['x'] - 26, n['y'] + n['h'] // 2)
    check('sources: a daily note task has a box that ticks it off',
          did(d, mark, 'task.done', src='obsidian', list='daily', id='a1b2c3', done=True) is not None, str(d.acts(mark)))
    d.state('tasks', fixtures.tasks())


def numpad_open(d: Deck) -> bool:
    return on_screen(d, 'Send keys', exact=True)


def test_numpad(d: Deck) -> None:
    d.page(0)  # not Windows: its zone map's "1" "2" "3" would be found before the keys
    d.tap(*fixtures.LAUNCHER)
    tap(d, 'Numpad', exact=True)
    check('numpad: opens from the shortcuts panel', numpad_open(d) and not launcher_open(d))
    check('numpad: over the page, which stays', on_screen(d, 'UP NEXT', exact=True))
    check('numpad: the context bar hides under it', not bar_key(d, 'Build'))
    d.shot(os.path.join(OUT, 'numpad.png'))
    mark = d.mark()
    tap(d, '7', exact=True)
    keys = keys_since(d, mark)
    check('numpad: a key goes over USB', any(k.get('k') == 0xE7 and k.get('down') for k in keys), str(keys))
    expect_act(d, 'numpad: ( is typed by the companion', '(', 'key.text', exact=True, text='(')
    tap(d, 'Calculator')
    for key in ('1', '2', '+', '3'):
        tap(d, key, exact=True)
    tap(d, 'Enter')
    check('numpad: the calculator works it out', on_screen(d, '15', exact=True))
    d.shot(os.path.join(OUT, 'calculator.png'))
    expect_act(d, 'numpad: a second Enter types the result', 'Enter', 'key.text', text='15')
    tap(d, 'Send keys')
    tap(d, CLOSE, exact=True)
    check('numpad: its X closes it', not numpad_open(d))
    check('numpad: and the context bar is back', bar_key(d, 'Build') is not None)
    d.page(5)  # the companion's (and the simulator's) old index for it opens it
    check('numpad: page 5 opens it', numpad_open(d))
    mark = d.mark()
    d.tap(38, 160)  # the rail's Dev button, beside it
    check('numpad: a tap beside it closes it, and reaches nothing underneath',
          not numpad_open(d) and not on_screen(d, 'TERMINAL') and not d.acts(mark), str(d.acts(mark)))
    d.page(0)


# ---- overlays

def test_toast(d: Deck) -> None:
    d.page(0)
    d.toast('Claude needs your approval · flex-screen', 'attention', 'claude.open', 's1')
    d.settle()
    check('toast: shows', on_screen(d, 'Claude needs your approval ·'))
    d.shot(os.path.join(OUT, 'toast.png'))
    expect_act(d, 'toast: tapping it runs its action', 'Claude needs your approval ·', 'claude.open', id='s1')
    check('toast: goes away when tapped', not on_screen(d, 'Claude needs your approval ·'))


def test_call(d: Deck) -> None:
    d.page(0)
    d.state('call', fixtures.call('teams'))
    d.settle()
    check('call: the panel comes up', on_screen(d, 'Leave', exact=True))
    d.shot(os.path.join(OUT, 'call-teams.png'))
    expect_act(d, 'call: mic', 'Mic', 'call.mic', exact=True)
    expect_act(d, 'call: camera', 'Camera', 'call.camera', exact=True)
    expect_act(d, 'call: raise hand', 'Hand', 'call.hand', exact=True)
    expect_act(d, 'call: leave', 'Leave', 'call.leave', exact=True)
    tap(d, 'Hide')
    check('call: Hide leaves a pill in the bar', not on_screen(d, 'Leave', exact=True))
    d.shot(os.path.join(OUT, 'call-hidden.png'))
    d.state('call', fixtures.call('discord'))
    d.settle()
    d.shot(os.path.join(OUT, 'call-discord.png'))
    d.state('call', {'app': ''})
    d.settle()
    check('call: ends', not on_screen(d, 'Leave', exact=True))


def zoom_buttons(d: Deck, h: int) -> list[dict]:
    """The call panel's buttons of that height, as shown, top to bottom, left to right (36:
    kinds and swatches, 78: the line widths). Only the panel's: the page under it has its own."""
    nodes, inside, panel_depth, hidden_depth, found = d.tree(), False, 0, None, []
    for n in nodes:
        if inside and n['d'] <= panel_depth:
            break
        if not inside:
            inside = (n['x'], n['y'], n['w'], n['h']) == (82, 44, 712, 378) and not n.get('hidden')  # right of the rail
            panel_depth = n['d']
            continue
        if hidden_depth is not None and n['d'] > hidden_depth:
            continue
        hidden_depth = n['d'] if n.get('hidden') else None
        if hidden_depth is None and n['h'] == h and n.get('c') == 'button':
            found.append(n)
    return sorted(found, key=lambda n: (n['y'], n['x']))


def tap_node(d: Deck, n: dict, hold: bool = False) -> None:
    (d.hold if hold else d.tap)(n['x'] + n['w'] // 2, n['y'] + n['h'] // 2)


def tool_order(d: Deck) -> list[str]:
    """The drawing tools' names as shown, left to right."""
    names = {'Laser', 'Pen', 'Marker', 'Fading', 'Eraser', 'Pan', 'Select', 'Sticky', 'Shapes', 'Text', 'Mouse', 'Stamp', 'Line'}
    row = [n for n in shown(d) if n.get('text') in names and n['y'] < 200]
    return [n['text'] for n in sorted(row, key=lambda n: n['x'])]


def slider_in_panel(d: Deck) -> dict | None:
    return next((n for n in shown(d) if n.get('c') == 'slider' and n['y'] > 120 and n['w'] > 300), None)


def test_zoom(d: Deck) -> None:
    d.page(0)
    muted = fixtures.call('zoom')
    muted['mic_muted'] = True
    d.state('call', muted)
    d.settle()
    check('zoom: the panel has Whiteboard', on_screen(d, 'Whiteboard', exact=True))
    d.shot(os.path.join(OUT, 'call-zoom-muted.png'))
    expect_act(d, 'zoom: whiteboard', 'Whiteboard', 'call.whiteboard', exact=True)

    d.state('call', fixtures.zoom_draw('annotate'))
    d.settle()
    order = tool_order(d)
    check('zoom: annotation tools, in order', order == ['Laser', 'Pen', 'Marker', 'Fading', 'Eraser', 'Select'], str(order))
    check('zoom: Save while annotating', on_screen(d, 'Save', exact=True))
    d.shot(os.path.join(OUT, 'zoom-annotate.png'))
    expect_act(d, 'zoom: pick the highlighter', 'Marker', 'draw.tool', exact=True, id='highlighter')
    expect_act(d, 'zoom: the laser is the spotlight', 'Laser', 'draw.tool', exact=True, id='spotlight')
    swatches = [n for n in zoom_buttons(d, 36) if n['w'] < 100]
    check('zoom: 15 annotation colors', len(swatches) == 15, str(len(swatches)))
    if len(swatches) > 4:
        mark = d.mark()
        tap_node(d, swatches[4])
        check('zoom: pick a color', did(d, mark, 'draw.color', id='4') is not None, str(d.acts(mark)))
    widths = zoom_buttons(d, 78)
    check('zoom: four line widths when annotating', len(widths) == 4, str(len(widths)))
    if len(widths) == 4:
        mark = d.mark()
        tap_node(d, widths[3])
        check('zoom: pick a width', did(d, mark, 'draw.width', id='3') is not None, str(d.acts(mark)))
    check('zoom: no weight slider when annotating', slider_in_panel(d) is None)
    mark = d.mark()
    tap(d, 'Clear', exact=True)
    check('zoom: a tap on Clear only says to hold', on_screen(d, 'Hold', exact=True) and did(d, mark, 'draw.clear', timeout=0.5) is None)
    time.sleep(2.2)
    check('zoom: the hint goes back to Clear', on_screen(d, 'Clear', exact=True))
    expect_act(d, 'zoom: holding Clear clears', 'Clear', 'draw.clear', exact=True, hold=True)
    expect_act(d, 'zoom: undo', 'Undo', 'draw.undo', exact=True)
    expect_act(d, 'zoom: mic while drawing', 'Mic', 'call.mic', exact=True)
    expect_act(d, 'zoom: stop annotating', 'Stop', 'draw.close', exact=True)
    tap(d, 'Call')
    check('zoom: the call controls while drawing', on_screen(d, 'Leave', exact=True) and not on_screen(d, 'Marker', exact=True))
    expect_act(d, 'zoom: Annotate stops it', 'Annotate', 'call.annotate', exact=True)
    tap(d, 'Draw')
    check('zoom: back to the tools', on_screen(d, 'Marker', exact=True))

    d.state('call', fixtures.zoom_draw('annotate', 'spotlight'))
    d.settle()
    check('zoom: the laser has its kinds, no colors',
          on_screen(d, 'Spotlight', exact=True) and on_screen(d, 'Arrow', exact=True) and not [n for n in zoom_buttons(d, 36) if n['w'] < 100])
    d.shot(os.path.join(OUT, 'zoom-laser.png'))
    expect_act(d, 'zoom: laser arrow', 'Arrow', 'draw.kind', exact=True, id='arrow')

    d.state('call', fixtures.zoom_draw('whiteboard', 'pen'))
    d.settle()
    order = tool_order(d)
    check('zoom: whiteboard tools, in order', order == ['Laser', 'Pen', 'Marker', 'Eraser', 'Pan', 'Select'], str(order))
    check('zoom: no Save on a whiteboard', not on_screen(d, 'Save', exact=True))
    swatches = [n for n in zoom_buttons(d, 36) if n['w'] < 100]
    check('zoom: 24 pen colors', len(swatches) == 24, str(len(swatches)))
    check('zoom: no width buttons on a whiteboard', not zoom_buttons(d, 78))
    slider = slider_in_panel(d)
    check('zoom: the pen has a weight slider', slider is not None and on_screen(d, 'Weight', exact=True))
    d.shot(os.path.join(OUT, 'zoom-whiteboard.png'))
    if slider:
        mark = d.mark()
        d.tap(slider['x'] + slider['w'] * 3 // 4, slider['y'] + slider['h'] // 2)
        hit = did(d, mark, 'draw.weight')
        check('zoom: the slider sets the weight', hit is not None and 13 <= int(hit.get('id', 0)) <= 17, str(d.acts(mark)))

    d.state('call', fixtures.zoom_draw('whiteboard', 'highlighter'))
    d.settle()
    swatches = zoom_buttons(d, 36)  # no kinds on the marker: every one is a swatch
    check("zoom: the marker shows its own colors", len(swatches) == len(fixtures.MARKER_COLORS), str(len(swatches)))
    check('zoom: the marker has a weight slider', slider_in_panel(d) is not None)
    d.shot(os.path.join(OUT, 'zoom-marker.png'))

    d.state('call', fixtures.zoom_draw('whiteboard', 'eraser'))
    d.settle()
    check('zoom: the eraser has kinds and a weight, no colors', on_screen(d, 'Pixel', exact=True) and slider_in_panel(d) is not None
          and not [n for n in zoom_buttons(d, 36) if n['w'] < 100])
    d.shot(os.path.join(OUT, 'zoom-eraser.png'))
    expect_act(d, 'zoom: whole-stroke eraser', 'Whole stroke', 'draw.kind', exact=True, id='stroke')

    d.state('call', fixtures.zoom_draw('whiteboard', 'laser'))
    d.settle()
    swatches = zoom_buttons(d, 36)
    check('zoom: the laser shows its colors, no weight', len(swatches) == len(fixtures.LASER_COLORS) and slider_in_panel(d) is None,
          str(len(swatches)))
    d.shot(os.path.join(OUT, 'zoom-board-laser.png'))
    expect_act(d, 'zoom: close the board', 'Close board', 'draw.close', exact=True)
    tap(d, 'Call')
    mark = d.mark()
    tap(d, 'Whiteboard', exact=True)
    check('zoom: Whiteboard with a board open goes back to its tools',
          on_screen(d, 'Pan', exact=True) and did(d, mark, 'call.whiteboard', timeout=0.5) is None)
    d.state('call', {'app': ''})
    d.settle()
    check('zoom: ends', not on_screen(d, 'Pan', exact=True))


def test_cheat_sheet(d: Deck) -> None:
    d.page(0)
    held = dict(fixtures.day()['kbd'], layer=1, label='NAV', held_ms=3000, key=16)
    d.state('kbd', held)
    d.settle()
    check('cheat sheet: not after 3 s of holding', not on_screen(d, 'Tap the layer pill'))
    d.state('kbd', dict(held, held_ms=10500))
    d.settle()
    check('cheat sheet: after 10 s of holding', on_screen(d, 'Tap the layer pill'))
    d.shot(os.path.join(OUT, 'cheat-held.png'))
    d.state('kbd', fixtures.day()['kbd'])
    d.settle()
    check('cheat sheet: letting go hides it', not on_screen(d, 'Tap the layer pill'))
    # the profile's cheatSheetSeconds
    d.settings('base', cheat_s=6)
    d.state('kbd', dict(held, held_ms=2000))
    d.settle()
    check('cheat sheet: set to 6 s, not after 2 s', not on_screen(d, 'Tap the layer pill'))
    d.state('kbd', dict(held, held_ms=6500))
    d.settle()
    check('cheat sheet: set to 6 s, after 6.5 s', on_screen(d, 'Tap the layer pill'))
    d.state('kbd', fixtures.day()['kbd'])
    d.settings('base', cheat_s=0)
    d.state('kbd', dict(held, held_ms=60000))
    d.settle()
    check('cheat sheet: set to 0, never from holding', not on_screen(d, 'Tap the layer pill'))
    d.state('kbd', fixtures.day()['kbd'])
    d.settings('base')
    d.settle()
    # Edit in Vial: Vial's web app on the PC, and the sheet goes.
    tap(d, 'BASE', exact=True)
    d.settle()
    mark = d.mark()
    tap(d, 'Edit in Vial', exact=True)
    check('cheat sheet: Edit in Vial opens Vial on the PC', did(d, mark, 'kbd.vial') is not None, str(d.acts(mark)))
    d.settle()
    check('cheat sheet: and closes', not on_screen(d, 'Edit in Vial'))


def test_idle(d: Deck) -> None:
    mark = d.mark()
    d.settings('base', clock_s=2, off_s=6)
    d.state('pc', {'locked': False, 'idle': 2})  # past the clock's 2 s, not yet the screen's 6
    try:
        d.wait_for(lambda m: m.get('t') == 'log' and m.get('msg') == 'idle: clock', since=mark, timeout=6)
        check('idle: the clock comes up', True)
    except TimeoutError:
        check('idle: the clock comes up', False)
    d.shot(os.path.join(OUT, 'idle-clock.png'))
    try:
        d.wait_for(lambda m: m.get('t') == 'log' and m.get('msg') == 'idle: screen off', since=mark, timeout=8)
        check('idle: then the screen goes off', True)
    except TimeoutError:
        check('idle: then the screen goes off', False)
    mark = d.mark()
    d.tap(400, 240)
    check('idle: a tap wakes it', any(m == 'idle: awake' for m in d.logs(mark)))
    check('idle: and does nothing else', not d.acts(mark))
    d.settings('base', clock_s=600, off_s=1800)
    d.state('pc', {'locked': False, 'idle': 1})
    d.settle()


def launcher_open(d: Deck) -> bool:
    return on_screen(d, 'Change them from the tray icon')


def test_face(d: Deck) -> None:
    """The launcher's button is a face: two square eyes over a square mouth, in the colors of
    its mood (sampled at the mouth, which never blinks)."""
    d.page(0)
    cx, cy = fixtures.LAUNCHER
    parts = [n for n in shown(d) if abs(n['x'] + n['w'] // 2 - cx) <= 11 and abs(n['y'] + n['h'] // 2 - cy) <= 11
             and n['w'] <= 10 and n['h'] <= 10]
    parts.sort(key=lambda n: (n['y'], n['x']))
    check('face: three squares', len(parts) == 3, str(parts))
    if len(parts) == 3:
        left, right, mouth = parts
        check('face: two eyes side by side over the mouth', left['y'] == right['y'] and left['x'] < mouth['x'] < right['x']
              and mouth['y'] > left['y'] + left['h'], str(parts))

    def mouth_color() -> tuple[int, int, int]:
        d.settle()
        return d.pixel(d.frame(), cx, cy + 6)

    r, g, b = mouth_color()
    check('face: busy while Claude works (the accent)', b > r + 40, str((r, g, b)))
    d.state('approval', fixtures.approval())
    d.settle()
    r, g, b = mouth_color()
    check('face: amber when Claude needs you', r > 180 and b < 120, str((r, g, b)))
    # And holds still, no bobbing: the column down its middle is the mouth alone (the eyes
    # blink beside it), the same over most of a second.
    columns = []
    for _ in range(6):
        f = d.frame(settle=False)
        columns.append([d.pixel(f, cx, y) for y in range(cy - 13, cy + 13)])
        time.sleep(0.15)
    check('face: alert holds still', all(c == columns[0] for c in columns),
          str(sum(c != columns[0] for c in columns)) + ' of 6 frames differ')
    d.shot(os.path.join(OUT, 'face-alert.png'))
    d.state('approval', fixtures.day()['approval'])
    d.state('claude', dict(fixtures.day()['claude'], sessions=[]))
    d.settle()
    r, g, b = mouth_color()
    check('face: grey with nothing going on', abs(r - g) < 30 and abs(g - b) < 30 and r > 60, str((r, g, b)))

    # A failed build or a finished session shows for a minute from when it happened on the PC,
    # so one from an hour ago isn't news. Sampled a row lower, inside the thin mouths.
    def low_mouth_color() -> tuple[int, int, int]:
        d.settle()
        return d.pixel(d.frame(), cx, cy + 7)

    def grey(c: tuple[int, int, int]) -> bool:
        return abs(c[0] - c[1]) < 30 and abs(c[1] - c[2]) < 30

    build = fixtures.day()['build']
    d.state('build', dict(build, state='failed', secs=3600))
    c = low_mouth_color()
    check('face: a build that failed an hour ago leaves it calm', grey(c), str(c))
    d.state('build', dict(build, state='passed'))
    d.state('build', dict(build, state='failed', secs=5))
    r, g, b = low_mouth_color()
    check('face: glum just after a build fails', r > 200 and 110 < g < 190 and b > 80, str((r, g, b)))
    d.state('build', build)
    session = fixtures.day()['claude']['sessions'][0]
    d.state('claude', dict(fixtures.day()['claude'], sessions=[dict(session, state='done', secs=3600)]))
    c = low_mouth_color()
    check('face: a session done an hour ago leaves it calm', grey(c), str(c))
    d.state('claude', fixtures.day()['claude'])
    d.settle()
    d.state('claude', dict(fixtures.day()['claude'], sessions=[dict(session, state='done', secs=2)]))
    r, g, b = low_mouth_color()
    check('face: pleased just after Claude finishes', g > r + 80 and g > b + 20, str((r, g, b)))
    d.state('claude', dict(fixtures.day()['claude'], sessions=[]))
    d.offline()
    r, g, b = mouth_color()
    check('face: faded while the PC is away', max(r, g, b) < 110, str((r, g, b)))
    d.online()
    d.settle()


def test_launcher(d: Deck) -> None:
    d.page(0)
    button = next((n for n in shown(d) if n['x'] < RAIL_W and n['h'] == 44 and 300 < n['y'] < RAIL_H), None)
    check('launcher: its button is at the foot of the rail', button is not None and button['y'] + button['h'] <= RAIL_H
          and abs(button['x'] + button['w'] // 2 - fixtures.LAUNCHER[0]) <= 2
          and abs(button['y'] + button['h'] // 2 - fixtures.LAUNCHER[1]) <= 2, str(button))
    last = rail_tab(d, 'Capture')
    check('launcher: all five page buttons fit above it', all(rail_tab(d, n) for n in ('Today', 'Capture')) and last
          and button and last['y'] + last['h'] < button['y'], str((last, button)))
    d.tap(*fixtures.LAUNCHER)
    check('launcher: the button opens it', launcher_open(d))
    check('launcher: with the Numpad button in its header', on_screen(d, 'Numpad', exact=True))
    names = ['Gmail', 'Calendar', "Today's note", 'Spotify', 'VS Code', 'Terminal']
    check('launcher: every shortcut has a tile', all(on_screen(d, n, exact=True) for n in names),
          str([n for n in names if not on_screen(d, n, exact=True)]))
    check("launcher: the calendar tile shows today's date", on_screen(d, '8', exact=True))
    head = label(d, 'Shortcuts', exact=True)
    check('launcher: it sits between the bars', head is not None and head['y'] > 44, str(head))
    d.shot(os.path.join(OUT, 'launcher.png'))
    # The panel's own label: Today's now-playing card says "Spotify" too, under the panel (the
    # top layer, where the panel is, comes last in the tree).
    tile = [n for n in shown(d) if n.get('text') == 'Spotify'][-1]
    mark = d.mark()
    d.tap(tile['x'] + tile['w'] // 2, tile['y'] + tile['h'] // 2)
    check('launcher: a tile runs its shortcut', did(d, mark, 'shortcut.run', id='k4') is not None, str(d.acts(mark)))
    check('launcher: and closes', not launcher_open(d))

    d.tap(*fixtures.LAUNCHER)
    mark = d.mark()
    d.tap(400, 100)  # on Today's Up next card, above the panel
    check('launcher: a tap beside it closes it', not launcher_open(d))
    check('launcher: and reaches nothing underneath', not d.acts(mark), str(d.acts(mark)))
    d.tap(*fixtures.LAUNCHER)
    d.tap(*fixtures.LAUNCHER)
    check('launcher: the button again closes it', not launcher_open(d))

    tap_node(d, rail_tab(d, 'Capture'))
    check('launcher: the tabs still switch pages', on_screen(d, 'CLIPBOARD'))
    d.tap(*fixtures.LAUNCHER)
    check('launcher: it opens over any page', launcher_open(d) and on_screen(d, 'Gmail', exact=True))
    d.tap(*fixtures.LAUNCHER)
    d.page(0)

    d.state('shortcuts', fixtures.stress()['shortcuts'])
    d.tap(*fixtures.LAUNCHER)
    tiles = [n for n in shown(d) if n.get('text', '').startswith('Shortcut ')]
    check('launcher: twelve tiles at most', on_screen(d, 'Shortcut 12 (git)') and not on_screen(d, 'thirteenth'), f'{len(tiles)} tiles')
    head = label(d, 'Shortcuts', exact=True)
    check('launcher: twelve still fit between the bars', head is not None and head['y'] > 44, str(head))
    long_name = label(d, 'A shortcut with a')  # the deck cuts it, so only its start is sure to be there
    check('launcher: a long name stops at two lines', long_name is not None and long_name['h'] <= 40, str(long_name))
    d.shot(os.path.join(OUT, 'launcher-12.png'))
    d.tap(*fixtures.LAUNCHER)

    d.state('shortcuts', {'list': []})
    d.tap(*fixtures.LAUNCHER)
    check('launcher: an empty list says how to add some', on_screen(d, 'No shortcuts yet'))
    d.shot(os.path.join(OUT, 'launcher-empty.png'))
    d.tap(*fixtures.LAUNCHER)

    d.state('shortcuts', fixtures.day()['shortcuts'])
    d.offline()
    d.tap(*fixtures.LAUNCHER)
    note = label(d, 'PC offline', below=100)
    check('launcher: offline it says so', note is not None and not on_screen(d, 'Gmail', exact=True))
    mark = d.mark()
    d.tap(*fixtures.LAUNCHER)
    check('launcher: offline the button still closes it', not launcher_open(d))
    d.online()
    d.settle()


BAR_TOP = 428  # the context bar's top edge


def bar_key(d: Deck, name: str) -> dict | None:
    """A context bar key's label (the bar is below the pages)."""
    return next((n for n in shown(d) if n.get('text') == name and n['y'] >= BAR_TOP), None)


def overlapping_bar(d: Deck) -> list[str]:
    """Page content that runs into the context bar. A row of a list that scrolls, past its
    list's bottom, is cut off there (LVGL clips children to their parent): not counted."""
    out, bottoms = [], {}  # the ancestors' bottom edges, by depth
    for n in shown(d):
        bottoms = {k: v for k, v in bottoms.items() if k < n['d']}
        clipped = any(b < BAR_TOP - 2 for k, b in bottoms.items() if k > 0)  # depth 0 is the screen
        bottoms[n['d']] = n['y'] + n['h']
        if clipped or n['c'] not in ('label', 'button', 'image', 'slider', 'bar'):
            continue
        if n['y'] < BAR_TOP - 2 and n['y'] + n['h'] > BAR_TOP + 2:
            out.append(f"{n.get('text') or n['c']}@{n['y']}+{n['h']}")
    return out


ACTION_TILES = {'list': [
    {'id': 't1', 'name': 'Volume down', 'icon': '', 'glyph': 'volume-down', 'act': 'vol.down'},
    {'id': 't2', 'name': 'Volume up', 'icon': '', 'glyph': 'volume-up', 'act': 'vol.up'},
    {'id': 't3', 'name': 'Mute', 'icon': '', 'glyph': 'volume', 'act': 'vol.mute'},
    {'id': 't4', 'name': 'Mic', 'icon': '', 'glyph': 'call', 'act': 'mic.mute'},
    {'id': 't5', 'name': 'Screens off', 'icon': '', 'glyph': 'screen-off', 'act': 'screens.off'},
    {'id': 't6', 'name': 'Task Manager', 'icon': '', 'glyph': 'taskmgr', 'act': 'taskmgr.open'},
    {'id': 't7', 'name': 'Gmail', 'icon': 'ic-gmail', 'glyph': 'mail', 'act': ''},
]}


def tile_at(d: Deck, name: str) -> tuple[int, int] | None:
    """A launcher tile's badge, found by the tile's name (the panel's labels come last)."""
    names = [n for n in shown(d) if n.get('text') == name]
    if not names:
        return None
    n = names[-1]
    return n['x'] + n['w'] // 2, n['y'] - 26


def tile_bg(d: Deck, frame: bytes, name: str) -> tuple[int, int, int]:
    """A tile's own color: its corner, beside the badge."""
    x, y = tile_at(d, name)
    return d.pixel(frame, x - 42, y + 2)


def test_shortcut_actions(d: Deck) -> None:
    d.page(0)
    d.state('shortcuts', ACTION_TILES)
    d.tap(*fixtures.LAUNCHER)
    d.settle()
    d.shot(os.path.join(OUT, 'launcher-actions.png'))
    up = tile_at(d, 'Volume up')
    if not up:
        check('tiles: the volume tiles are there', False)
        return
    mark = d.mark()
    d.tap(*up)
    time.sleep(0.3)
    runs = [a for a in d.acts(mark) if a.get('a') == 'shortcut.run']
    check('tiles: a tap on Volume up steps it once', len(runs) == 1 and runs[0].get('id') == 't2', str(runs))
    check('tiles: and the panel stays open for more', launcher_open(d))
    mark = d.mark()
    d.hold(*up, ms=1200)
    time.sleep(0.2)
    runs = [a for a in d.acts(mark) if a.get('a') == 'shortcut.run' and a.get('id') == 't2']
    check('tiles: holding it keeps stepping', len(runs) >= 5, f'{len(runs)} steps')
    mark = d.mark()
    time.sleep(0.4)
    check('tiles: and stops when it lets go', not d.acts(mark), str(d.acts(mark)))
    check('tiles: still open after the hold', launcher_open(d))

    frame = d.frame()
    plain = tile_bg(d, frame, 'Mute')
    d.state('vol', {'v': 42, 'muted': True})
    d.state('mic', {'available': True, 'muted': True})
    frame = d.frame()
    lit, mic_lit, other = tile_bg(d, frame, 'Mute'), tile_bg(d, frame, 'Mic'), tile_bg(d, frame, 'Gmail')
    check('tiles: Mute lights up while the sound is muted', lit != plain and lit[0] > lit[2], f'{plain} -> {lit}')
    check('tiles: Mic lights up while the mic is muted', mic_lit == lit, f'{mic_lit} vs {lit}')
    check('tiles: other tiles stay as they were', other == plain, f'{other} vs {plain}')
    d.shot(os.path.join(OUT, 'launcher-muted.png'))
    d.state('vol', {'v': 42, 'muted': False})
    check('tiles: and goes back when unmuted', tile_bg(d, d.frame(), 'Mute') == plain)
    d.state('mic', {'available': True, 'muted': False})

    mark = d.mark()
    d.tap(*tile_at(d, 'Screens off'))
    check('tiles: Screens off runs', did(d, mark, 'shortcut.run', id='t5') is not None, str(d.acts(mark)))
    check('tiles: and closes the panel, as other tiles do', not launcher_open(d))
    d.state('shortcuts', fixtures.day()['shortcuts'])


def test_context_bar(d: Deck) -> None:
    d.page(0)
    names = ['Build', 'Terminal', 'Commands', 'Go to file', 'Search', 'Split']
    missing = [n for n in names if not bar_key(d, n)]
    check('context bar: shows the app in front and its keys, names in full', not missing and bar_key(d, 'VS Code'), str(missing))
    d.shot(os.path.join(OUT, 'context-bar.png'))
    key = bar_key(d, 'Commands')
    mark = d.mark()
    if key:
        d.tap(key['x'] + key['w'] // 2, key['y'] + key['h'] // 2)
    check('context bar: a key goes to the companion', did(d, mark, 'ctx.run', id='commands') is not None, str(d.acts(mark)))
    for page in range(5):
        d.page(page)
        bad = overlapping_bar(d)
        check(f'context bar: page {page} fits above it', not bad, str(bad[:5]))
    d.page(5)
    check('context bar: not under the Numpad', not bar_key(d, 'Build'))
    d.page(0)
    check('context bar: back on the other pages', bar_key(d, 'Build') is not None)
    chip = bar_key(d, 'VS Code')
    if chip:
        d.tap(chip['x'] + chip['w'] // 2, chip['y'] + chip['h'] // 2)
        d.settle()
    check("context bar: the app's name goes to its page (VS Code: Dev)", on_screen(d, 'TERMINAL'))
    d.state('context', {**fixtures.day()['context'], 'app': 'Explorer', 'key': 'explorer', 'icon': ''})
    d.settle()
    chip = bar_key(d, 'Explorer')
    if chip:
        d.tap(chip['x'] + chip['w'] // 2, chip['y'] + chip['h'] // 2)
        d.settle()
    check("context bar: the app's name goes to its page (Explorer: Windows)", on_screen(d, 'Show desktop'))
    d.state('context', fixtures.day()['context'])
    d.page(0)
    d.settle()

    chrome = {'app': 'Chrome', 'icon': 'ic-chrome', 'color': 0x34A853, 'keys': [
        {'id': f'c{i}', 'name': name, 'glyph': glyph} for i, (name, glyph) in enumerate([
            ('Back', 'back'), ('Reload', 'reload'), ('Prev tab', 'prev'), ('Next tab', 'next'), ('New tab', 'plus'),
            ('Reopen', 'history'), ('Find', 'search')])]}
    d.state('context', chrome)
    d.settle()
    missing = [k['name'] for k in chrome['keys'] if not bar_key(d, k['name'])]
    check('context bar: follows the app in front, seven keys in full', not missing and bar_key(d, 'Chrome'), str(missing))
    d.shot(os.path.join(OUT, 'context-bar-chrome.png'))

    stress = fixtures.stress()['context']
    d.state('context', stress)
    d.settle()
    keys = [n for n in shown(d) if n['y'] >= BAR_TOP and n['c'] == 'label' and n.get('text') and n['x'] > 130]
    check('context bar: eight keys at most', bar_key(d, 'Mute') and not any('Ninth' in n.get('text', '') for n in keys),
          str([n.get('text') for n in keys]))
    inside = all(n['x'] + n['w'] <= 790 for n in shown(d) if n['y'] >= BAR_TOP)
    check('context bar: eight long keys stay inside it', inside)
    d.shot(os.path.join(OUT, 'context-bar-stress.png'))

    d.page(3)
    shown_y = (label(d, 'Show desktop') or {}).get('y', 0)
    d.state('context', {'app': '', 'icon': '', 'color': 0, 'keys': []})
    d.settle()
    hidden_y = (label(d, 'Show desktop') or {}).get('y', 0)
    check('context bar: none to show, and the pages take its room back',
          not bar_key(d, 'Build') and hidden_y > shown_y, f'{shown_y} -> {hidden_y}')
    d.state('context', fixtures.day()['context'])
    d.page(0)
    d.offline()
    check('context bar: offline it goes', not bar_key(d, 'Build'))
    d.online()
    d.settle()


def menu_open(d: Deck) -> bool:
    return on_screen(d, 'Instead of') or on_screen(d, 'Add a key')


def hold_key(d: Deck, name: str) -> None:
    key = bar_key(d, name)
    if key:
        d.hold(key['x'] + key['w'] // 2, key['y'] + key['h'] // 2)


def menu_item(d: Deck, name: str) -> dict | None:
    """An item in the long-press menu (above the bar, on the top layer: last in the tree)."""
    found = [n for n in shown(d) if n.get('text') == name and n['y'] < BAR_TOP]
    return found[-1] if found else None


def test_context_menu(d: Deck) -> None:
    d.page(0)
    mark = d.mark()
    hold_key(d, 'Commands')
    check('context menu: a long press opens it', on_screen(d, 'Instead of Commands'))
    check('context menu: a long press runs nothing', not d.took('ctx.run', since=mark), str(d.acts(mark)))
    title = label(d, 'Instead of Commands')
    check('context menu: it fits between the status bar and the bar', title is not None and title['y'] > 44, str(title))
    check("context menu: the app's actions and the everyday ones",
          menu_item(d, 'Go to symbol') is not None and on_screen(d, 'VS CODE', exact=True), '')
    d.shot(os.path.join(OUT, 'context-menu.png'))
    d.drag(400, 300, 400, 100)  # from a gap between items
    check('context menu: it scrolls to the everyday actions', menu_item(d, 'Screenshot') is not None and menu_open(d))
    d.shot(os.path.join(OUT, 'context-menu-everyday.png'))
    d.drag(400, 120, 400, 320)
    item = menu_item(d, 'Go to symbol')
    mark = d.mark()
    if item:
        d.tap(item['x'] + item['w'] // 2, item['y'] + item['h'] // 2)
    check('context menu: picking an action puts it in that place',
          did(d, mark, 'ctx.set', app='code', slot=2, id='goto-symbol') is not None, str(d.acts(mark)))
    check('context menu: and closes', not menu_open(d))

    hold_key(d, 'Split')
    mark = d.mark()
    tap(d, 'Remove', exact=True)
    check('context menu: Remove takes the key off', did(d, mark, 'ctx.remove', app='code', slot=5) is not None, str(d.acts(mark)))

    hold_key(d, 'Build')
    mark = d.mark()
    tap(d, 'Reset', exact=True)
    check("context menu: Reset puts the app's keys back", did(d, mark, 'ctx.reset', app='code') is not None, str(d.acts(mark)))

    plus = next((n for n in shown(d) if n.get('text') == '\uf067' and n['y'] >= BAR_TOP), None)
    if plus:
        d.tap(plus['x'] + plus['w'] // 2, plus['y'] + plus['h'] // 2)
    check('context menu: + opens it to add a key', on_screen(d, 'Add a key') and not on_screen(d, 'Remove', exact=True))
    item = menu_item(d, 'Rename')
    mark = d.mark()
    if item:
        d.tap(item['x'] + item['w'] // 2, item['y'] + item['h'] // 2)
    check('context menu: an added key goes at the end', did(d, mark, 'ctx.set', app='code', slot=6, id='rename') is not None,
          str(d.acts(mark)))

    hold_key(d, 'Build')
    mark = d.mark()
    d.tap(400, 20)  # the status bar, above the menu
    check('context menu: a tap beside it closes it', not menu_open(d))
    check('context menu: and reaches nothing underneath', not d.acts(mark), str(d.acts(mark)))

    d.state('context', dict(fixtures.day()['context'], keys=fixtures.day()['context']['keys'][:2] + [
        {'id': 'goto-symbol', 'name': 'Go to symbol', 'glyph': 'at'}]))
    d.settle()
    check('context menu: the bar follows what the companion sends back', bar_key(d, 'Go to symbol') is not None)
    d.state('context', dict(fixtures.day()['context'], keys=[]))
    d.settle()
    plus = next((n for n in shown(d) if n.get('text') == '\uf067' and n['y'] >= BAR_TOP), None)
    check('context menu: with every key removed, + is still there', plus is not None)

    # The shortcuts pressed most in the app come first, in a section of their own: one the
    # bank has (moved up from the app's own), one made from its keys.
    ctx = fixtures.day()['context']
    own, every = ctx['bank'][:ctx['own']], ctx['bank'][ctx['own']:]
    moved = next(k for k in own if k['id'] not in {b['id'] for b in ctx['keys']})
    sug = [{'id': 'used-ctrl+shift+l', 'name': 'Ctrl+Shift+L', 'glyph': 'keyboard'}, moved]
    d.state('context', dict(ctx, bank=sug + [k for k in own if k is not moved] + every, sug=2, own=len(own) - 1))
    d.settle()
    hold_key(d, 'Build')
    sections = [n['text'] for n in shown(d) if n.get('text') in ('YOU PRESS THESE OFTEN', 'VS CODE', 'EVERYWHERE')]
    check('context menu: the shortcuts you press come first, then the app\'s, then everyday ones',
          sections == ['YOU PRESS THESE OFTEN', 'VS CODE', 'EVERYWHERE'], str(sections))
    first = label(d, 'YOU PRESS THESE OFTEN')
    made = menu_item(d, 'Ctrl+Shift+L')
    check('context menu: a made key sits under them', bool(first and made) and made['y'] > first['y'] and made['y'] < label(d, 'VS CODE')['y'])
    mark = d.mark()
    if made:
        d.tap(made['x'] + made['w'] // 2, made['y'] + made['h'] // 2)
    check('context menu: picking it puts it on the bar', did(d, mark, 'ctx.set', app='code', id='used-ctrl+shift+l') is not None,
          str(d.acts(mark)))
    d.state('context', fixtures.day()['context'])
    d.settle()
    hold_key(d, 'Build')
    check('context menu: without any, no such section', not on_screen(d, 'YOU PRESS THESE OFTEN'))
    d.tap(400, 20)
    d.settle()


def test_approval(d: Deck) -> None:
    d.page(2)
    d.state('approval', fixtures.approval())
    d.settle()
    check('approval: Claude asking shows on any page', on_screen(d, 'Bash: npm run build') and on_screen(d, 'Approve', exact=True))
    title = label(d, 'Claude · flex-screen wants to')
    check('approval: under the status bar', title is not None and title['y'] > 44, str(title))
    d.shot(os.path.join(OUT, 'approval.png'))
    expect_act(d, 'approval: Approve answers it', 'Approve', 'claude.approve', exact=True, id='p1', always=False)
    check('approval: and the banner goes at once', not on_screen(d, 'Bash: npm run build'))

    d.state('approval', fixtures.approval('p2', 'Edit: page_dev.cpp'))
    d.settle()
    expect_act(d, 'approval: Always also keeps the rule', 'Always', 'claude.approve', exact=True, id='p2', always=True)

    d.state('approval', fixtures.approval('p3', 'WebFetch', always=False, count=2))
    d.settle()
    check('approval: no Always when Claude has no rule to suggest', not on_screen(d, 'Always', exact=True))
    check('approval: says when more are waiting', on_screen(d, 'more waiting'))
    expect_act(d, 'approval: Deny', 'Deny', 'claude.deny', exact=True, id='p3')

    d.state('approval', fixtures.approval('p4', 'Bash: ' + fixtures.LONG))
    d.settle()
    detail = label(d, 'Bash: A very long')
    check('approval: a long command stays on one line', detail is not None and detail['h'] <= 22, str(detail))
    d.offline()
    check('approval: offline it goes', not on_screen(d, 'Bash: A very long'))
    d.online()
    d.state('approval', fixtures.NO_APPROVAL)
    d.settle()
    check('approval: answered at the PC, it goes', not on_screen(d, 'Bash: A very long'))


def test_question(d: Deck) -> None:
    d.page(1)
    d.state('question', fixtures.question())
    d.settle()
    title = label(d, 'Claude · flex-screen asks')
    check('question: Claude asking shows on any page', title is not None and on_screen(d, 'Convert the other 12 files too?'))
    check('question: under the status bar', title is not None and title['y'] > 44, str(title))
    check('question: its options as buttons', all(on_screen(d, o, exact=True) for o in ('Yes', 'No', 'Only the icons')))
    d.shot(os.path.join(OUT, 'question.png'))
    expect_act(d, 'question: a tap answers it', 'Only the icons', 'claude.answer', exact=True, id='q1', picks=[2])
    check('question: and the card goes at once', not on_screen(d, 'Convert the other 12 files too?'))

    # Claude's own question: several asked together, options with notes, one taking several picks.
    d.state('question', fixtures.question('q2', 'Which checks should run?', ('Unit tests', 'Simulator', 'Lint'),
                                          notes=('Fast', 'Takes a minute', ''), multi=True, step=2, steps=3))
    d.settle()
    check('question: says which of several it is', on_screen(d, '2 of 3') and on_screen(d, 'pick any'))
    check("question: an option's note shows under it", on_screen(d, 'Takes a minute', exact=True))
    d.shot(os.path.join(OUT, 'question-multi.png'))
    mark = d.mark()
    tap(d, 'Unit tests', exact=True)
    tap(d, 'Lint', exact=True)
    check('question: picking several sends nothing yet', d.took('claude.answer', since=mark) is None)
    expect_act(d, 'question: Done sends the picks', 'Done', 'claude.answer', exact=True, id='q2', picks=[0, 2])

    d.state('question', fixtures.question('q3', count=2))
    d.state('approval', fixtures.approval())
    d.settle()
    banner = label(d, 'Claude · flex-screen wants to')
    card = label(d, 'Claude · flex-screen asks')
    check('question: under the approval banner when both show',
          banner is not None and card is not None and card['y'] > banner['y'] + 60, f'{banner} {card}')
    check('question: says when more are waiting', on_screen(d, 'more waiting'))
    d.state('approval', fixtures.NO_APPROVAL)
    d.settle()
    card = label(d, 'Claude · flex-screen asks')
    check('question: back up once the approval goes', card is not None and card['y'] < 70, str(card))
    mark = d.mark()
    close = [n for n in shown(d) if n.get('text') == CLOSE and n['y'] < 120]
    if close:
        tap_node(d, close[-1])
    check('question: X dismisses it', did(d, mark, 'claude.answer', id='q3', picks=[]) is not None)

    d.state('question', fixtures.note())
    d.settle()
    check('question: a note shows with OK', on_screen(d, 'All 12 files converted; 2 need a look') and on_screen(d, 'OK', exact=True))
    check('question: and no X', not [n for n in shown(d) if n.get('text') == CLOSE and n['y'] < 120])
    d.shot(os.path.join(OUT, 'note.png'))
    expect_act(d, "question: a note's Open brings up the session", 'Open', 'claude.open', exact=True, id='s1')
    expect_act(d, 'question: OK marks it read', 'OK', 'claude.answer', exact=True, id='n1', picks=[])
    check('question: and it goes', not on_screen(d, 'All 12 files converted'))

    d.state('question', fixtures.question('q4'))
    d.settle()
    d.offline()
    check('question: offline it goes', not on_screen(d, 'Convert the other 12 files too?'))
    d.online()
    d.state('question', fixtures.NO_QUESTION)
    d.settle()
    check('question: answered at the PC, it goes', not on_screen(d, 'Convert the other 12 files too?'))


def test_sessions(d: Deck) -> None:
    d.page(0)
    d.state('claude', fixtures.sessions())
    d.settle()
    tap(d, 'Claude needs you', exact=True)
    check('sessions: the board opens from the pill', on_screen(d, 'Claude sessions', exact=True))
    check('sessions: says how many and who needs you', on_screen(d, '5 sessions · 1 needs you'))
    rows = [label(d, p, exact=True) for p in ('api-gateway', 'flex-screen', 'icons', 'website', 'notes')]
    check('sessions: every session, needing you first', all(rows) and [r['y'] for r in rows] == sorted(r['y'] for r in rows),
          str([r and r['y'] for r in rows]))
    check('sessions: where each runs and its model', on_screen(d, 'VS Code · Sonnet 4.5') and on_screen(d, 'VS Code · SSH · Opus 5.5'))
    check('sessions: what it waits on', on_screen(d, 'Needs you · Bash: kubectl'))
    check("sessions: a long job's progress", on_screen(d, 'Converting icons · 7 of 12'))
    check('sessions: how long each has been at it', on_screen(d, '14 min', exact=True))
    stops = [n for n in shown(d) if n.get('text') == 'Stop']
    check('sessions: Stop only for those at work or waiting', len(stops) == 3, str(len(stops)))
    check('sessions: the usage figure', on_screen(d, '62%', exact=True) and on_screen(d, 'week 17%', exact=True))
    check('sessions: when it resets', on_screen(d, 'Resets at 12:42 · in 2 h'))
    check('sessions: when the limit comes at this pace', on_screen(d, 'At this pace you hit the limit around 12:18'))
    d.shot(os.path.join(OUT, 'sessions.png'))

    expect_act(d, 'sessions: a row brings up its window', 'website', 'claude.open', exact=True, id='s4')
    mark = d.mark()
    tap_node(d, stops[0])
    check('sessions: a tap on Stop only says Hold', on_screen(d, 'Hold', exact=True) and d.took('claude.stop', since=mark) is None)
    tap_node(d, stops[1], hold=True)
    check('sessions: holding Stop stops that session', did(d, mark, 'claude.stop', id='s1') is not None)
    s = fixtures.sessions()
    s['sessions'][1]['stopping'] = True
    d.state('claude', s)
    d.settle()
    check('sessions: says it stops before its next step', on_screen(d, 'Stopping before its next step'))

    d.state('usage', fixtures.usage(30, 4.0, limit=False))
    d.settle()
    check('sessions: a slow pace lasts to the reset', on_screen(d, 'At this pace it lasts until the reset'))
    d.state('usage', {'five': -1, 'week': -1, 'start': 0, 'resets': 0, 'eta': 0, 'rate': 0, 'points': []})
    d.settle()
    check('sessions: no usage known, it says why', on_screen(d, 'Shows once Claude Code is signed in'))
    d.state('claude', {'sessions': []})
    d.settle()
    check('sessions: none, it says where they come from', on_screen(d, 'No Claude sessions.'))
    tap(d, CLOSE, exact=True)

    d.page(2)
    d.state('claude', fixtures.sessions())
    d.settle()
    tap(d, 'Claude needs your approval')
    check("sessions: the Dev page's banner opens it too", on_screen(d, 'Claude sessions', exact=True))
    d.tap(400, 20)  # beside the panel: over the status bar
    check('sessions: a tap beside it closes it', not on_screen(d, 'Claude sessions', exact=True))


def firmware_version() -> str:
    with open(os.path.join(HERE, '..', 'firmware', 'src', 'pc.h'), encoding='utf-8') as f:
        return re.search(r'kFirmwareVersion = "([^"]+)"', f.read()).group(1)


def bar_clock(d: Deck) -> dict | None:
    """The rail's time, "HH:MM", at its top."""
    return next((n for n in shown(d) if n['y'] < 30 and n['x'] < RAIL_W and re.fullmatch(r'\d\d:\d\d', n.get('text', ''))), None)


def counter(d: Deck, name: str) -> str:
    """A diagnostics counter's figure: the label just before its name, in its cell."""
    nodes = shown(d)
    for i, n in enumerate(nodes):
        if n.get('text') == name and i > 0:
            return nodes[i - 1].get('text', '')
    return ''


def figure_ink(d: Deck, f: bytes, n: dict) -> tuple[int, int]:
    """Where a label's text is brightest: its colour, without the anti-aliased edges."""
    spots = [(x, y) for y in range(n['y'], n['y'] + n['h']) for x in range(n['x'], n['x'] + n['w'])]
    return max(spots, key=lambda p: sum(d.pixel(f, *p)))


def overlay_labels(d: Deck) -> list[dict]:
    """The labels showing on LVGL's top layer (the overlays), not the page under them."""
    d.settle()
    mark, token = d.mark(), int(time.time() * 1000) % 1000000
    d.send({'t': 'sim.dump', 'id': token})
    reply = d.wait_for(lambda m: m.get('t') == 'sim.tree' and m.get('id') == token, since=mark)
    out, hidden_depth = [], None
    for n in reply['top']:
        if hidden_depth is not None and n['d'] > hidden_depth:
            continue
        hidden_depth = n['d'] if n.get('hidden') else None
        if hidden_depth is None and 'text' in n:
            out.append(n)
    return out


def open_diagnostics(d: Deck) -> bool:
    clock = bar_clock(d)
    if clock:
        tap_node(d, clock, hold=True)
    return on_screen(d, 'Deck diagnostics', exact=True)


def test_diagnostics(d: Deck) -> None:
    d.page(0)
    clock = bar_clock(d)
    check('diagnostics: the bar shows the clock', clock is not None)
    if clock:
        tap_node(d, clock)
    check('diagnostics: a tap on the clock does nothing', not on_screen(d, 'Deck diagnostics', exact=True))
    check('diagnostics: holding the clock opens them', open_diagnostics(d))
    check('diagnostics: the firmware version', on_screen(d, firmware_version(), exact=True), firmware_version())
    check('diagnostics: why it last restarted', on_screen(d, 'Last restart: power-on', exact=True))
    uptime = next((n['text'] for n in shown(d) if re.fullmatch(r'\d+ s|\d+ min \d+ s', n.get('text', ''))), '')
    check('diagnostics: how long it has been up', uptime != '')
    check('diagnostics: since when, by the clock', any(re.fullmatch(r'Since \d\d:\d\d', n.get('text', '')) for n in shown(d)))
    check('diagnostics: the frame rate', any(re.fullmatch(r'\d+ fps', n.get('text', '')) for n in shown(d)))
    peak = next((n['text'] for n in shown(d) if n.get('text', '').startswith('Up to ')), '')
    check('diagnostics: the busiest second of the last minute', re.fullmatch(r'Up to [1-9]\d* fps · last minute', peak) is not None, peak)
    check('diagnostics: how long a frame takes', any(re.fullmatch(r'Draw \d+ ms avg, \d+ ms worst', n.get('text', '')) for n in shown(d)))
    check('diagnostics: free internal RAM', on_screen(d, '146 KB', exact=True))
    check('diagnostics: its low point and largest block', on_screen(d, 'Internal RAM, lowest 146 KB', exact=True)
          and on_screen(d, 'Largest free block 87 KB', exact=True))
    check('diagnostics: free PSRAM', on_screen(d, 'PSRAM 6.2 MB, lowest 6.2 MB', exact=True))
    check('diagnostics: the link is up', on_screen(d, 'Connected for '))
    lines = counter(d, 'Lines')
    check('diagnostics: lines taken in', lines.isdigit() and int(lines) > 10 or lines.endswith('k'), lines)
    check('diagnostics: no errors yet', all(counter(d, n) == '0' for n in ('Bad lines', 'Cut lines', 'Bytes lost', 'Bad parts'))
          and on_screen(d, 'No errors since the deck started.', exact=True))
    d.shot(os.path.join(OUT, 'diagnostics.png'))

    d.send_raw(b'{"t":"state",\n')  # garbled
    d.send_raw(b'{"t":"ping","pad":"' + b'x' * 9000 + b'"}\n')  # past the 8 KB a line may take
    d.send({'t': 'img', 'id': 'bad-art', 'part': 0, 'parts': 2, 'w': 10, 'h': 10, 'fmt': 'rgb565', 'off': 0, 'b64': 'AAAA'})
    time.sleep(1.2)  # the figures move once a second
    check('diagnostics: a garbled line counts', counter(d, 'Bad lines') == '1', counter(d, 'Bad lines'))
    check('diagnostics: a line too long counts', counter(d, 'Cut lines') == '1', counter(d, 'Cut lines'))
    check('diagnostics: a bad image part counts', counter(d, 'Bad parts') == '1', counter(d, 'Bad parts'))
    check('diagnostics: the last error, and when', on_screen(d, 'Last error, just now: img bad-art: bad part 0', exact=True))
    f = d.frame()
    figure = next((n for n in shown(d) if n.get('text') == '1'), None)
    check('diagnostics: errors stand out in amber', figure is not None and near(d.pixel(f, *figure_ink(d, f, figure)), AMBER, 40))
    check('diagnostics: the uptime moves on', next((n['text'] for n in shown(d) if re.fullmatch(r'\d+ s|\d+ min \d+ s', n.get('text', ''))), '') != uptime)
    cut = [n['text'] for n in overlay_labels(d) if n.get('text', '').endswith('...')]
    check('diagnostics: nothing cut short', not cut, str(cut))
    d.shot(os.path.join(OUT, 'diagnostics-errors.png'))

    drops = counter(d, 'Drops')
    d.offline()
    check('diagnostics: offline, it stays open', on_screen(d, 'Deck diagnostics', exact=True))
    check('diagnostics: and says for how long', on_screen(d, 'Offline for '))
    check('diagnostics: a drop counts', drops.isdigit() and counter(d, 'Drops') == str(int(drops) + 1), f'{drops} -> {counter(d, "Drops")}')
    tap(d, CLOSE, exact=True)
    check('diagnostics: its X closes them', not on_screen(d, 'Deck diagnostics', exact=True))
    check('diagnostics: they open offline too', open_diagnostics(d))
    d.online()
    time.sleep(1.2)
    check('diagnostics: back online', on_screen(d, 'Connected for '))
    mark = d.mark()
    d.tap(400, 20)  # beside the panel: over the status bar
    check('diagnostics: a tap beside them closes them', not on_screen(d, 'Deck diagnostics', exact=True))
    check('diagnostics: and goes no further', not d.acts(mark))


def texts_now(d: Deck) -> list[str]:
    """The texts on screen right now, without waiting for a still screen as tree() does: the
    demo keeps it moving."""
    mark, token = d.mark(), int(time.time() * 1000) % 1000000
    d.send({'t': 'sim.dump', 'id': token})
    reply = d.wait_for(lambda m: m.get('t') == 'sim.tree' and m.get('id') == token, since=mark)
    out, hidden_depth = [], None
    for n in reply['screen'] + reply['top']:
        if hidden_depth is not None and n['d'] > hidden_depth:
            continue
        hidden_depth = n['d'] if n.get('hidden') else None
        if hidden_depth is None and 0 <= n['x'] < 800 and 0 <= n['y'] < 480 and 'text' in n:
            out.append(n['text'])
    return out


def test_demo(d: Deck) -> None:
    """The diagnostics' Demo: about a minute of the deck by itself, with made-up data. It sends
    the PC nothing, holds back what the PC sends meanwhile, and a touch ends it with the deck
    as it was, then what the PC sent."""
    has = lambda texts, text: any(text in t for t in texts)
    d.page(3)  # Windows: where it should come back to
    check('demo: the diagnostics have a Demo button', open_diagnostics(d) and on_screen(d, 'Demo'))
    mark = d.mark()
    tap(d, 'Demo')
    started = time.time()
    time.sleep(0.4)
    texts = texts_now(d)
    check('demo: it starts, the diagnostics closed', 'DEMO' in texts and 'Deck diagnostics' not in texts)
    check('demo: a caption says what the step shows', has(texts, 'Your day at a glance') and '1/14' in texts)
    check('demo: Today, with its own made-up day', 'Low Orbit' in texts and 'Weightless' not in texts)
    d.state('media', {**fixtures.day()['media'], 'title': 'Sent meanwhile'})
    d.toast('Toast meanwhile')
    time.sleep(0.3)
    texts = texts_now(d)
    check('demo: what the PC sends waits', not has(texts, 'Sent meanwh') and 'Toast meanwhile' not in texts)
    time.sleep(max(0.0, started + 8.5 - time.time()))
    texts = texts_now(d)
    check('demo: then the day, opened from Up next by itself', has(texts, 'Tap Up next') and '2/14' in texts
          and 'Coffee with Noa' in texts and 'Sprint planning' in texts, f'{time.time() - started:.1f} s in')
    d.tap(450, 240)  # over the day view: it doesn't take it
    d.settle()
    check('demo: a touch ends it', not on_screen(d, 'DEMO', exact=True))
    check('demo: back on the page it started from', on_screen(d, 'PrusaSlicer', exact=True))
    check('demo: then the toast the PC sent meanwhile', on_screen(d, 'Toast meanwhile', exact=True))
    d.page(0)
    check('demo: and its state', on_screen(d, 'Sent meanwh') and not on_screen(d, 'Low Orbit', exact=True))
    check('demo: the rest as it was', on_screen(d, 'Marconi Union', exact=True))
    check('demo: nothing went to the PC', not d.acts(mark), str(d.acts(mark)))

    d.send({'t': 'demo'})  # the companion's test endpoint
    time.sleep(0.4)
    started = 'DEMO' in texts_now(d)
    d.send({'t': 'page', 'i': 0})
    d.settle()
    check("demo: the companion's demo starts it, its page ends it", started and not on_screen(d, 'DEMO', exact=True))

    d.offline()
    check('demo: offline too', open_diagnostics(d) and tap(d, 'Demo'))
    time.sleep(0.4)
    texts = texts_now(d)
    check('demo: offline, drawn as if the PC were there', 'Low Orbit' in texts and not has(texts, 'PC offline'))
    d.tap(450, 240)
    d.settle()
    check('demo: and offline again after', on_screen(d, 'PC offline') and not on_screen(d, 'DEMO', exact=True))
    d.online()
    d.settle()


def near(a: tuple[int, int, int], b: int, slack: int = 24) -> bool:
    return all(abs(a[i] - (b >> (16 - 8 * i) & 255)) <= slack for i in range(3))


AMBER, RED, BLUE = 0xF5A623, 0xFF4D4D, 0x4C8DFF  # the base palette's amber, and the glow's own


def glow(d: Deck, settle: bool = True) -> tuple[int, int, int] | None:
    """The frame's colour if all four edges are lit in it, else None."""
    f = d.frame(settle=settle)
    edges = [d.pixel(f, 400, 0), d.pixel(f, 400, 479), d.pixel(f, 0, 240), d.pixel(f, 799, 240)]
    inside = d.pixel(f, 400, 14), d.pixel(f, 14, 240)
    lit = all(edges[0] == e for e in edges) and edges[0] != inside[0]
    return edges[0] if lit and all(max(e) > 150 for e in edges) else None


def blink(d: Deck, secs: float = 1.2) -> list[tuple[int, int, int]]:
    """The lit frames seen while it blinks: screen grabs as fast as they come, mid-animation."""
    end, seen = time.time() + secs, []
    while time.time() < end:
        c = glow(d, settle=False)
        if c:
            seen.append(c)
    return seen


def glowed(d: Deck, mark: int, what: str) -> bool:
    return f'glow: {what}' in d.logs(mark)


def test_glow(d: Deck) -> None:
    d.page(0)
    check('glow: none on a quiet day', glow(d) is None)

    mark = d.mark()
    d.state('call', fixtures.call('teams'))
    lit = blink(d)
    check('glow: blinks blue when a call starts', any(near(c, BLUE) for c in lit), str(lit[:3]))
    check('glow: and says so in the log', glowed(d, mark, 'blue, a call'))
    time.sleep(1.8)
    check('glow: then fades away', glow(d) is None)
    mark = d.mark()
    call = fixtures.call('teams')
    call['secs'] += 30
    d.state('call', call)
    d.settle()
    check('glow: not again while the call goes on', not [m for m in d.logs(mark) if m.startswith('glow:')])

    mark = d.mark()
    d.state('build', {'state': 'failed', 'cmd': 'npm run build', 'ms': 61000, 'secs': 5, 'history': 'PPPPF'})
    lit = blink(d)
    check('glow: blinks red when a build fails', any(near(c, RED) for c in lit), str(lit[:3]))
    check('glow: its log', glowed(d, mark, 'red, the build failed'))

    mark = d.mark()
    d.state('claude', fixtures.sessions())
    lit = blink(d, 1.0)
    check('glow: blinks amber when Claude comes to need you', any(near(c, AMBER) for c in lit), str(lit[:3]))
    check('glow: its log', glowed(d, mark, 'amber, Claude needs you'))
    d.tap(700, 6)  # the volume button's top edge, under the frame while it fades
    check('glow: taps go through it', did(d, mark, 'vol.mute') is not None)
    d.shot(os.path.join(OUT, 'glow-claude.png'))
    time.sleep(3)
    check('glow: and it fades while Claude still waits', glow(d) is None)

    # Over the idle clock: Claude busy, then needing you again.
    d.state('claude', fixtures.day()['claude'])
    d.settings('base', clock_s=2, off_s=600)
    mark = d.mark()
    d.state('pc', {'locked': False, 'idle': 2})
    try:
        d.wait_for(lambda m: m.get('t') == 'log' and m.get('msg') == 'idle: clock', since=mark, timeout=6)
    except TimeoutError:
        pass
    mark = d.mark()
    d.state('claude', fixtures.sessions())
    lit = blink(d)
    check('glow: it blinks over the idle clock', any(near(c, AMBER) for c in lit), str(lit[:3]))
    time.sleep(2)
    d.shot(os.path.join(OUT, 'glow-clock-after.png'))
    check('glow: and leaves the clock as it was', glow(d) is None)
    d.tap(400, 240)
    d.settings('base', clock_s=600, off_s=1800)
    d.state('pc', {'locked': False, 'idle': 1})

    mark = d.mark()
    d.state('build', {'state': 'running', 'cmd': 'npm run build', 'ms': 0, 'secs': 1, 'history': 'PPPPF'})
    d.state('build', {'state': 'failed', 'cmd': 'npm run build', 'ms': 61000, 'secs': 11 * 60, 'history': 'PPPFF'})
    d.settle()
    check('glow: not for a build that failed a while ago', not glowed(d, mark, 'red, the build failed'))

    d.state('call', {'app': ''})
    d.state('claude', fixtures.day()['claude'])
    d.settle()
    d.offline()
    mark = d.mark()
    d.state('call', fixtures.call('zoom'))  # coming back with a call that started meanwhile
    d.online()
    lit = blink(d)
    check('glow: back online, it blinks for what started meanwhile', any(near(c, BLUE) for c in lit), str(lit[:3]))
    time.sleep(2)
    d.offline()
    mark = d.mark()
    d.online()
    d.settle()
    check("glow: but not again for what's still the same", not [m for m in d.logs(mark) if m.startswith('glow:')])


def test_progress(d: Deck) -> None:
    d.page(2)
    d.state('claude', {'sessions': [{'id': 's1', 'project': 'flex-screen', 'state': 'working', 'detail': 'Edit: icons.cpp', 'secs': 40,
                                     'progress': {'label': 'Converting icons', 'done': 3, 'total': 12}}]})
    d.settle()
    check('progress: the status bar counts', on_screen(d, 'Claude 3/12', exact=True))
    check('progress: the Dev banner says how far', on_screen(d, 'Converting icons · 3 of 12'))
    d.shot(os.path.join(OUT, 'progress.png'))
    d.state('claude', {'sessions': [{'id': 's1', 'project': 'flex-screen', 'state': 'working', 'detail': 'Edit: icons.cpp', 'secs': 40}]})
    d.settle()
    check('progress: without it, the step again', on_screen(d, 'Claude working', exact=True) and on_screen(d, 'Edit: icons.cpp'))


def test_scripts(d: Deck) -> None:
    """flexctl: a script's note, question and progress, under the name it gave."""
    d.page(0)
    d.state('question', {**fixtures.note('n7', 'Backup done: 412 files', 'done'), 'session': '', 'project': '', 'from': 'Backup'})
    d.settle()
    check("scripts: a script's note goes by its name, not Claude's",
          on_screen(d, 'Backup', exact=True) and on_screen(d, 'Backup done: 412 files') and not on_screen(d, 'Claude ·'))
    check('scripts: with OK, and no Open (no session to bring up)',
          on_screen(d, 'OK', exact=True) and not on_screen(d, 'Open', exact=True))
    d.shot(os.path.join(OUT, 'script-note.png'))
    expect_act(d, "scripts: OK marks the script's note read", 'OK', 'claude.answer', exact=True, id='n7', picks=[])

    d.state('question', {**fixtures.question('q7', 'Deploy to production?', ('Yes', 'No')), 'session': '', 'project': '', 'from': 'Deploy'})
    d.settle()
    check("scripts: a script's question says who asks", on_screen(d, 'Deploy asks', exact=True))
    expect_act(d, 'scripts: a tap answers it', 'Yes', 'claude.answer', exact=True, id='q7', picks=[0])
    d.state('question', fixtures.NO_QUESTION)

    d.state('job', {'label': 'Uploading', 'from': 'Backup', 'done': 3, 'total': 12})
    d.settle()
    check("scripts: a script's progress takes the build pill", on_screen(d, 'Uploading 3/12') and not on_screen(d, 'Build passed'))
    d.shot(os.path.join(OUT, 'script-progress.png'))
    d.state('job', {'label': '', 'from': 'Backup', 'done': 5, 'total': 12})
    d.settle()
    check('scripts: without a label, the name it gave', on_screen(d, 'Backup 5/12'))
    pill = label(d, 'Backup 5/12')
    if pill:
        tap_node(d, pill)
    check('scripts: the pill opens Dev, as the build pill does', on_screen(d, 'TERMINAL'))
    # Beside a meeting soon, Claude and a call the status line is full: the job stays.
    d.state('job', {'label': 'Uploading the photos', 'from': 'Backup', 'done': 7, 'total': 12})
    d.load(fixtures.stress())
    d.settle()
    check("scripts: in a full status line the progress stays (the meeting's title gives way)", on_screen(d, '7/12'))
    d.shot(os.path.join(OUT, 'script-progress-stress.png'))
    d.load(fixtures.day())
    d.state('job', {'label': '', 'from': '', 'done': 0, 'total': 0})
    d.settle()
    check('scripts: done, the build pill again', on_screen(d, 'Build passed') and not on_screen(d, '7/12'))
    d.page(0)


def raw_tree(d: Deck) -> list[dict]:
    """The objects now, without waiting for the screen to be still (a hold's ring moves)."""
    mark = d.mark()
    token = int(time.time() * 1000) % 1000000
    d.send({'t': 'sim.dump', 'id': token})
    reply = d.wait_for(lambda m: m.get('t') == 'sim.tree' and m.get('id') == token, since=mark)
    return reply['screen'] + reply['top']


def ring_shown(d: Deck) -> dict | None:
    """The hold ring, if it's showing: the top layer's 92 px arc."""
    out, hidden_depth = None, None
    for n in raw_tree(d):
        if hidden_depth is not None and n['d'] > hidden_depth:
            continue
        hidden_depth = n['d'] if n.get('hidden') else None
        if hidden_depth is None and n['c'] == 'arc' and n['w'] == 92:
            out = n
    return out


def test_hold_ring(d: Deck) -> None:
    d.page(3)
    tile = label(d, 'Chrome', exact=True) or label(d, 'Chrome')
    if not tile:
        check('hold ring: a window tile to hold', False, 'no Chrome tile')
        return
    x, y = tile['x'] + tile['w'] // 2, tile['y'] + tile['h'] // 2
    mark = d.mark()
    d.touch(x, y, True)
    time.sleep(0.25)
    ring = ring_shown(d)
    check('hold ring: fills round the finger while it holds', ring is not None and abs(ring['x'] + 46 - x) <= 2 and abs(ring['y'] + 46 - y) <= 2,
          str(ring))
    d.shot(os.path.join(OUT, 'hold-ring.png'))
    time.sleep(0.4)
    check('hold ring: gone once the hold has done its thing', ring_shown(d) is None)
    d.touch(x, y, False)
    d.settle()
    check('hold ring: the hold still does it (closes the window)', did(d, mark, 'win.close') is not None)

    # The tile's gone now (a hold closes its window at once): a context bar key next.
    d.load(fixtures.day())
    d.page(0)
    key = bar_key(d, 'Commands')
    if not key:
        check('hold ring: a context bar key to hold', False, 'no Commands key')
        return
    x, y = key['x'] + key['w'] // 2, key['y'] + key['h'] // 2
    mark = d.mark()
    d.touch(x, y, True)
    time.sleep(0.06)
    d.touch(x, y, False)
    time.sleep(0.12)
    check('hold ring: a tap never shows it', ring_shown(d) is None)
    check('hold ring: and the tap still runs the key', did(d, mark, 'ctx.run', id='commands') is not None)
    d.settle()

    d.touch(x, y, True)
    time.sleep(0.2)
    shown_before = ring_shown(d) is not None
    d.touch(x, y - 40, True)
    time.sleep(0.05)
    gone = ring_shown(d) is None
    d.touch(x, y - 40, False)
    d.settle()
    check('hold ring: sliding away hides it', shown_before and gone, f'{shown_before} {gone}')
    if menu_open(d):
        tap_node(d, {'x': 400, 'y': 60, 'w': 0, 'h': 0})  # beside the menu closes it
    d.load(fixtures.day())
    d.page(0)


LOCK = ''  # the icon font's lock


def test_context_lock(d: Deck) -> None:
    d.page(0)
    chip = bar_key(d, 'VS Code')
    mark = d.mark()
    if chip:
        tap_node(d, chip, hold=True)
    check("context lock: a hold on the app's name locks the bar", did(d, mark, 'ctx.lock', on=True) is not None, str(d.acts(mark)))
    check("context lock: and doesn't go to the app's page", not on_screen(d, 'TERMINAL'))
    d.state('context', {**fixtures.day()['context'], 'locked': True})
    d.settle()
    lock = next((n for n in shown(d) if n.get('text') == LOCK and n['y'] >= BAR_TOP), None)
    chip = bar_key(d, 'VS Code')
    check("context lock: locked, a lock on the app's icon", lock is not None and chip is not None and lock['x'] < chip['x'], f'{lock} {chip}')
    check('context lock: the name in full beside it', chip is not None)
    d.shot(os.path.join(OUT, 'context-locked.png'))
    mark = d.mark()
    if chip:
        tap_node(d, chip, hold=True)
    check('context lock: another hold lets it follow again', did(d, mark, 'ctx.lock', on=False) is not None, str(d.acts(mark)))
    if chip:
        tap_node(d, chip)
        d.settle()
    check("context lock: a tap still goes to the app's page", on_screen(d, 'TERMINAL'))
    d.state('context', {**fixtures.day()['context'], 'app': 'A very long application name', 'key': 'long', 'locked': True})
    d.settle()
    lock = next((n for n in shown(d) if n.get('text') == LOCK and n['y'] >= BAR_TOP), None)
    name = next((n for n in shown(d) if n.get('text', '').startswith('A very') and n['y'] >= BAR_TOP), None)
    check('context lock: with a long name too, inside the bar', lock is not None and name is not None and lock['y'] + lock['h'] <= 474,
          f'{lock} {name}')
    d.state('context', fixtures.day()['context'])
    d.settle()
    check('context lock: unlocked, no lock', not any(n.get('text') == LOCK and n['y'] >= BAR_TOP for n in shown(d)))
    d.page(0)


def test_picker(d: Deck) -> None:
    d.page(0)
    d.send({'t': 'pick', 'id': 'claude-model', 'title': 'Model', 'current': 'opus', 'choices': [
        {'id': c, 'name': n} for c, n in [('default', 'Default'), ('opus', 'Opus'), ('sonnet', 'Sonnet'), ('haiku', 'Haiku'),
                                          ('fable', 'Fable'), ('opusplan', 'Opus plan')]]})
    d.settle()
    check("picker: a key's list opens above the bar", on_screen(d, 'Model', exact=True) and menu_item(d, 'Sonnet') is not None)
    check('picker: no Remove or Reset there', not on_screen(d, 'Remove', exact=True) and not on_screen(d, 'Reset', exact=True))
    d.shot(os.path.join(OUT, 'picker.png'))
    item = menu_item(d, 'Sonnet')
    mark = d.mark()
    if item:
        d.tap(item['x'] + item['w'] // 2, item['y'] + item['h'] // 2)
    check('picker: a choice goes to the companion', did(d, mark, 'ctx.pick', id='claude-model', choice='sonnet') is not None,
          str(d.acts(mark)))
    check('picker: and the list closes', menu_item(d, 'Sonnet') is None)


def test_offline(d: Deck) -> None:
    d.page(0)
    d.offline()
    check('offline: the bar says so', on_screen(d, 'PC offline'))
    mark = d.mark()
    tap(d, PLAY, exact=True, below=180) or tap(d, PAUSE, exact=True, below=180)
    keys = keys_since(d, mark)
    d.shot(os.path.join(OUT, 'offline-today.png'))  # after: any message, even this, counts as the PC being back
    check('offline: play/pause is a media key', any(k.get('usage') == 0xCD for k in keys), str(keys))
    d.online()
    d.settle()
    check('offline: back online', not on_screen(d, 'PC offline'))


def ota_send(d: Deck, image: bytes, start: int = 0, end: int | None = None) -> int:
    """Sends the image's 4 KB parts from `start` to `end`, each after the last one's ack, as
    the companion does; returns where it stopped."""
    end = len(image) if end is None else end
    for off in range(start, end, 4096):
        mark = d.mark()
        part = image[off:off + 4096]
        d.send({'t': 'ota_data', 'off': off, 'b64': base64.b64encode(part).decode()})
        d.wait_for(lambda m: m.get('t') in ('ota_ack', 'ota_error'), since=mark)
    return min(end, len(image))


def ota_start(d: Deck, image: bytes, version: str = '0.21.0') -> dict:
    mark = d.mark()
    d.send({'t': 'ota', 'size': len(image), 'md5': hashlib.md5(image).hexdigest(), 'version': version})
    return d.wait_for(lambda m: m.get('t') in ('ota_ready', 'ota_error'), since=mark)


def test_firmware_update(d: Deck) -> None:
    """Last: a finished update restarts the simulator, as the deck restarts into its new firmware."""
    d.page(0)
    image = b'\xe9' + bytes(3 * 4096 + 99)  # three full parts and a short one
    check('firmware: the deck takes an update', ota_start(d, image).get('t') == 'ota_ready')
    d.settle()
    check('firmware: it says so over every page', on_screen(d, 'Updating the deck', exact=True))
    check('firmware: with the version coming', on_screen(d, 'Firmware 0.21.0  ·  0%'))
    check('firmware: and to keep it plugged in', on_screen(d, 'Keep it plugged in'))
    mark = d.mark()
    tap(d, PLAY, exact=True, below=180) or tap(d, PAUSE, exact=True, below=180) or d.tap(400, 300)
    check('firmware: taps stop at the progress screen', did(d, mark, 'media.toggle', timeout=0.5) is None)
    ota_send(d, image, 0, 2 * 4096)
    d.settle()
    check('firmware: progress moves', on_screen(d, 'Firmware 0.21.0  ·  66%'))
    d.shot(os.path.join(OUT, 'firmware-update.png'))

    mark = d.mark()
    d.send({'t': 'ota_data', 'off': 0, 'b64': base64.b64encode(image[:4096]).decode()})
    error = d.wait_for(lambda m: m.get('t') in ('ota_ack', 'ota_error'), since=mark)
    check('firmware: a part out of order ends it', error.get('msg') == 'out-of-order chunk', str(error))
    d.settle()
    check('firmware: and the screen comes back', not on_screen(d, 'Updating the deck', exact=True))

    mark = d.mark()
    d.send({'t': 'ota', 'size': len(image)})  # older companions send no md5 or version
    d.settle()
    check('firmware: without a version it says new firmware', on_screen(d, 'New firmware  ·  0%'))
    try:
        error = d.wait_for(lambda m: m.get('t') == 'ota_error', since=mark, timeout=25)
    except TimeoutError:
        error = {}
    check('firmware: an update that stops coming is given up', error.get('msg') == 'the update stopped coming', str(error))
    d.settle()
    check('firmware: and the screen comes back after that too', not on_screen(d, 'Updating the deck', exact=True))

    mark = d.mark()
    d.send({'t': 'ota', 'size': 8192})
    d.wait_for(lambda m: m.get('t') in ('ota_ready', 'ota_error'), since=mark)
    mark = d.mark()
    d.send({'t': 'ota_data', 'off': 0, 'b64': base64.b64encode(b'\x00' * 4096).decode()})
    error = d.wait_for(lambda m: m.get('t') in ('ota_ack', 'ota_error'), since=mark)
    check('firmware: something not a firmware image is refused', error.get('t') == 'ota_error', str(error))

    ota_start(d, image)
    ota_send(d, image)
    mark = d.mark()
    sent = time.time()
    d.send({'t': 'ota_end'})
    done = d.wait_for(lambda m: m.get('t') in ('ota_done', 'ota_error'), since=mark)
    check('firmware: a whole image is taken', done.get('t') == 'ota_done', str(done))
    # It says "Installing" for 0.4 s before it writes (too short to read the screen here: the
    # restart comes first).
    check('firmware: and says it installs before it writes', time.time() - sent >= 0.35, f'{time.time() - sent:.2f} s')
    d.restarted = True
    d.reconnect()
    hello = d.sync()
    check('firmware: the deck restarts and says hello', hello.get('reset') == 'software', str(hello))
    d.settle()
    check('firmware: with its screen back', not on_screen(d, 'Updating the deck', exact=True))


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    with Deck.launch(log_path=os.path.join(OUT, 'sim.log')) as d:
        d.sync()
        d.settings('base')
        d.load(fixtures.day(), fixtures.ICONS, fixtures.NOW, fixtures.TZ_MIN)
        for test in (test_today, test_icon_asks, test_day, test_pages, test_status_bar, test_audio, test_seek, test_media_accent, test_dev, test_windows, test_desktop_map, test_tile_drag, test_capture, test_tasks, test_task_sources, test_numpad,
                     test_toast, test_call, test_zoom, test_cheat_sheet, test_face, test_launcher, test_shortcut_actions, test_context_bar, test_context_menu, test_approval, test_question, test_progress, test_scripts, test_hold_ring, test_context_lock, test_sessions, test_picker, test_diagnostics, test_demo, test_glow, test_idle, test_offline, test_firmware_update):
            try:
                d.load(fixtures.day())  # each starts from the same day
                test(d)
            except Exception:
                check(test.__name__, False, traceback.format_exc(limit=3))
            if not d.alive():  # a restart it was told to do (test_firmware_update) is fine
                check('the simulator is still running', False, f'exit code {d.process.returncode}')
                break
    failed = [r for r in results if not r[1]]
    print(f'\n{len(results) - len(failed)} passed, {len(failed)} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
