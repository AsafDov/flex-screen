"""Runs some of test_deck.py's tests by name: python run_some.py test_sessions test_dev"""
import os
import sys
import traceback

import fixtures
import test_deck
from deck import Deck


def main() -> int:
    names = sys.argv[1:]
    os.makedirs(test_deck.OUT, exist_ok=True)
    with Deck.launch(log_path=os.path.join(test_deck.OUT, 'sim.log')) as d:
        d.sync()
        d.settings('base')
        d.load(fixtures.day(), fixtures.ICONS, fixtures.NOW, fixtures.TZ_MIN)
        for name in names:
            try:
                d.load(fixtures.day())
                getattr(test_deck, name)(d)
            except Exception:
                test_deck.check(name, False, traceback.format_exc(limit=3))
    failed = [r for r in test_deck.results if not r[1]]
    print(f'\n{len(test_deck.results) - len(failed)} passed, {len(failed)} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
