# Calendars

The deck's calendar (Today's **Up next**, the whole day behind it, the status bar's next
meeting, the meeting keys on the context bar) comes from every calendar in your
[profile](../README.md#profiles) at once:

| Source | What it needs | Set up |
|---|---|---|
| Google accounts | your own Google OAuth client, once | [google-setup.md](google-setup.md) |
| Microsoft 365 or Outlook.com | your own app registration in Microsoft Entra, once | below |
| A calendar link | an iCalendar address (`.ics`, `webcal://`) | below |

Everything is under the companion's tray icon, **Calendars**: each source with its state
(who's signed in, or what it still needs), its calendars to tick for the deck, its sign-in
or link, and **Remove**. A meeting that's on several of them (you and your work account
both invited) shows once.

The deck gets today's events from midnight (the day view shows the whole day, those over
dimmed) and tomorrow's, up to 24, read every 5 minutes, and at once after a change in the
tray or another profile.

## Which calendars

Each source's submenu lists its calendars; ticked ones go on the deck.

- **Google:** the calendars ticked in Google Calendar to begin with (yours and the ones
  shared with you). Untick one here to keep it off the deck without changing Google
  Calendar, or tick one that's unticked there.
- **Microsoft 365:** the account's main calendar to begin with; tick others (a shared team
  calendar, Birthdays).
- In the profile it's the account's `"calendars"`: `{ "hide": ["Birthdays"], "show": ["Team"] }`,
  by a calendar's name or id.

The day view names the calendar each event is from, unless it's an account's own main one;
then the account's name (Work, Personal), if it has one.

## Hiding events

**Calendars** › …, for every calendar at once (the profile's `"calendarFilter"`):

- **Hide events I declined** (on to begin with). One declined on one calendar but accepted
  on another still shows.
- **Hide all-day events.**
- **Hide events marked free** (Google's *Available*, Outlook's *Free* or *Working elsewhere*).
- **Hide events titled…**: words or phrases, separated by commas (*Lunch, Focus time*); a
  title with any of them, in any case, is hidden. A `/regular expression/` between slashes
  works too.

```jsonc
"calendarFilter": { "hideTitles": ["Lunch", "/^Focus/"], "hideAllDay": false, "hideDeclined": true, "hideFree": false }
```

## Microsoft 365 or Outlook.com

flex-screen reads Outlook calendars through Microsoft Graph with an app registration of your
own, as for Microsoft To Do (the same one can do both).

1. [Microsoft Entra](https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
   › App registrations › **New registration** (or open To Do's): *Accounts in any
   organizational directory and personal Microsoft accounts*.
2. **Authentication** › Add a platform › **Mobile and desktop applications**, redirect URI
   `http://localhost`.
3. **API permissions** › Add › Microsoft Graph › Delegated: **Calendars.Read** and **User.Read**.
4. Tray icon › **Calendars** › **Add a Microsoft 365 or Outlook calendar…**: a name (*Work*),
   the Application (client) ID, and the tenant (`common`; `organizations` for work accounts
   only; or your organization's). Microsoft's sign-in follows; pick the account.

A work account whose organization doesn't let you consent to apps can't sign in this way.
Its calendar can still come as a link: Outlook on the web › Settings › Calendar › Shared
calendars › **Publish a calendar** › copy the ICS address, then add it as a calendar link.

## A calendar link

Tray icon › **Calendars** › **Add a calendar link (iCal)…**: a name and the address. The
companion reads it once right away and says how many events it has today and tomorrow (or
why it can't read it). Addresses that work:

- **Outlook:** Outlook on the web › Settings › Calendar › Shared calendars › Publish a
  calendar › the ICS link.
- **Google Calendar:** the calendar's Settings › Integrate calendar › *Secret address in
  iCal format* (for one you can't sign in to; your own accounts are better added as Google
  accounts).
- **iCloud:** the calendar's sharing › Public Calendar › the `webcal://` link.
- A school's, a team's or a holiday calendar's `.ics`.

The address works as a password for that calendar, so it's kept on the PC (encrypted for
your Windows user, with the sign-ins), not in the profile: another PC on the same profile
lists the calendar and asks for its link once (**Change the link…**). A public calendar's
address can go in the profile instead, as the account's `"url"`.

Repeating events are expanded by the companion (daily, weekly, monthly, yearly; every
other week; the second Tuesday; the last Friday; with exceptions and moved occurrences), in
the time zone each event names. A feed is read at most every 4 minutes.

## Troubleshooting

The companion's log (tray icon › **Open log**) has a `calendar:` line for each source that
couldn't be read, with what it answered.

| What you see | Why, and what to do |
|---|---|
| **Calendars** › a source says *not signed in* | Its sign-in was never done on this PC, or was revoked: **Sign in…** in its submenu. |
| *no link on this PC* | A calendar link added on another PC: **Change the link…** and paste it here. |
| *can't be read now* | The source didn't answer, or answered an error (the log says which). Its last events stay on the deck. |
| Deck: *Connect a calendar* | No source in this profile is set up on this PC. |
| An event is missing | Its calendar isn't ticked in the source's submenu; or it's declined, all-day, free or titled with a word you hide. |
| A meeting shows twice | The two copies don't share a UID (they were added separately, not as one invitation). |
