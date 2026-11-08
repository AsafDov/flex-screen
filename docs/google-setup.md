# Connect Google Calendar, Tasks and mail

What the deck shows once Google is connected:

- **Status bar:** the next meeting with a countdown, amber from five minutes out.
- **Today:** *Up next* with a **Join** button for Meet, Zoom and Teams links; *Recent* can hold a
  meeting you joined; the bar at the bottom counts mail action items.
- **Capture:** *From mail*, the action items made from your email, with sender and due day.
  Tap the box to tick one off in Google Tasks; **Open Gmail** opens Gmail in the browser.

The deck reads your calendar (read-only) and your Google Tasks (it can tick tasks off). It
doesn't read Gmail itself: the mail items are Google Tasks that the `todo` skill makes from
your inbox (see [Mail on the deck](#mail-on-the-deck)).

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
2. **Sign in.** Right-click the flex-screen tray icon → **Connect Google Calendar and
   Tasks…**. Your browser opens Google's sign-in:
   - Pick the account from part 1.
   - *Google hasn't verified this app*: that's your own app, unverified because you didn't
     submit it. Click **Advanced** → **Go to flex-screen (unsafe)**.
   - **Tick both boxes** (calendar and tasks) → **Continue**.
   - The tab says *flex-screen is connected to Google*, and the deck shows
     *Connected to Google Calendar and Tasks*. You have five minutes to finish the sign-in;
     after that, choose the menu item again.
3. **Check the deck.** Within a few seconds Today's *Up next* shows your next event (or
   *Nothing else today*). After that the companion reads Google every 5 minutes.

The sign-in is stored on the PC encrypted for your Windows account (`google-token.bin` next to
the client file), so it's useless anywhere else.

## Mail on the deck

The deck shows Google Tasks whose notes contain a `[todo src=gmail:…]` tag: the action items
the `todo` skill creates when it goes through your inbox with Claude (Gmail and Google Tasks
connected to Claude). From those notes it also takes a `From: Name <address>` line for the
sender and the task's due date. Ticking one on the deck marks it completed in Google Tasks;
it stays shown, struck through, until midnight.

So the *From mail* card fills once two things are true: the deck is connected (part 2), and
the `todo` skill has run (for example through `personal-assistant` or `todo`). A task you add
by hand shows too if you put such a tag in its notes. **Open Gmail** just opens
mail.google.com.

## More than one Google account

- **Calendar:** the deck shows every calendar *ticked* in Google Calendar for the signed-in
  account. To add another account's calendar, in that account: Google Calendar → Settings →
  the calendar → **Share with specific people** → add your main address with **See all event
  details**; accept the invite in the main account. A meeting on both calendars shows once.
- **Mail:** forward the other account's mail to the main one (Gmail → Settings → **Forwarding
  and POP/IMAP**), so the `todo` skill sees it.

## On another PC

Copy `google-client.json` to `%LOCALAPPDATA%\flex-screen\` on that PC and do part 2 there.
One client serves any number of PCs; each signs in on its own.

## Disconnecting

- To stop on one PC: delete `google-token.bin` from `%LOCALAPPDATA%\flex-screen\` (the deck
  then asks to connect again). `uninstall.cmd` removes both files.
- To revoke access everywhere: [myaccount.google.com/permissions](https://myaccount.google.com/permissions)
  → flex-screen → **Remove access**.

## Troubleshooting

The companion's log says what Google answered: tray icon → **Open log**, lines starting with
`google:`.

| What you see | Why, and what to do |
|---|---|
| Deck: *Connect Google Calendar*, and the tray item shows a note instead of a browser | The client file isn't at `%LOCALAPPDATA%\flex-screen\google-client.json` (check the name: Windows may have kept `.json.json` or the long downloaded name). |
| Log: `Google Calendar API has not been used in project … or it is disabled` (or the same for Tasks) | Part 1, step 2: enable that API. It can take a few minutes to take effect; the companion tries again every 5 minutes. |
| Toast: *Google didn't grant Calendar (or Tasks) access* | A box was left unticked on the consent page. Choose the tray item again and tick both. |
| It worked, then stopped after about a week | The app was still in *Testing* (part 1, step 4). Publish it, then connect again from the tray. |
| Log: `token request failed: invalid_grant` | The sign-in was revoked or expired; the companion drops it. Connect again from the tray. |
| *Error 403: access_denied* in the browser | The app is in *Testing* and this account isn't a test user: publish it (step 4), or add the account under **Audience** → **Test users**. |
| Deck: *Calendar unavailable* | Google couldn't be reached right now; the last data stays shown. |
| *From mail* says *Nothing from mail to do* | Connected and working; there are no open tasks with the `[todo src=gmail:…]` tag yet. Run the `todo` skill. |
| An event from another calendar is missing | Tick that calendar in Google Calendar's list (left side, *My calendars* / *Other calendars*). |
