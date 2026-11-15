"""Made-up companion state for the simulator: a realistic working day, a stress set (long
texts, Hebrew, full lists) and an empty set (first run, nothing connected)."""
from __future__ import annotations

import calendar

# Sunday 8 November 2026, 10:12 local (UTC+2).
TZ_MIN = 120
NOW = calendar.timegm((2026, 11, 8, 8, 12, 0))
MIN = 60
HOUR = 3600

ICONS = {
    'ic-vscode': (0, 120, 215),
    'ic-chrome': (219, 68, 55),
    'ic-terminal': (40, 40, 40),
    'ic-obsidian': (124, 58, 237),
    'ic-explorer': (240, 190, 40),
    'ic-claude': (217, 119, 87),
    'ic-spotify': (30, 215, 96),
    'ic-prusa': (250, 100, 30),
    'ic-gmail': (234, 67, 53),
}

# Where the launcher's button sits: the right end of the tab bar.
LAUNCHER = (759, 458)

# VS Code's actions for the context bar, then the everyday ones, as the companion sends them.
VSCODE_BANK = [
    ('build', 'Build', 'play'), ('run', 'Run', 'bug'), ('terminal', 'Terminal', 'terminal'), ('commands', 'Commands', 'keyboard'),
    ('goto-file', 'Go to file', 'file'), ('goto-symbol', 'Go to symbol', 'at'), ('goto-line', 'Go to line', 'line'),
    ('definition', 'Definition', 'target'), ('search', 'Search', 'search'), ('split', 'Split', 'split-right'),
    ('sidebar', 'Sidebar', 'bars'), ('format', 'Format', 'format'), ('rename', 'Rename', 'cursor'), ('comment', 'Comment', 'comment'),
    ('quick-fix', 'Quick fix', 'bolt'), ('git', 'Source control', 'branch'), ('problems', 'Problems', 'bell'),
    ('back', 'Back', 'back'), ('forward', 'Forward', 'forward'), ('close-tab', 'Close tab', 'close'), ('reopen', 'Reopen tab', 'history'),
]
EVERYDAY_BANK = [
    ('any-copy', 'Copy', 'copy'), ('any-paste', 'Paste', 'paste'), ('any-cut', 'Cut', 'cut'), ('any-undo', 'Undo', 'undo'),
    ('any-redo', 'Redo', 'redo'), ('any-save', 'Save', 'save'), ('any-find', 'Find', 'search'), ('any-all', 'Select all', 'check'),
    ('any-new', 'New', 'plus'), ('any-open', 'Open', 'folder'), ('any-print', 'Print', 'print'), ('any-close', 'Close', 'close'),
    ('any-switch', 'Switch app', 'switch'), ('any-snap-left', 'Snap left', 'back'), ('any-snap-right', 'Snap right', 'forward'),
    ('any-max', 'Maximize', 'expand'), ('any-min', 'Minimize', 'down'), ('any-tasks', 'Task view', 'window'),
    ('any-shot', 'Screenshot', 'camera'), ('any-emoji', 'Emoji', 'smile'), ('any-desktop', 'Desktop', 'home'), ('any-lock', 'Lock PC', 'lock'),
]


NO_APPROVAL = {'id': '', 'project': '', 'tool': '', 'detail': '', 'always': False, 'count': 0}


def approval(id: str = 'p1', detail: str = 'Bash: npm run build', always: bool = True, count: int = 1) -> dict:
    return {'id': id, 'project': 'flex-screen', 'tool': detail.split(':')[0], 'detail': detail, 'always': always, 'count': count}


def sessions() -> dict:
    """A busy afternoon: one waiting on you, two at work (one with progress), one done, one on a server."""
    return {'sessions': [
        {'id': 's2', 'project': 'api-gateway', 'state': 'input', 'detail': 'Bash: kubectl rollout restart deploy/api', 'secs': 95,
         'app': 'VS Code', 'model': 'Sonnet 4.5'},
        {'id': 's1', 'project': 'flex-screen', 'state': 'working', 'detail': 'Edit: sessions.cpp', 'secs': 14 * 60,
         'app': 'Claude app', 'model': 'Opus 5.5'},
        {'id': 's3', 'project': 'icons', 'state': 'working', 'detail': 'Write: arrow.svg', 'secs': 6 * 60, 'app': 'Terminal',
         'model': 'Haiku 4.5', 'progress': {'label': 'Converting icons', 'done': 7, 'total': 12}},
        {'id': 's4', 'project': 'website', 'state': 'done', 'detail': 'Finished', 'secs': 22 * 60, 'app': 'VS Code · SSH',
         'model': 'Opus 5.5'},
        {'id': 's5', 'project': 'notes', 'state': 'idle', 'detail': '', 'secs': 50 * 60, 'app': 'Claude app', 'model': ''},
    ]}


def usage(five: int = 62, rate: float = 18.0, limit: bool = True) -> dict:
    """Two and a half hours into the window, 62% used and climbing."""
    start = NOW - int(2.5 * HOUR)
    points = [[m * 60, round(min(five, five * (m / 150) ** 1.3))] for m in range(0, 151, 5)]
    return {'five': five, 'week': 17, 'start': start, 'resets': start + 5 * HOUR,
            'eta': NOW + int((100 - five) / rate * HOUR) if limit else 0, 'rate': rate, 'points': points}


def question(id: str = 'q1', text: str = 'Convert the other 12 files too?', options=('Yes', 'No', 'Only the icons'),
             notes=None, multi: bool = False, step: int = 1, steps: int = 1, count: int = 1) -> dict:
    notes = notes or [''] * len(options)
    return {'id': id, 'kind': 'ask', 'level': '', 'session': 's1', 'project': 'flex-screen', 'text': text,
            'options': [{'label': o, 'note': n} for o, n in zip(options, notes)], 'multi': multi, 'step': step,
            'steps': steps, 'count': count}


def note(id: str = 'n1', text: str = 'All 12 files converted; 2 need a look', level: str = 'done') -> dict:
    return {'id': id, 'kind': 'note', 'level': level, 'session': 's1', 'project': 'flex-screen', 'text': text,
            'options': [], 'multi': False, 'step': 1, 'steps': 1, 'count': 1}


NO_QUESTION = {'id': ''}


def bank(items: list[tuple[str, str, str]]) -> list[dict]:
    return [{'id': i, 'name': n, 'glyph': g} for i, n, g in items]


def keymap() -> dict:
    """A small split keyboard (3x5 a side plus 3 thumbs each), three layers."""
    keys = []
    for side, x0 in ((0, 0), (1, 650)):
        for row in range(3):
            for col in range(5):
                keys.append([x0 + 50 + col * 100, 50 + row * 100, 100, 100])
        for t in range(3):
            keys.append([x0 + (250 + t * 100 if side == 0 else 50 + t * 100), 360, 100, 100])
    base = list('QWERTASDFGZXCVB') + ['Esc', 'Spc\n\nNAV', 'Tab'] + list('YUIOPHJKL;NM,./') + ['Ent', 'Bsp\n\nSYM', 'Del']
    nav = ['', 'Home', '↑', 'End', 'PgUp', '', '←', '↓', '→', 'PgDn'] + [''] * 5 + ['', '', ''] + \
          ['Ctrl\nC', 'Ctrl\nV', '', '', ''] + [''] * 10 + ['', '', '']
    sym = list('!@#$%') + list('^&*()') + list('[]{}\\') + ['', '', ''] + list('12345') + list('67890') + \
          list('-=+_|') + ['', '', '']
    dim = lambda labels: ''.join('1' if not l else '0' for l in labels)
    return {'keys': keys, 'layers': [
        {'n': 0, 'name': 'BASE', 'keys': base, 'dim': '0' * 36},
        {'n': 1, 'name': 'NAV', 'keys': nav, 'dim': dim(nav)},
        {'n': 2, 'name': 'SYMBOL', 'keys': sym, 'dim': dim(sym)},
    ]}


def zones() -> dict:
    return {
        'mons': [
            {'name': 'DELL P2722H', 'primary': True, 'w': 1920, 'h': 1032, 'type': 'custom', 'n': 3,
             'layout': 'custom-1',
             'zones': [[0, 0, 1215, 1032, 'VS Code'], [1215, 0, 705, 516, 'Terminal'], [1215, 516, 705, 516, '']]},
            {'name': 'LEN T24i-10', 'primary': False, 'w': 1080, 'h': 1920, 'type': 'rows', 'n': 2, 'layout': 'rows',
             'zones': [[0, 0, 1080, 960, 'Chrome'], [0, 960, 1080, 960, '']]},
        ],
        'active': 0,
        'app': 'VS Code',
        'layouts': [
            {'id': 'custom-1', 'name': 'real priority', 'custom': True, 'cells': [[0, 0, 26, 26], [26, 0, 14, 13], [26, 13, 14, 13]]},
            {'id': 'priority-grid', 'name': 'Priority', 'custom': False, 'cells': [[0, 0, 10, 26], [10, 0, 20, 26], [30, 0, 10, 26]]},
            {'id': 'columns', 'name': 'Columns', 'custom': False, 'cells': [[0, 0, 13, 26], [13, 0, 14, 26], [27, 0, 13, 26]]},
            {'id': 'rows', 'name': 'Rows', 'custom': False, 'cells': [[0, 0, 40, 13], [0, 13, 40, 13]]},
        ],
    }


def day() -> dict[str, dict]:
    """Every topic, as a normal working morning."""
    return {
        'media': {'present': True, 'title': 'Weightless', 'artist': 'Marconi Union', 'app': 'Spotify', 'playing': True,
                  'pos': 192, 'len': 489, 'shuffle': False, 'repeat': 'none', 'art': 'art-1'},
        'vol': {'v': 42, 'muted': False},
        'mixer': {'apps': [
            {'id': 'spotify', 'name': 'Spotify', 'v': 47, 'muted': False},
            {'id': 'chrome', 'name': 'Chrome', 'v': 33, 'muted': False},
            {'id': 'ms-teams', 'name': 'Teams', 'v': 80, 'muted': True},
            {'id': 'system', 'name': 'System', 'v': 100, 'muted': False},
            {'id': 'flow.launcher', 'name': 'Flow Launcher', 'v': 54, 'muted': False},
        ]},
        'devices': {
            'out': [{'id': 'o1', 'name': 'Speakers', 'detail': 'Realtek(R) Audio', 'sel': True},
                    {'id': 'o2', 'name': 'Headphones', 'detail': 'ThinkPad Thunderbolt 3 Dock USB Audio', 'sel': False},
                    {'id': 'o3', 'name': 'LEN T24i-10', 'detail': 'HD Audio Driver for Display Audio', 'sel': False}],
            'in': [{'id': 'i1', 'name': 'Microphone Array', 'detail': 'Intel Smart Sound', 'sel': True}],
        },
        'mic': {'available': True, 'muted': False},
        'claude': {'sessions': [{'id': 's1', 'project': 'flex-screen', 'state': 'working',
                                 'detail': 'Bash: pio run -e deck -t upload', 'secs': 40, 'app': 'Claude app', 'model': 'Opus 5.5'}]},
        'usage': usage(),
        'term': {'repo': 'flex-screen', 'cmd': 'git status', 'running': False, 'secs': 120, 'exit': 0, 'ms': 300,
                 'lines': ['On branch master', "Your branch is ahead of 'origin/master' by 2 commits."]},
        'build': {'state': 'passed', 'cmd': 'dotnet build companion', 'ms': 4800, 'secs': 600, 'history': 'PPFPPPPFPP'},
        'recent': {'list': [
            {'cmd': 'git status', 'running': False, 'exit': 0, 'ms': 300, 'secs': 120},
            {'cmd': 'pio run -d firmware -e deck -t upload', 'running': False, 'exit': 0, 'ms': 62000, 'secs': 900},
            {'cmd': 'npm run lint', 'running': False, 'exit': 1, 'ms': 5200, 'secs': 3000},
            {'cmd': 'dotnet build companion -c Release', 'running': False, 'exit': 0, 'ms': 4800, 'secs': 26 * HOUR},
        ]},
        'actions': {'list': [
            {'id': 'a1', 'kind': 'window', 'name': 'VS Code', 'title': 'page_today.cpp - flex-screen', 'icon': 'ic-vscode'},
            {'id': 'a2', 'kind': 'note', 'name': 'Obsidian', 'title': "Today's note", 'icon': ''},
            {'id': 'a3', 'kind': 'command', 'name': 'Terminal', 'title': 'git status', 'icon': ''},
            {'id': 'a4', 'kind': 'meeting', 'name': 'Meeting', 'title': 'Standup', 'icon': ''},
            {'id': 'a5', 'kind': 'device', 'name': 'Output', 'title': 'Headphones', 'icon': ''},
        ]},
        'shortcuts': {'list': [
            {'id': 'k1', 'name': 'Gmail', 'icon': 'ic-gmail', 'glyph': 'mail'},
            {'id': 'k2', 'name': 'Calendar', 'icon': '', 'glyph': 'calendar'},
            {'id': 'k3', 'name': "Today's note", 'icon': '', 'glyph': 'note'},
            {'id': 'k4', 'name': 'Spotify', 'icon': 'ic-spotify', 'glyph': ''},
            {'id': 'k5', 'name': 'VS Code', 'icon': 'ic-vscode', 'glyph': ''},
            {'id': 'k6', 'name': 'Terminal', 'icon': 'ic-terminal', 'glyph': 'terminal'},
        ]},
        'context': {'app': 'VS Code', 'key': 'code', 'icon': 'ic-vscode', 'color': 0x2F9BF0, 'keys': [
            {'id': 'build', 'name': 'Build', 'glyph': 'play'},
            {'id': 'terminal', 'name': 'Terminal', 'glyph': 'terminal'},
            {'id': 'commands', 'name': 'Commands', 'glyph': 'keyboard'},
            {'id': 'goto-file', 'name': 'Go to file', 'glyph': 'file'},
            {'id': 'search', 'name': 'Search', 'glyph': 'search'},
            {'id': 'split', 'name': 'Split', 'glyph': 'split-right'},
        ], 'bank': bank(VSCODE_BANK + EVERYDAY_BANK), 'own': len(VSCODE_BANK)},
        'approval': NO_APPROVAL,
        'git': {'repo': 'flex-screen', 'branch': 'master', 'ahead': 2, 'behind': 0, 'm': 3, 'a': 1, 'del': 0, 'u': 2,
                'last': 'Recent actions on Today, full-command terminal card', 'last_secs': HOUR, 'busy': '', 'error': ''},
        'windows': {'list': [
            {'id': '101', 'app': 'VS Code', 'title': 'page_today.cpp - flex-screen', 'icon': 'ic-vscode', 'active': True, 'min': False},
            {'id': '102', 'app': 'Chrome', 'title': 'Inbox (8) - nathendov@gmail.com - Gmail', 'icon': 'ic-chrome', 'active': False, 'min': False},
            {'id': '103', 'app': 'Terminal', 'title': 'PowerShell', 'icon': 'ic-terminal', 'active': False, 'min': False},
            {'id': '104', 'app': 'Obsidian', 'title': '2026-11-08 - DoorsOfStone', 'icon': 'ic-obsidian', 'active': False, 'min': True},
            {'id': '105', 'app': 'Explorer', 'title': 'flex-screen', 'icon': 'ic-explorer', 'active': False, 'min': False},
            {'id': '106', 'app': 'Claude', 'title': 'Claude', 'icon': 'ic-claude', 'active': False, 'min': False},
            {'id': '107', 'app': 'Spotify', 'title': 'Marconi Union - Weightless', 'icon': 'ic-spotify', 'active': False, 'min': True},
            {'id': '108', 'app': 'PrusaSlicer', 'title': 'deck stand.3mf', 'icon': 'ic-prusa', 'active': False, 'min': False},
        ]},
        'zones': zones(),
        'clips': {'list': [
            {'id': 'c1', 'text': 'ssh deck@192.168.1.40', 'kind': 'command', 'pinned': True, 'secs': 5000},
            {'id': 'c2', 'text': 'https://github.com/AsafDov/flex-screen', 'kind': 'link', 'pinned': False, 'secs': 30},
            {'id': 'c3', 'text': 'lv_obj_set_style_pad_all(c, 10, 0);', 'kind': 'code', 'pinned': False, 'secs': 200},
            {'id': 'c4', 'text': '#5AB4FF', 'kind': 'color', 'pinned': False, 'secs': 900},
            {'id': 'c5', 'text': 'Meeting moved to 13:00, room 4', 'kind': 'text', 'pinned': False, 'secs': 3000},
            {'id': 'c6', 'text': 'שלום, הדוח מצורף', 'kind': 'text', 'pinned': False, 'secs': 7200},
        ]},
        'obsidian': {'found': True, 'vault': 'DoorsOfStone', 'recent': [
            {'name': '2026-11-08', 'path': 'Daily/2026-11-08.md'},
            {'name': 'flex-screen ideas', 'path': 'Projects/flex-screen ideas.md'},
            {'name': 'Kingkiller reread notes', 'path': 'Books/Kingkiller reread notes.md'},
            {'name': 'Inbox', 'path': 'Inbox.md'},
            {'name': 'Groceries', 'path': 'Home/Groceries.md'},
        ]},
        'google': {'status': 'ok',
                   'events': [
                       {'id': 'e1', 'title': 'Standup', 'start': NOW + 18 * MIN, 'end': NOW + 33 * MIN, 'allday': False, 'join': True},
                       {'id': 'e2', 'title': 'Design review with Dana', 'start': NOW + 3 * HOUR, 'end': NOW + 4 * HOUR, 'allday': False, 'join': True},
                       {'id': 'e3', 'title': 'To-do: Reset DreamHost email and SFTP password', 'start': NOW + 8 * HOUR, 'end': NOW + 9 * HOUR, 'allday': False, 'join': False},
                   ]},
        'tasks': tasks(),
        'call': {'app': ''},
        'pc': {'locked': False, 'idle': 5},
        'kbd': {'present': True, 'name': 'W-CORNE', 'layer': 0, 'label': 'BASE', 'held_ms': -1, 'key': -1},
        'keymap': keymap(),
    }


LONG = ('A very long title that keeps going well past what any tile, row or card on the deck '
        'has room for, to see how it is cut')


def stress() -> dict[str, dict]:
    """Long texts, Hebrew, every list full, odd values."""
    d = day()
    d['media'].update(title='נפלת חזק - הופעה חיה בקיסריה 2019 (רמאסטר)', artist='Eifo HaYeled, with a very long featured artist list', app='Google Chrome')
    d['claude'] = {'sessions': [
        {'id': 's1', 'project': 'a-project-with-a-really-long-name', 'state': 'input', 'detail': 'Wants to run: ' + LONG, 'secs': 3},
        {'id': 's2', 'project': 'flex-screen', 'state': 'working', 'detail': 'Edit', 'secs': 20},
        {'id': 's3', 'project': 'notes', 'state': 'done', 'detail': '', 'secs': 400},
    ]}
    d['recent'] = {'list': [
        {'cmd': 'npm run build', 'running': True, 'exit': None, 'ms': 0, 'secs': 12},
        {'cmd': 'git log --oneline --graph --decorate --all --since="2 weeks ago" -- companion/MixerSource.cs firmware/src/page_dev.cpp firmware/src/page_today.cpp', 'running': False, 'exit': 0, 'ms': 900, 'secs': 600},
        {'cmd': 'Remove-Item -Recurse -Force .\\firmware\\.pio\\build\\deck\\src -ErrorAction SilentlyContinue; pio run -d firmware -e deck -t upload --upload-port COM11', 'running': False, 'exit': 1, 'ms': 1500, 'secs': 4 * HOUR},
        {'cmd': 'ls', 'running': False, 'exit': 0, 'ms': 10, 'secs': 9 * 24 * HOUR},
    ]}
    d['actions'] = {'list': [
        {'id': 'a1', 'kind': 'window', 'name': 'PowerToys.Peek.UI', 'title': LONG, 'icon': 'ic-missing'},
        {'id': 'a2', 'kind': 'claude', 'name': 'Claude', 'title': 'a-project-with-a-really-long-name', 'icon': ''},
        {'id': 'a3', 'kind': 'command', 'name': 'Terminal', 'title': 'git log --oneline --graph --decorate --all', 'icon': ''},
        {'id': 'a4', 'kind': 'mail', 'name': 'Gmail', 'title': 'Inbox', 'icon': ''},
        {'id': 'a5', 'kind': 'note', 'name': 'Obsidian', 'title': 'Kingkiller reread notes, part two: the Chandrian', 'icon': ''},
    ]}
    looks = ['mail', 'calendar', 'note', 'terminal', 'code', 'media', 'volume', 'lock', 'desktop', 'folder', 'keys', 'git']
    d['shortcuts'] = {'list': [{'id': f'k{i + 1}', 'name': f'Shortcut {i + 1} ({look})', 'icon': '', 'glyph': look}
                               for i, look in enumerate(looks)]}
    d['shortcuts']['list'][0].update(name='Gmail', icon='ic-gmail')
    d['shortcuts']['list'][1].update(name='A shortcut with a name far too long for its tile to hold')
    d['shortcuts']['list'][2].update(name='הפתק של היום')
    d['shortcuts']['list'][3].update(name='Missing icon', icon='ic-missing', glyph='')
    d['shortcuts']['list'].append({'id': 'k13', 'name': 'The thirteenth, one too many', 'icon': '', 'glyph': ''})
    d['context'] = {'app': 'Microsoft Teams (work or school)', 'icon': 'ic-missing', 'color': 0x6264A7, 'keys': [
        {'id': f'x{i + 1}', 'name': name, 'glyph': glyph} for i, (name, glyph) in enumerate([
            ('Mute', 'mic-off'), ('Camera', 'video'), ('Raise hand', 'hand'), ('Share the whole screen', 'screen'),
            ('Leave', 'leave'), ('A key with no icon', ''), ('Unknown icon', 'no-such-icon'), ('Eight', 'split-down'),
            ('Ninth, one too many', 'plus')])]}
    d['git'].update(branch='feature/a-very-long-branch-name-for-the-deck', ahead=12, behind=3, m=14, a=7, **{'del': 2}, u=31,
                    last=LONG, busy='pull')
    for w in d['windows']['list']:
        w['title'] = LONG
    d['windows']['list'][0]['app'] = 'Microsoft Visual Studio Code'
    d['windows']['list'][1]['icon'] = 'ic-missing'
    d['clips']['list'][1]['text'] = 'https://example.com/' + 'very/long/path/' * 12
    d['clips']['list'][4]['text'] = LONG
    d['obsidian']['recent'][2]['name'] = 'Kingkiller reread notes, part two: the Chandrian and the Amyr'
    d['google']['events'][0].update(title=LONG, start=NOW - 5 * MIN, end=NOW + 25 * MIN)
    d['tasks']['lists'] = d['tasks']['lists'][:3] + [{'src': 'google', 'id': f'L{i}', 'name': f'A list with a rather long name {i}', 'show': True} for i in range(4, 9)]
    d['tasks']['tasks'] = [{'src': 'google', 'list': f'L{1 + i % 8}', 'id': f't{i}', 'title': LONG if i == 0 else f'Task number {i}',
                            'sub': 'Someone With A Long Name <someone@example.com>' if i % 3 == 0 else '',
                            'due': '' if i > 30 else f'2026-11-{1 + i // 2:02d}', 'done': i == 3, 'child': i % 5 == 1}
                           for i in range(40)]
    d['tasks']['open'] = 57
    d['tasks']['sched'] = 48
    d['mixer']['apps'][4]['name'] = 'Microsoft Teams (work or school)'
    d['devices']['out'].append({'id': 'o4', 'name': 'Realtek Digital Output (Optical)', 'detail': 'Realtek(R) Audio', 'sel': False})
    d['vol'] = {'v': 100, 'muted': True}
    d['mic'] = {'available': True, 'muted': True}
    d['build'] = {'state': 'failed', 'cmd': 'npm run build', 'ms': 61000, 'secs': 5, 'history': 'FFFFFFFFFF'}
    d['kbd'] = {'present': True, 'name': 'W-CORNE', 'layer': 2, 'label': 'SYMBOL', 'held_ms': -1, 'key': -1}
    return d


def empty() -> dict[str, dict]:
    """A first run: nothing playing, no Google, no repo, no windows, nothing copied."""
    return {
        'media': {'present': False},
        'vol': {'v': -1, 'muted': False},
        'mixer': {'apps': []},
        'devices': {'out': [], 'in': []},
        'mic': {'available': False, 'muted': False},
        'claude': {'sessions': []},
        'term': {'repo': '', 'cmd': ''},
        'build': {'state': 'none', 'history': ''},
        'recent': {'list': []},
        'actions': {'list': []},
        'shortcuts': {'list': []},
        'context': {'app': '', 'icon': '', 'color': 0, 'keys': []},
        'git': {'repo': '', 'error': 'Not a git repository'},
        'windows': {'list': []},
        'zones': {'mons': [], 'layouts': []},
        'clips': {'list': []},
        'obsidian': {'found': False, 'recent': []},
        'google': {'status': 'setup', 'events': []},
        'tasks': {'rev': 0, 'status': 'setup', 'lists': [], 'tasks': [], 'open': 0, 'sched': 0},
        'call': {'app': ''},
        'pc': {'locked': False, 'idle': 5},
        'kbd': {'present': False},
        'keymap': {'keys': [], 'layers': []},
    }


def tasks() -> dict:
    """Google Tasks on the 8th: three lists on the deck and one kept off it (Someday, whose
    dated task still comes, for Scheduled), one task overdue, three today, a subtask."""
    lists = [{'src': 'google', 'id': 'L1', 'name': 'My Tasks', 'show': True}, {'src': 'google', 'id': 'L2', 'name': 'Home', 'show': True},
             {'src': 'google', 'id': 'L3', 'name': 'flex-screen', 'show': True}, {'src': 'google', 'id': 'L4', 'name': 'Someday', 'show': False}]

    def t(i: int, lst: str, title: str, due: str = '', sub: str = '', child: bool = False) -> dict:
        return {'src': 'google', 'list': lst, 'id': f't{i}', 'title': title, 'sub': sub, 'due': due, 'done': False, 'child': child}
    items = [
        t(1, 'L2', 'Reply to the landlord about the boiler', '2026-11-06', 'From: Yossi Cohen'),
        t(2, 'L1', 'Send the signed lease', '2026-11-08', 'From: Dana Levi'),
        t(3, 'L2', 'Pay the Arnona bill', '2026-11-08'),
        t(10, 'L4', 'Call the plumber', '2026-11-08'),
        t(4, 'L3', 'Tasks page on the deck', '2026-11-09'),
        t(5, 'L3', 'Sort the tasks by due day', '2026-11-09', child=True),
        t(6, 'L1', 'Book the car service', '2026-11-12'),
        t(7, 'L1', 'Renew the passport', '2026-12-01', 'Appointment at the post office'),
        t(8, 'L2', 'Fix the balcony light'),
        t(9, 'L3', 'הוספת מקורות נוספים'),
    ]
    return {'rev': 1, 'status': 'ok', 'lists': lists, 'tasks': items, 'open': len(items), 'sched': sum(1 for i in items if i['due'])}


def call(app: str = 'teams') -> dict:
    can = {'teams': ['mic', 'camera', 'hand', 'leave', 'react'], 'zoom': ['mic', 'camera', 'hand', 'whiteboard', 'leave'],
           'discord': ['mic', 'deafen', 'leave'], 'browser': ['mic']}[app]
    return {'app': app, 'name': {'teams': 'Teams', 'zoom': 'Zoom', 'discord': 'Discord', 'browser': 'Chrome'}[app],
            'secs': 754, 'mic_muted': False, 'camera': 1 if 'camera' in can else -1, 'hand': 0 if 'hand' in can else -1,
            'deafened': False, 'can': can, 'note': '', 'volume': 70}


# Zoom's palettes, as the companion sends them (ZoomControl.cs).
ANNOTATE_COLORS = ['FFFFFF', 'F42E2E', 'FFD43C', '3BC255', '1A73E8', 'B57CF2', 'FF5BB4', 'FF8A1F', 'A6D93A', '62D3F5',
                   '5A5A5A', '9E1B1B', '8A5A2B', '1E7A35', '1D3E9E']
BOARD_COLORS = ['FFFFFF', 'EDEDED', 'DEDEDE', '939393', '585858', '000000', '0072C6', '00B342', 'FFFF00', 'FF9000', 'FF0000',
                '792BA6', '00B3F6', '7ED235', 'EBFF54', 'FFBE00', 'FF32B4', 'E100FF', 'B2EAFF', 'CCFF9F', 'F7FFBB', 'FFE599',
                'FFB6E4', 'F6B3FF']
# The whiteboard marker's and laser's own palettes (Zoom Workplace 7.1.9).
MARKER_COLORS = ['FBFFA2', 'D8FFBA', 'C4FFFA', 'E6D3FF', 'FFD9F4', 'FFE8C8']
LASER_COLORS = ['D70F1E', '005AC8', 'D08D00', '339951', '5300F9', '6E7680']


def zoom_draw(mode: str = 'annotate', tool: str = 'pen') -> dict:
    """A Zoom meeting with the annotation tools (on a shared screen) or a whiteboard open."""
    c = call('zoom')
    c['can'] = ['mic', 'camera', 'hand', 'annotate', 'leave'] if mode == 'annotate' else ['mic', 'camera', 'hand', 'whiteboard', 'leave']
    kinds, kind, weight = [], '', {}
    if mode == 'annotate':
        tools = ['spotlight', 'pen', 'highlighter', 'vanish', 'eraser', 'select']
        inks = ('pen', 'highlighter', 'vanish')
        colors = ANNOTATE_COLORS if tool in inks else []
        if tool == 'spotlight':
            kinds, kind = [{'id': 'spotlight', 'name': 'Spotlight'}, {'id': 'arrow', 'name': 'Arrow'}], 'spotlight'
    else:
        tools = ['laser', 'pen', 'highlighter', 'eraser', 'grab', 'select']
        inks = ()
        colors = {'pen': BOARD_COLORS, 'highlighter': MARKER_COLORS, 'laser': LASER_COLORS}.get(tool, [])
        if tool in ('pen', 'highlighter', 'eraser'):  # the pixel eraser has a weight, the whole-stroke one not
            weight = {'weight': 6, 'weight_min': 1, 'weight_max': 20}
        if tool == 'eraser':
            kinds, kind = [{'id': 'stroke', 'name': 'Whole stroke'}, {'id': 'pixel', 'name': 'Pixel'}], 'pixel'
    c['draw'] = {'mode': mode, 'tools': tools, 'tool': tool, 'colors': colors, 'color': 1 if colors else -1,
                 'width': 1, 'kinds': kinds, 'kind': kind, 'can_color': bool(colors), 'can_width': tool in inks,
                 'undo': True, 'redo': False, 'save': mode == 'annotate', **weight}
    return c
