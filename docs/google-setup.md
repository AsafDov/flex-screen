# Connect Google Calendar and Tasks

What the deck shows once Google is connected:

- **Status bar:** the next meeting with a countdown, amber from five minutes out.
- **Today:** *Up next* with a **Join** button for Meet, Zoom and Teams links; *Recent* can hold a
  meeting you joined; a pill counts your open tasks and those due today.
- **Capture:** *Tasks*: your scheduled tasks and the lists you chose, by when they're due. Tap
  one to tick it off; **+** adds one (see [Tasks on the deck](#tasks-on-the-deck)).

The deck reads your calendar (read-only) and your Google Tasks (it can tick tasks off and add
new ones).

It takes about ten minutes, once. Part 1 is done once per Google account; part 2 on each PC.

## Part 1: your own Google sign-in app (once)

flex-screen signs in to Google with an OAuth client you create in your own Google Cloud
project, so no one else's app ever holds access to your account.

1. **Create a project.** Open [console.cloud.google.com](https://console.cloud.google.com)
   with the Google account whose calendar you want, then the project picker at the top →
   **New project**. Name it, for example, `flex-screen`, and select it.
2. **Turn on the two APIs.** Menu → **APIs & Services** → **Library**. Search for and
   **Enable** both:
   - **Google Calendar API**
   - **Google Tasks API**

   Skipping either is the most common problem: the sign-in works, then Google refuses every
   read (see [Troubleshooting](#troubleshooting)).
3. **Set up the consent screen.** Menu → **Google Auth Platform** (in older consoles:
   **APIs & Services** → **OAuth consent screen**) → **Get started**:
   - **App name:** `flex-screen`. **User support email** and **Developer contact:** your address.
   - **Audience:** **External**.
   - The *App domain* links (home page, privacy policy, terms) and *Authorized domains* can
     stay empty. They only matter if you submit the app for Google's verification, which
     personal use doesn't need.
4. **Publish it.** **Audience** → **Publish app** → confirm, so the status reads
   **In production**. Left in *Testing*, Google ends the sign-in every 7 days. Publishing
   doesn't make anything public: only someone holding your client file (step 6) could even
   start a sign-in, and they could only ever reach their own account.
5. **Scopes (optional).** **Data Access** → **Add or remove scopes**, tick
   `.../auth/calendar.readonly` and `.../auth/tasks` → **Update** → **Save**. flex-screen asks
   for exactly these two either way; listing them here just documents it.
6. **Create the client.** **Clients** (or **APIs & Services** → **Credentials**) →
   **Create client** → **Application type: Desktop app**, name it `flex-screen` →
   **Create** → **Download JSON**. That file is your client: keep it private (don't commit it
   or share it).

## Part 2: connect on each PC

1. **Save the client file** as
   `%LOCALAPPDATA%\flex-screen\google-client.json`
   (paste `%LOCALAPPDATA%\flex-screen` into File Explorer's address bar, copy the downloaded
   file there, and rename it).
2. **Sign in.** Right-click the flex-screen tray icon → **Google accounts** → **Google** →
   **Sign in…**. Your browser opens Google's sign-in:
   - Pick the account from part 1.
   - *Google hasn't verified this app*: that's your own app, unverified because you didn't
     submit it. Click **Advanced** → **Go to flex-screen (unsafe)**.
   - **Tick both boxes** (calendar and tasks) → **Continue**.
   - The tab says *flex-screen is connected to Google*, and the deck shows
     *Connected to Google*. You have five minutes to finish the sign-in; after that, choose
     **Sign in…** again.
3. **Check the deck.** Within a few seconds Today's *Up next* shows your next event (or
   *Nothing else today*). After that the companion reads Google every 5 minutes.

The sign-in is stored on the PC encrypted for your Windows account (`google-token.bin` next to
the client file; another account's is `<its id>-token.bin`), so it's useless anywhere else.
The accounts themselves are part of your [profile](../README.md#profiles): a PC on the same
profile lists the same accounts, and signs in to each one once.

## Tasks on the deck

The Tasks card on the Capture page shows each signed-in account's Google Tasks (up to 40 on
the deck, soonest due first; the counts include the rest). A task's notes give the grey line
under its title (their first line; the `todo` skill's `[todo src=…]` tag is left out).

- **Scheduled** (where the card opens): every open task with a due date, from every list.
- **One list:** a chip for each list you chose. In the tray icon's menu, **Task lists on the
  deck** has every list, ticked ones get a chip (all of them at first). An unticked list's
  undated tasks don't come to the deck at all; its dated ones still show under Scheduled.
- There's no *Starred*: Google's Tasks API doesn't tell apps which tasks are starred.

- **Open one:** tap it for its whole title, list, due day, status (to do, done, or how many
  days overdue) and notes, with **Mark done**.
- **Tick one off:** tap the box at its start (or **Mark done** in it). Google Tasks marks it
  completed; the deck keeps it, struck through, until midnight, and ticking it again marks
  it not done.
- **Add one:** **+**, type the title on the deck's keyboard, pick the list (the one
  on show to begin with; from Scheduled, the first list and due today) and when it's due, then **Add** (or the keyboard's ✓). A toast
  says which list it went to.
- The companion reads Google Tasks every 2 minutes, at once after a change from the deck,
  and when you tap ↻.

## More than one Google account

Add the other account (a work one beside your own, say): tray icon → **Google accounts** →
**Add a Google account…**, give it a name (*Work*), and pick that account in Google's
account picker. Then:

- **Calendar:** its events join the deck's calendar: every calendar *ticked* in Google
  Calendar, for each account. A meeting on several of them (both accounts invited) shows once.
- **Tasks:** its lists join the Tasks card as *Google Tasks (Work)*, with their own chips in
  **Task lists on the deck**.
- **Either one off:** **Google accounts** → the account → **Its calendar on the deck** or
  **Its tasks on the deck** (a work calendar without the work tasks, say).
- **The OAuth client:** the one from part 1 serves every account, but while its app is in
  *Testing*, add each account under **Audience** → **Test users** first. A Google Workspace
  domain that only allows its own apps needs a client made in that domain's Google Cloud
  project: save its JSON in `%LOCALAPPDATA%\flex-screen\` under another name and give that
  name when adding the account (it's the account's `"client"` in the profile).
- **Per profile:** the accounts are in your [profile](../README.md#profiles), so a work
  profile can have the work account and a home profile not.

Without adding it, another account's calendar can still show by sharing it: in that
account, Google Calendar → Settings → the calendar → **Share with specific people** → add
your main address with **See all event details**; accept the invite in the main account.
Google Tasks can't be shared that way.

## On another PC

Copy `google-client.json` (and any other account's client file) to
`%LOCALAPPDATA%\flex-screen\` on that PC and sign in to each account there (part 2, step 2).
One client serves any number of PCs; each signs in on its own.

## Disconnecting

- To stop on one PC: tray icon → **Google accounts** → the account → **Sign out on this PC**
  (the deck then asks to connect again). **Remove from the … profile…** also takes the
  account out of the profile. `uninstall.cmd` removes the client files and the sign-ins.
- To revoke access everywhere: [myaccount.google.com/permissions](https://myaccount.google.com/permissions)
  → flex-screen → **Remove access**.

## Troubleshooting

The companion's log says what Google answered: tray icon → **Open log**, lines starting with
`google:`.

| What you see | Why, and what to do |
|---|---|
| Deck: *Connect Google Calendar*, and the tray item shows a note instead of a browser | The client file isn't at `%LOCALAPPDATA%\flex-screen\google-client.json` (check the name: Windows may have kept `.json.json` or the long downloaded name). |
| Log: `Google Calendar API has not been used in project … or it is disabled` (or the same for Tasks) | Part 1, step 2: enable that API. It can take a few minutes to take effect; the companion tries again every 5 minutes. |
| Toast: *Google didn't grant Calendar (or Tasks) access* | A box was left unticked on the consent page. Sign in again (tray icon → **Google accounts** → the account) and tick both. |
| It worked, then stopped after about a week | The app was still in *Testing* (part 1, step 4). Publish it, then connect again from the tray. |
| Log: `token request failed: invalid_grant` | The sign-in was revoked or expired; the companion drops it. Connect again from the tray. |
| *Error 403: access_denied* in the browser | The app is in *Testing* and this account isn't a test user: publish it (step 4), or add the account under **Audience** → **Test users**. |
| Deck: *Calendar unavailable* | Google couldn't be reached right now; the last data stays shown. |
| Tasks: *Google Tasks can't be reached right now* | The Tasks API refused or Google couldn't be reached; the log's `tasks: google:` line says which. Enable the Google Tasks API (part 1, step 2) if that's what it says. |
| Toast: *Couldn't add the task to Google Tasks* | As above; the log has Google's reason. |
| An event from another calendar is missing | Tick that calendar in Google Calendar's list (left side, *My calendars* / *Other calendars*). |
