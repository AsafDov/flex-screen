# Task sources

The Tasks card on the deck's Capture page shows tasks from several places at once, each
keeping where it came from and a link back to it:

| Source | On the deck | Tick off | Add | Set up |
|---|---|---|---|---|
| Google Tasks | every list | yes | yes | [google-setup.md](google-setup.md) |
| Microsoft To Do | every list | yes | yes (not into Flagged email) | below |
| Obsidian | the open `- [ ]` tasks of your latest daily note | yes | yes, at the end of that note | nothing: on when your vault is found |
| Jira | open issues assigned to you, with project, key, priority and status | no: open them in Jira | no | below |
| GitHub | open issues assigned to you, in every repository | no: open them on GitHub | no | below |

All of them are under the companion's tray icon, **Task sources**. A source that isn't set
up is simply left out. Sites and choices go in your [profile](../README.md#profiles), so a
work profile can have Jira and GitHub and a home profile Google, To Do and Obsidian, and
switching profiles switches sources. Tokens stay on each PC, encrypted for your Windows
account (`%LOCALAPPDATA%\flex-screen\*.bin`), one per site: a work Jira and a home one each
keep their own, and each PC signs in once.

Tap a task on the deck to open it: its notes, what its source says about it, and an
**Open in …** button that opens it on the PC (the issue's page, the note in Obsidian,
the task in Google Tasks or To Do on the web).

The tray's **Task lists on the deck** picks which lists get a chip. A list you untick still
shows its dated tasks under Scheduled.

## Microsoft To Do

flex-screen reads To Do through Microsoft Graph with an app registration of your own (as
for Google), so nothing passes through anyone else's app.

1. Go to Microsoft Entra, **App registrations** (entra.microsoft.com, or portal.azure.com).
   A personal Microsoft account may first need a free Azure sign-up to get a directory.
2. **New registration**: any name ("flex-screen"); supported account types: **Accounts in
   any organizational directory and personal Microsoft accounts** (or *Personal Microsoft
   accounts only*, then use the tenant `consumers` below).
3. **Authentication** › Add a platform › **Mobile and desktop applications**, custom
   redirect URI `http://localhost`.
4. **API permissions** › Add › Microsoft Graph › Delegated › **Tasks.ReadWrite**.
5. Copy the **Application (client) ID** from the Overview.
6. Tray icon › Task sources › **Microsoft To Do…**: paste the client ID (tenant `common`,
   `consumers`, or your organization's), OK, and sign in in the browser.

A work account's organization may not allow apps it hasn't approved; then To Do stays at
home.

## Jira

1. Make a token:
   - **Jira Cloud** (`…atlassian.net`): an API token from Atlassian account settings ›
     Security › API tokens (id.atlassian.com/manage-profile/security/api-tokens).
   - **Jira Server / Data Center**: a personal access token from your Jira profile.
2. Tray icon › Task sources › **Jira…**: the site (`https://yourcompany.atlassian.net`), your
   email for Jira Cloud (empty for Server / Data Center), and the token. The companion
   checks it and says who you're signed in as.

Which issues: `assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC,
updated DESC`. Change it in the profile (tray icon › Profile › **Open profile file**):

```jsonc
"jira": { "url": "https://acme.atlassian.net", "email": "me@acme.com",
          "jql": "project = ACME AND assignee = currentUser() AND statusCategory != Done",
          "name": "Work" }   // the chip's name on the deck
```

The deck shows up to 50. Issues aren't changed from the deck: move them on in Jira.

## GitHub

Tray icon › Task sources › **GitHub…**: the host (`github.com`, or your GitHub Enterprise
server) and a token: a fine-grained token with *Issues: Read* on the repositories (or a
classic one with `repo`). Leave the token empty to use the GitHub CLI's sign-in
(`gh auth login` on this PC).

Which issues: `is:open is:issue assignee:@me archived:false`, any GitHub search:

```jsonc
"github": { "host": "github.com", "query": "is:open assignee:@me org:acme", "name": "GitHub" }
```

A label like `P1`, `priority: high` or `critical` shows as its priority, one like `in
progress` or `blocked` as its status, and a milestone's due date as its due day.

## Obsidian

On by itself once the companion finds your vault (as for the Capture page's Obsidian card).
It reads the **latest daily note** (today's, or the newest before it, as the Daily notes
plugin names them, or the Periodic Notes plugin when that makes your daily notes), for the
way of working where each day's note carries the open tasks over.

**Which vault.** Normally the one Obsidian has open, looked at again every 30 seconds. On a
PC with more than one vault (a home vault and a work vault), pick it: Task sources ›
Obsidian daily note › **Vault: …** lists the vaults Obsidian knows on this PC, and
**Another vault folder…** takes any other (the folder with `.obsidian` in it). **The vault
Obsidian has open** goes back to automatic. The pick is `"obsidianVault"` in this PC's `machine.json`,
per PC; if Google Drive moves the vault's folder, it's found again and the setting follows.
The companion log says which note it reads (`tasks: obsidian: reading WorkVault/Daily
Log/2026-11-17.md`), or where it looked when there's none.

- `- [ ]` and `- [/]` (in progress) lines are tasks; `[x]`, `[-]` and the rest aren't.
- An indented task is a subtask; indented bullets under a task are its notes.
- The heading a task is under shows beside it, with the Tasks plugin's priority (🔺 ⏫ 🔼 🔽 ⏬)
  and its 📅 due date (⏳ scheduled when it has none).
- Ticking one off on the deck writes `[x]` into the note (and the Tasks plugin's `✅ date`
  when that plugin is on); ticking it again puts it back.
- **+** adds `- [ ] the task` (and `📅 date`) at the end of the latest daily note, or makes
  today's note from your template when there's none yet.

To leave Obsidian off the deck: Task sources › Obsidian daily note › untick **Its tasks on
the deck** (`"obsidianTasks": false`).
