# RynthSuite: the Fellowship view reads big fellowships; Infinite Leaftide's /fship commands (RynthAi 0.5.57, RynthILT 0.1.3)

Branch `feat/dynamic-sections`, after the server plugins commit (RynthAi 0.5.56, RynthILT 0.1.2).
No engine change: it runs on RynthCore 2026.10.5.48 (the plugin-command broker and the sections
manifest are already there).

| Component | Previous | Now |
|---|---|---|
| RynthAi | 0.5.56-legacy-ui | 0.5.57-legacy-ui |
| RynthILT | 0.1.2 | 0.1.3 |

## Fixed: the Fellowship view showed no members

RynthAi's `FellowshipTracker` stopped reading when the fellowship had more than 9 members (the
retail limit). ACECustom servers allow 29 (`Fellowship.MaxFellows`), so on Infinite Leaftide a
fellowship of 18 or 26 showed its name and status line but no member rows. The same empty list
also affected corpse looting's "fellowship member" check and the radar's fellow markers.

- The limit is now `FellowshipTracker.MaxMembers` (64). The hash-table and chain guards stay, so
  a torn read still stops early.
- Member levels are read too (`Fellow._level`, entry +0x0C) and shown in a Level column.
- New `GetMembers()` returns id, name and level from one read. The view used to mix
  `MemberCount` (live) with the cached list, which could disagree for up to 2 s.
- The leader is listed first, then everyone else by name.

Checked against the live client (read-only): with the limit lifted, the walk returned all 18 and
26 members with the right names, levels and flags.

## New: Infinite Leaftide fellowship commands (RynthILT)

ACECustom's `/fship` command (`PlayerCommands.cs`) as buttons:

| Button | Chat command | Notes |
|---|---|---|
| Create | `/fship create <name>` | needs a name |
| Invite | `/fship add [name]` | empty name: your selected player |
| Dismiss | `/fship remove [name]` | leader; asks first |
| Invite landblock | `/fship landblock` | not in the Marketplace; asks first |
| List nearby | `/fship list` | once every 10 s (server limit) |
| Leave | `/fship leave` | asks first |
| Disband | `/fship disband` | leader; asks first |
| Help | `/fship` | the server's help |

A name with spaces is sent quoted (`/fship add "Raw Cigam"`), which ACE's command parser joins
into one argument. Quotes and control characters in the name box are dropped. The buttons that
ask first turn into "Confirm ...?" on the first click and run on a second click within 4 s.

Where they show:

- RynthCore's Mini Remote, Fellowship section: a new "ILT fellowship (RynthILT)" pane under the
  member list (surface `section:ilt-fellowship`, server `ilt`), so RynthCore shows it only while
  it treats the world as Infinite Leaftide.
- Its own window: `/ilt fellow [show|hide|toggle]` (also `/ilt fellowship`, `/ilt fship`).
- RynthAi's Mini Remote Fellowship tab, linked dynamically (next section).

## New: server commands linked into RynthAi's Fellowship tab

A server plugin can now hand RynthAi its commands for a section, so RynthAi's view shows the
active server's commands without knowing any of them.

- Format: `Plugins/Shared/ServerCommandShare.cs`, compiled into both plugins. The broker action
  is `servercommands`; the payload is tab-separated lines: `S section serverId serverName`, then
  `C label command flags tooltip`. Flags: `a` optional argument, `A` required argument,
  `c` ask first.
- RynthILT sends the `/fship` list for the `fellowship` section while ILT is active, when that
  changes and again every 10 s. It sends an empty list when ILT stops being active and at logout.
  "Active" is RynthCore's verdict once RynthCore has sent one; before that, the world name has
  "Leaftide" in it.
- RynthAi (`Huds/ServerCommandModel.cs`) keeps the latest list per section. It drops a list that
  is cleared, or that hasn't been re-sent for 35 s (for example, RynthILT was unloaded).
- Both sides check every command: one `/word ...` line of printable text, no quotes, at most 96
  characters, and at most 24 commands.
- Drawn by `Plugins/Shared/ServerCommandPanel.cs`, the same buttons in both plugins. Clicks queue
  the chat line for the plugin tick (`Host.InvokeChatParser`), never on the render thread.
- RynthAi's tab shows the commands even when you are not in a fellowship, since that's when you
  want Create. In RynthCore's Fellowship section RynthAi leaves them out, because RynthILT's own
  pane is there.

## Tests

`Tools/RynthILT.Tests`, new `FellowshipCommandTests` (5 tests). They compile RynthILT's list, the
shared format and RynthAi's store together, and cover:

- the round trip;
- name quoting, and Create needing a name;
- rejected commands and payloads, flags, the 24-command cap and field cleaning;
- the store's clear, stale timeout, and logout clear;
- the active check.

Result: 26 tests and 298 checks pass.
