"""Every feature of the deck, driven in the simulator: taps, holds and drags on each page,
checked against what the deck sends the companion (or types over USB), plus the overlays
(toast, call panel, cheat sheet, idle screen) and going offline. Screenshots of the states
worth a look land in out/tests/.

    python test_deck.py        # prints PASS/FAIL per check; exit code 1 if any failed
"""
from __future__ import annotations

import os
import sys
import time
import traceback

import fixtures
from deck import Deck, HERE

OUT = os.path.join(HERE, 'out', 'tests')
PREV, PLAY, PAUSE, NEXT = '', '', '', ''
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

def test_today(d: Deck) -> None:
    d.page(0)
    expect_act(d, 'today: Join opens the next meeting', 'Join', 'cal.join', id='e1')
    expect_act(d, 'today: play/pause', PAUSE, 'media.toggle', exact=True, below=180)
    expect_act(d, 'today: next track', NEXT, 'media.next', exact=True, below=180)
    expect_act(d, 'today: previous track', PREV, 'media.prev', exact=True, below=180)
    expect_act(d, 'today: Recent tile runs it again', 'Obsidian', 'recent.run', id='a2', below=180)
    mark = d.mark()
    d.drag(60, 336, 200, 336)
    check('today: volume slider sets the volume', did(d, mark, 'vol.set') is not None)
    tap(d, 'to do from mail')
    check('today: the mail bar opens Capture', on_screen(d, 'FROM MAIL'))
    d.page(0)
    d.shot(os.path.join(OUT, 'today.png'))


def test_status_bar(d: Deck) -> None:
    d.page(0)
    expect_act(d, 'bar: volume button mutes', '42%', 'vol.mute')
    mark = d.mark()
    d.tap(767, 21)
    check('bar: mic button mutes the mic', did(d, mark, 'mic.mute') is not None)
    expect_act(d, 'bar: Claude pill opens the session', 'Claude working', 'claude.open', id='s1')
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
    expect_act(d, 'audio: Teams mute button', 'Teams', 'mixer.mute', exact=True, dy=26, app='ms-teams')
    spotify = label(d, 'Spotify', exact=True, below=150)
    mark = d.mark()
    if spotify:
        x = spotify['x'] + spotify['w'] // 2
        d.drag(x, 250, x, 200)
    hit = did(d, mark, 'mixer.set', app='spotify')
    check('audio: dragging a fader sets that app', hit is not None, str(d.acts(mark)))
    d.shot(os.path.join(OUT, 'audio.png'))


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


def test_capture(d: Deck) -> None:
    d.page(4)
    expect_act(d, 'capture: tap a clip pastes it', 'ssh deck', 'clip.paste', id='c1')
    expect_act(d, 'capture: hold a clip pins it', '#5AB4FF', 'clip.pin', hold=True, id='c4')
    expect_act(d, 'capture: Clear', 'Clear', 'clip.clear', exact=True)
    title = label(d, 'Send the signed lease')  # its box is just left of the title
    expect_act(d, 'capture: ticking a mail task', 'Send the signed lease', 'task.done', dx=-(title['w'] // 2 + 26) if title else 0,
               list='L1', id='t2', done=True)
    expect_act(d, 'capture: Open Gmail', 'Open Gmail', 'mail.open')
    expect_act(d, "capture: Obsidian today's note", "Today's note", 'obs.today')
    expect_act(d, 'capture: a recent note', 'Inbox', 'obs.open', exact=True, path='Inbox.md')
    d.shot(os.path.join(OUT, 'capture.png'))


def test_numpad(d: Deck) -> None:
    d.page(5)
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


def test_launcher(d: Deck) -> None:
    d.page(0)
    button = next((n for n in shown(d) if n['y'] >= 436 and n['w'] == 58), None)
    check('launcher: its button ends the tab bar', button is not None and button['x'] + button['w'] <= 790
          and abs(button['x'] + button['w'] // 2 - fixtures.LAUNCHER[0]) <= 2, str(button))
    tabs = [n for n in shown(d) if n['y'] >= 436 and n.get('text', '').strip().endswith(('Today', 'Numpad'))]
    check('launcher: all six tabs still fit beside it', len(tabs) == 2 and all(t['x'] + t['w'] < 730 for t in tabs), str(tabs))
    d.tap(*fixtures.LAUNCHER)
    check('launcher: the button opens it', launcher_open(d))
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

    tap(d, 'Capture', below=436)
    check('launcher: the tabs still switch pages', on_screen(d, 'FROM MAIL'))
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


BAR_TOP = 384  # the context bar's top edge


def bar_key(d: Deck, name: str) -> dict | None:
    """A context bar key's label (the bar is below the pages)."""
    return next((n for n in shown(d) if n.get('text') == name and n['y'] >= BAR_TOP), None)


def overlapping_bar(d: Deck) -> list[str]:
    """Page content that runs into the context bar."""
    return [f"{n.get('text') or n['c']}@{n['y']}+{n['h']}" for n in shown(d)
            if n['c'] in ('label', 'button', 'image', 'slider', 'bar') and n['y'] < BAR_TOP - 2 and n['y'] + n['h'] > BAR_TOP + 2]


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
    check('context bar: a key goes to the companion', did(d, mark, 'ctx.run', id='x3') is not None, str(d.acts(mark)))
    for page in range(5):
        d.page(page)
        bad = overlapping_bar(d)
        check(f'context bar: page {page} fits above it', not bad, str(bad[:5]))
    d.page(5)
    check('context bar: not on the Numpad', not bar_key(d, 'Build'))
    d.page(0)
    check('context bar: back on the other pages', bar_key(d, 'Build') is not None)

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
    inside = all(n['x'] + n['w'] <= 790 for n in shown(d) if n['y'] >= BAR_TOP and n['y'] < 436)
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


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    with Deck.launch(log_path=os.path.join(OUT, 'sim.log')) as d:
        d.sync()
        d.settings('base')
        d.load(fixtures.day(), fixtures.ICONS, fixtures.NOW, fixtures.TZ_MIN)
        for test in (test_today, test_status_bar, test_audio, test_dev, test_windows, test_capture, test_numpad,
                     test_toast, test_call, test_cheat_sheet, test_launcher, test_context_bar, test_idle, test_offline):
            try:
                d.load(fixtures.day())  # each starts from the same day
                test(d)
            except Exception:
                check(test.__name__, False, traceback.format_exc(limit=3))
            if d.process.poll() is not None:
                check('the simulator is still running', False, f'exit code {d.process.returncode}')
                break
    failed = [r for r in results if not r[1]]
    print(f'\n{len(results) - len(failed)} passed, {len(failed)} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
