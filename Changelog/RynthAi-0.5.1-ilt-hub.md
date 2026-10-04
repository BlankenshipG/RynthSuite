# RynthAi 0.5.1 — ILT Hub

**Release date:** 2026-10-04
**Plugin version:** `0.5.1-legacy-ui` (file version `0.5.1.0`)
**Previous release:** `0.5.0` (backed up to `C:\Games\RynthSuite\RynthAi\PreviousReleases\0.5.0\`)

This release brings UtilityBelt's ILT Hub into RynthAi as a native ImGui window. It covers
priorities P0, P1 and P2 of the UB Hub merge plan. RynthAi's own combat, looting and
salvaging still work the way they did. The hub only adds the ILT server features on top.

## New: ILT Hub window

- Open it from the **ILT Hub** launcher button or with `/ra hub show`.
- It has five tabs: **Character · Pet · Banking · Gear · Games**. The hub remembers the
  last tab you used.
- The header shows the world name and server-feature status. It also has a Refresh
  button, a "Force ILT world" override and a profile menu (save or load per character,
  or shared).
- Destructive actions ask for confirmation first. These are bank transfers, Enlighten,
  equipping suits, applying USD imports and splitting arrows.

### Server feature gating

- The hub and launcher button show up only on an ILT-like world (the name contains
  "leaftide", or you set the force override) and only when at least one ILT feature is
  enabled.
- Each feature checks its own on/off flag. The hub learns these from `/ilt features`
  when the server supports it. Otherwise it runs read-only probes after login (`/bank`,
  `/aug`, `/qb`, `/pets`, `/shinies`, `/xp`, `/pb`, `/blackjack`, `/fblackjack`,
  `/clap`). When the server refuses a command, that feature is switched off.
- The hub never probes `/enl`, because that command opens a confirm dialog.

### Banking (P0)

- Shows parsed `/bank` balances, with optional auto-refresh.
- Transfers go out as `/bank t <code> <amount> <target>`. They need confirmation, are
  spaced at least 5 seconds apart and are blocked if you try to send to yourself.
  Amounts accept k/m/b/t suffixes.
- The transaction log is filtered by channel, keeps up to 1000 entries and can be
  exported as CSV or copied.
- On first use the hub imports one time from UB's `bankbalances.json`,
  `transactions.json` and `itemconversions.json`.

### Pet (P0/P1)

- Pet essence picker that edits RynthAi's existing Pet consumable rules (priority and
  enabled state).
- Pet-refill and Universal-Mastery charms are detected from the item's ON/OFF status.
- HealingBuddy keeps a heal pet summoned while you are hurt and dismisses it once you
  have recovered.
- `/pets` roster and `/shinies` log.

### Character (P1)

- Session rates: XP, luminance, coins, pyreals, conversion items and kills per hour. The
  conversion table can be edited.
- Quest Tracker UI for kill tasks, one-time quests and timed quests, with ready-in
  timers. It can read friendly names from an optional
  `C:\Games\RynthSuite\RynthAi\quests.xml`.
- `/qb` viewer.
- Augmentation planner, using the server cost formula and soft-cap multipliers.
- Enlightenment planner that lists what is blocking you. Auto-enlighten has to be
  armed every session and always asks for confirmation.
- XP spend planner and an attribute-raise loop.

### Gear (P1/P2)

- Charm strip, including toggling Guardian Hand.
- Dispel keeper, which removes harmful enchantments with runes and has editable
  include and exclude lists.
- Surging Strength keeper.
- Chunk (hammer salvage) state machine and Clap, either manual or automatic on a
  randomized interval.
- Split arrows into White Quartz bags.
- Equip `.utl` suits: list, test or load profiles with retry and stall detection.
- VTank USD importer that previews, then applies, monster, weapon and consumable
  settings.

### Games (P2)

- Blackjack and fblackjack pads, plus Powerball info, buying and tickets. Each one
  shows only when the server enables it.
- Optional Games HUD overlay.

## New commands

- `/ra hub show|hide|toggle|refresh|bank|status|force on|off|clap`
- `/ra hub profile list|save <name> [shared]|load <name>`
- `/ra hub suit list|test [name]|load [name]`
- `/ra quests [refresh|check <regex>]`

## Changed

- **PetManager:** two new hooks, `AllowSummonOnEmpty` (summon even when the essence
  reports 0 charges while the refill charm is on) and `HoldSummons` (lets HealingBuddy
  pause combat summons).
- **QuestTracker:** supports quiet refreshes, so hub-initiated `/myquests` output is
  hidden from chat. It also adds thread-safe snapshots, a `RefreshCompleted` event,
  quest descriptions and time-until-ready. A new listing replaces the old one.
- **Chat routing:** the hub and quest tracker now see chat lines first and can hide
  their own replies from the chat window.
- **Version:** the csproj now sets `Version`, `AssemblyVersion`, `FileVersion` and
  `InformationalVersion`, matching the value `GetVersion` returns.

## New data files (per character folder)

- `ilt-hub.json`: hub state and settings.
- `ilt-hub-transactions.json`: bank transaction log.
- `ilt-hub-profiles\<name>.json`: per-character profiles. Shared profiles go in
  `C:\Games\RynthSuite\RynthAi\IltHubProfiles\`.

## Not included (by design)

- AutoTinker, AutoVendor and AutoXp from the UB hub.
- VVS XML layout port.
- ACE `/config` and `/charms`.
- Changes to the Avalonia `RynthAiPanel`.

## Build notes

- NativeAOT x86 publish is clean, with no trim or AOT warnings. The only compiler
  warnings are old unused-field warnings in files this release doesn't touch.
- Tabs are drawn as buttons instead of an ImGui tab bar. This keeps the saved tab
  selection without needing ImGui.NET's `ref bool` overload of `BeginTabItem`.
