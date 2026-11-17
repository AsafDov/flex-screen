// flex-screen's browser extension (Chrome and Edge): tells the companion on this PC which page
// the tab in front shows, so the deck's context bar has that site's keys (Gmail, Jira,
// GitHub, and the profile's own sites) whatever the page's title says. It sends the address
// and title of the active tab when another tab or window comes to the front or the tab goes
// somewhere else; nothing else, and nowhere but localhost. Incognito tabs and pages that
// aren't web pages (chrome://, files) say nothing.

const COMPANION = 'ws://localhost:48151/browser';
const BROWSER = /\bEdg\//.test(navigator.userAgent) ? 'msedge' : 'chrome';

let socket;
let retryMs = 1000;
let lastSent = '';

function connect() {
  try {
    socket = new WebSocket(COMPANION);
  } catch {
    later();
    return;
  }
  socket.onopen = () => {
    retryMs = 1000;
    lastSent = '';
    reportFront();
  };
  socket.onclose = () => {
    socket = undefined;
    later();
  };
  socket.onerror = () => {};  // onclose comes next
}

// The companion isn't running (or is restarting for an update): try again, less and less often.
function later() {
  setTimeout(connect, retryMs);
  retryMs = Math.min(retryMs * 2, 60000);
}

function report(tab) {
  if (!tab || !tab.active || tab.incognito || !/^https?:\/\//.test(tab.url || '')) return;
  const message = JSON.stringify({ t: 'tab', browser: BROWSER, url: tab.url, title: tab.title || '' });
  if (message === lastSent || socket?.readyState !== WebSocket.OPEN) return;
  socket.send(message);
  lastSent = message;
}

async function reportFront() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    report(tab);
  } catch {
    // No window in front (the browser is closing).
  }
}

chrome.tabs.onActivated.addListener(() => reportFront());
chrome.tabs.onUpdated.addListener((_id, change, tab) => {
  if (change.url || change.title) report(tab);
});
chrome.windows.onFocusChanged.addListener(() => reportFront());

// A service worker stops after 30 s with nothing to do; a message every 20 s keeps it, and
// the connection, alive (Chrome 116 on).
setInterval(() => {
  if (socket?.readyState === WebSocket.OPEN) socket.send('{"t":"ping"}');
}, 20000);

connect();
