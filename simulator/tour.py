"""Screenshots of every page, for looking over: out/tour/<set>-<palette>-<page>.png.

    python tour.py                  # the day set in all four themes, plus stress, empty, offline
    python tour.py day base         # one set, one theme
"""
import os
import sys

import fixtures
from deck import Deck, HERE

PAGES = ['today', 'audio', 'dev', 'windows', 'capture', 'numpad']
PALETTES = ['base', 'vibrant', 'ember', 'violet']
OUT = os.path.join(HERE, 'out', 'tour')


def shoot_pages(d: Deck, prefix: str) -> None:
    for i, name in enumerate(PAGES):
        d.page(i)
        d.shot(os.path.join(OUT, f'{prefix}-{name}.png'))


def set_palette(d: Deck, palette: str) -> None:
    """The deck restarts into a palette it isn't using; reconnect and say hello again."""
    mark = d.mark()
    d.settings(palette)
    try:
        d.wait_for(lambda m: m.get('t') == 'log' and 'restarting' in m.get('msg', ''), since=mark, timeout=2)
        d.reconnect()
    except TimeoutError:
        pass  # already in it
    d.sync()


def tour(sets: list[str], palettes: list[str], offline: bool = True) -> None:
    os.makedirs(OUT, exist_ok=True)
    with Deck.launch(log_path=os.path.join(OUT, 'sim.log')) as d:
        if offline:
            d.settle()
            shoot_pages(d, 'waiting')  # connected, nothing sent yet
            d.offline()
            shoot_pages(d, 'offline')  # the companion gone quiet
            d.online()
        d.sync()
        for palette in palettes:
            set_palette(d, palette)
            for name in sets:
                d.load(getattr(fixtures, name)(), fixtures.ICONS, fixtures.NOW, fixtures.TZ_MIN)
                shoot_pages(d, f'{name}-{palette}')
        set_palette(d, 'base')


if __name__ == '__main__':
    if len(sys.argv) > 1:
        tour([sys.argv[1]], [sys.argv[2] if len(sys.argv) > 2 else 'base'], offline=False)
    else:
        tour(['day'], PALETTES)
        tour(['stress', 'empty'], ['base'], offline=False)
        # The day set taken offline: what's shown when the companion goes away mid-day.
        with Deck.launch(log_path=os.path.join(OUT, 'sim-gone.log')) as d:
            d.sync()
            d.load(fixtures.day(), fixtures.ICONS, fixtures.NOW, fixtures.TZ_MIN)
            d.offline()
            shoot_pages(d, 'gone')
