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
                                 'detail': 'Bash: pio run -e deck -t upload', 'secs': 40}]},
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
        'google': {'status': 'ok', 'open': 3,
                   'events': [
                       {'id': 'e1', 'title': 'Standup', 'start': NOW + 18 * MIN, 'end': NOW + 33 * MIN, 'allday': False, 'join': True},
                       {'id': 'e2', 'title': 'Design review with Dana', 'start': NOW + 3 * HOUR, 'end': NOW + 4 * HOUR, 'allday': False, 'join': True},
                       {'id': 'e3', 'title': 'To-do: Reset DreamHost email and SFTP password', 'start': NOW + 8 * HOUR, 'end': NOW + 9 * HOUR, 'allday': False, 'join': False},
                   ],
                   'tasks': [
                       {'list': 'L1', 'id': 't1', 'title': 'Reply to the landlord about the boiler', 'from': 'Yossi Cohen', 'due': '2026-11-08', 'done': False},
                       {'list': 'L1', 'id': 't2', 'title': 'Send the signed lease', 'from': 'Dana Levi', 'due': '2026-11-09', 'done': False},
                       {'list': 'L1', 'id': 't3', 'title': 'Pay the Arnona bill', 'from': 'Hadera Municipality', 'due': '', 'done': False},
                   ]},
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
    d['google']['tasks'] = [{'list': 'L1', 'id': f't{i}', 'title': LONG if i == 0 else f'Task number {i} from mail',
                             'from': 'Someone With A Long Name <someone@example.com>', 'due': '2026-11-0' + str(5 + i), 'done': i == 3}
                            for i in range(6)]
    d['google']['open'] = 5
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
        'google': {'status': 'setup', 'events': [], 'tasks': [], 'open': 0},
        'call': {'app': ''},
        'pc': {'locked': False, 'idle': 5},
        'kbd': {'present': False},
        'keymap': {'keys': [], 'layers': []},
    }


def call(app: str = 'teams') -> dict:
    can = {'teams': ['mic', 'camera', 'hand', 'leave', 'react'], 'zoom': ['mic', 'camera', 'hand', 'leave'],
           'discord': ['mic', 'deafen', 'leave'], 'browser': ['mic']}[app]
    return {'app': app, 'name': {'teams': 'Teams', 'zoom': 'Zoom', 'discord': 'Discord', 'browser': 'Chrome'}[app],
            'secs': 754, 'mic_muted': False, 'camera': 1 if 'camera' in can else -1, 'hand': 0 if 'hand' in can else -1,
            'deafened': False, 'can': can, 'note': '', 'volume': 70}
