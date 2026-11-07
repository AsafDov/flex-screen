// flex-screen deck: connects this VS Code window to the companion app on this PC.
//
// Reports the open workspace, whether the window has focus, every command run in an
// integrated terminal (text, last lines of output, exit code, duration), the last few
// commands (kept per workspace, so the deck has them after a restart) and task results.
// Runs what the deck asks for: one of those commands again, Ctrl+C, the terminal, Source
// Control. Protocol: protocol/v0.md ("VS Code extension").
import * as vscode from 'vscode';

const RECONNECT_MS = 3000;
const OUTPUT_LINES = 8;
const OUTPUT_EVERY_MS = 300;
const MAX_LINE = 120;
const HISTORY = 8;
const HISTORY_KEY = 'flexScreen.history';

/** A command run lately in this workspace; only the newest keeps its output. */
type Past = { cmd: string; exit: number | null; ms: number; at: number; running?: boolean; lines?: string[] };
type Run = { id: number; started: number; terminal: vscode.Terminal; cmd: string; lines: string[]; ended?: number };

let socket: WebSocket | undefined;
let stopped = false;
let nextId = 1;
const runs = new Map<vscode.TerminalShellExecution, Run>();
let last: { terminal: vscode.Terminal; commandLine: string } | undefined;
const ranIn = new Map<string, vscode.Terminal>();  // where each command ran last, this session
let deckTerminal: vscode.Terminal | undefined;
let memory: vscode.Memento | undefined;
let version = '';

export function activate(context: vscode.ExtensionContext): void {
  memory = context.workspaceState;
  version = String(context.extension.packageJSON.version ?? '');
  // A command still marked running belonged to a window that closed mid-run.
  const past = history();
  if (past.some((p) => p.running)) void memory.update(HISTORY_KEY, past.map((p) => ({ ...p, running: false })));
  connect();
  context.subscriptions.push(
    vscode.window.onDidChangeWindowState((s) => send({ t: 'focus', focused: s.focused })),
    vscode.workspace.onDidChangeWorkspaceFolders(() => send(hello())),
    vscode.window.onDidStartTerminalShellExecution(onCommandStart),
    vscode.window.onDidEndTerminalShellExecution(onCommandEnd),
    vscode.tasks.onDidStartTaskProcess((e) => send({ t: 'task.start', ...describeTask(e.execution.task) })),
    vscode.tasks.onDidEndTaskProcess((e) =>
      send({ t: 'task.end', ...describeTask(e.execution.task), exit: e.exitCode ?? null })),
    { dispose: () => { stopped = true; socket?.close(); } },
  );
}

export function deactivate(): void {
  stopped = true;
  socket?.close();
}

function hello() {
  return {
    t: 'hello',
    version,
    window: vscode.env.sessionId,
    name: vscode.workspace.name ?? '',
    folders: (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath),
    focused: vscode.window.state.focused,
  };
}

function connect(): void {
  if (stopped) return;
  const port = vscode.workspace.getConfiguration('flexScreen').get<number>('companionPort', 48151);
  const ws = new WebSocket(`ws://localhost:${port}/vscode`);
  ws.onopen = () => {
    socket = ws;
    send(hello());
    sendHistory(history());
  };
  ws.onmessage = (e) => {
    try {
      handle(JSON.parse(String(e.data)));
    } catch {
      // A malformed command is ignored; the companion logs its own side.
    }
  };
  ws.onclose = () => {
    if (socket === ws) socket = undefined;
    setTimeout(connect, RECONNECT_MS);  // the companion may simply not be running yet
  };
  ws.onerror = () => { /* onclose follows */ };
}

function send(message: object): void {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

function describeTask(task: vscode.Task) {
  return { name: task.name, group: task.group?.id ?? '', source: task.source };
}

function history(): Past[] {
  return memory?.get<Past[]>(HISTORY_KEY) ?? [];
}

function sendHistory(list: Past[]): void {
  send({ t: 'term.history', items: list });
}

/** Puts a command at the top of the workspace's list (once: an earlier run of it drops out). */
function remember(entry: Past): void {
  const list = [entry, ...history().filter((p) => p.cmd !== entry.cmd)].slice(0, HISTORY)
    .map((p, i) => (i === 0 ? p : { ...p, lines: undefined }));
  void memory?.update(HISTORY_KEY, list);
  sendHistory(list);
}

function onCommandStart(e: vscode.TerminalShellExecutionStartEvent): void {
  const id = nextId++;
  const commandLine = e.execution.commandLine.value;
  const run: Run = { id, started: Date.now(), terminal: e.terminal, cmd: commandLine, lines: [] };
  runs.set(e.execution, run);
  last = { terminal: e.terminal, commandLine };
  if (commandLine.trim()) ranIn.set(commandLine, e.terminal);
  send({ t: 'term.start', id, cmd: commandLine, cwd: e.execution.cwd?.fsPath ?? '', terminal: e.terminal.name });
  if (commandLine.trim()) remember({ cmd: commandLine, exit: null, ms: 0, at: run.started, running: true });
  void streamOutput(e.execution, run);
}

function onCommandEnd(e: vscode.TerminalShellExecutionEndEvent): void {
  const run = runs.get(e.execution);
  if (!run) return;
  runs.delete(e.execution);
  run.ended = Date.now();
  const ms = run.ended - run.started;
  send({ t: 'term.end', id: run.id, exit: e.exitCode ?? null, ms });
  if (run.cmd.trim()) remember({ cmd: run.cmd, exit: e.exitCode ?? null, ms, at: run.ended, lines: run.lines });
}

/** Output that arrived after the command ended goes into the saved copy too. */
function keepLines(run: Run): void {
  const list = history();
  if (!run.ended || list[0]?.cmd !== run.cmd || list[0].at !== run.ended) return;
  list[0] = { ...list[0], lines: run.lines };
  void memory?.update(HISTORY_KEY, list);
}

// The last few lines of output, at most every OUTPUT_EVERY_MS, so the deck can show a
// build's tail without receiving everything it prints.
async function streamOutput(execution: vscode.TerminalShellExecution, run: Run): Promise<void> {
  let lines: string[] = [];
  let partial = '';
  let sentAt = 0;
  let dirty = false;
  const flush = () => {
    run.lines = partial ? [...lines, partial].slice(-OUTPUT_LINES) : lines;
    send({ t: 'term.data', id: run.id, lines: run.lines });
    keepLines(run);
    sentAt = Date.now();
    dirty = false;
  };
  try {
    for await (const chunk of execution.read()) {
      const text = partial + stripAnsi(chunk);
      const parts = text.split('\n');
      partial = parts.pop() ?? '';
      for (const raw of parts) {
        const line = cleanLine(raw);
        if (line) lines.push(line);
      }
      lines = lines.slice(-OUTPUT_LINES);
      partial = cleanLine(partial);
      dirty = true;
      if (Date.now() - sentAt >= OUTPUT_EVERY_MS) flush();
    }
  } catch {
    // The terminal closed mid-command.
  }
  if (dirty) flush();
}

function stripAnsi(text: string): string {
  // CSI and OSC sequences (colors, cursor moves, shell-integration markers).
  return text.replace(/\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[()][A-Z0-9]/g, '');
}

function cleanLine(raw: string): string {
  // A carriage return redraws the line (progress bars): keep what's visible last.
  const visible = raw.split('\r').filter((s) => s.length > 0).pop() ?? '';
  const trimmed = visible.replace(/\s+$/, '');
  return trimmed.length > MAX_LINE ? trimmed.slice(0, MAX_LINE - 1) + '…' : trimmed;
}

function handle(message: { t?: string; cmd?: string }): void {
  switch (message.t) {
    case 'replay': {
      // In the terminal it ran in, if that's still open and idle; else the deck's own.
      const cmd = message.cmd || last?.commandLine;
      if (!cmd) break;
      const terminal = ranIn.get(cmd);
      const busy = [...runs.values()].some((r) => r.terminal === terminal);
      void run(cmd, terminal && !busy ? terminal : undefined);
      break;
    }
    case 'interrupt':
      (last?.terminal ?? vscode.window.activeTerminal)?.sendText('\x03', false);
      break;
    case 'terminal':
      (last?.terminal ?? vscode.window.activeTerminal)?.show();
      break;
    case 'scm':
      void vscode.commands.executeCommand('workbench.view.scm');
      break;
  }
}

/**
 * The terminal for commands from the deck: its own, opened at the workspace root. The active
 * terminal may be in another folder, or predate PATH changes other extensions made (PlatformIO
 * adds `pio` only to terminals opened after it starts). A second one opens while it's busy.
 */
function terminalForDeck(): vscode.Terminal {
  const alive = deckTerminal !== undefined && deckTerminal.exitStatus === undefined;
  const busy = alive && [...runs.values()].some((r) => r.terminal === deckTerminal);
  if (!alive || busy) {
    deckTerminal = vscode.window.createTerminal({ name: 'flex-screen', cwd: vscode.workspace.workspaceFolders?.[0]?.uri });
  }
  return deckTerminal!;
}

/** Runs a command in the given terminal (or the deck's), through shell integration when it's ready. */
async function run(commandLine: string, terminal: vscode.Terminal | undefined): Promise<void> {
  const target = terminal && terminal.exitStatus === undefined ? terminal : terminalForDeck();
  target.show(true);
  const integration = target.shellIntegration ?? (await waitForShellIntegration(target, 3000));
  if (integration) {
    integration.executeCommand(commandLine);
  } else {
    target.sendText(commandLine);  // no shell integration: runs, but VS Code can't report its result
  }
}

function waitForShellIntegration(terminal: vscode.Terminal, timeoutMs: number): Promise<vscode.TerminalShellIntegration | undefined> {
  return new Promise((resolve) => {
    const timer = setTimeout(() => { listener.dispose(); resolve(undefined); }, timeoutMs);
    const listener = vscode.window.onDidChangeTerminalShellIntegration((e) => {
      if (e.terminal !== terminal) return;
      clearTimeout(timer);
      listener.dispose();
      resolve(e.shellIntegration);
    });
  });
}
