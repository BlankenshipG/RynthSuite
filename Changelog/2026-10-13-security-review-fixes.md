# RynthSuite: security review fixes (RynthAi 0.5.54, RynthInventory 0.1.1, ub-Rythai 3.1.11)

Branch `fix/security-review-2026-10-13`, from `build-comp-10-11` (2c9fb1d). It pairs with the RynthCore
branch of the same name (engine 2026.10.5.45, StatusAgent 1.0.1); build the two together.

| Component | Previous | Now |
|---|---|---|
| RynthAi | 0.5.53-legacy-ui | 0.5.54-legacy-ui |
| RynthInventory | 0.1.0 | 0.1.1 |
| ub-Rythai | 3.1.10 | 3.1.11 |

## F1: a '%' in shown text could crash the client

ImGui.NET passes the text of `Text`, `TextColored`, `TextWrapped`, `TextDisabled`, `BulletText`,
`SetTooltip`, `SetItemTooltip` and `LabelText` to native ImGui as a printf format string with no
arguments. A `%` in a chat line, a translation, an item or player name, or a server reply made ImGui
read arguments that were never passed. That draws garbage, and `%s` or `%n` can crash the client.

- New `Plugins/Shared/SafeImGui.cs` doubles every `%` and keeps ImGui's signatures. It is linked into
  RynthAi and ub-Rythai.
- 180 calls in 27 files now go through it: TranslateUi (16), the ILT hub tabs, the legacy UI, the
  HUDs, and the ub-Rythai panels. Calls whose text is a plain literal with no `%` are left as they were.

## M4 and L1: chat signals a player could fake

Two readers acted on chat text found anywhere in a line and in any chat type, so another player could
type it:

- **"Your missile attack hit the environment."** This counts toward the environment-impact throttle,
  which drops or approaches the target.
- **A `~ael1 {"t":"mule",...}` data line, or "Unknown command: mule-data".** RynthInventory's vault feed
  took it in and hid it from chat.

The fix:

- New `Plugins/Shared/ChatSource.cs`, linked into RynthAi, RynthInventory and RynthInventory.Tests:
  - It lists the chat types a player writes in (ACE `ChatMessageType`: Speech, Tell, Channel, Social,
    Emote, Allegiance, Fellowship and their outgoing forms).
  - It matches a marker only at the start of the line. The client puts the speaker's name in front of
    player text, so a player's copy never starts with the marker.
- `CombatManager.HandleChatForDamage(text, chatType)`:
  - It skips player chat types; every line it reads is a combat or system line.
  - The missile line must start with "Your missile attack hit the environment". ACE sends it as a
    Broadcast line.
- `VaultFeed.ShouldEat(text, chatType)`:
  - It skips player chat types.
  - `~ael1 ` must start the line.
  - The refusal must be "Unknown command:" at the start and name exactly `mule-data` (a leading `/` or
    `@` is allowed).
- Tests:
  - `EnvironmentImpactTests.MissileSpoof` covers speech, tell, emote and channel copies, plus the real
    line.
  - `VaultFeedTests` has spoof cases, ACE's real Help-type reply, and leading whitespace.

## M3: actiontrygiveprofile

A meta someone shared could do two things through this action:

- name any file as the profile, including a full path;
- name any nearby player as the target, handing your inventory to their character.

Now:

- **The profile must be inside `RynthAi\ItemGiver\`** (`ExpressionEngine.ResolveGiveProfilePath`).
  - A subfolder is fine.
  - Refused: full paths, drive-relative and UNC paths, `:` (alternate streams), and `..\` escapes.
  - A refusal returns "0" and is logged once per argument, not on every pulse.
- **The target must be an NPC or a vendor.** It is read from the weenie bitfield, as the meta's
  ObjectClass is. A player with the same name is skipped.
- **The give uses AC's give action (`GiveObjectTo`, whole object).** An NPC ignores
  `MoveItemExternal`, the move-to-container call the action used before. Without the give action, the
  action returns "0".
- **Players:** to give to a player, type `/ra ig <profile> to <player>`. `ExpressionReference.txt`
  documents the new limits.
- **Tests:** `GiveProfileTests` covers the path rules, the NPC against a same-named player, and the
  refusal being logged once.

Note: a meta that gave to a player (the old "My Salvage Mule" example) now returns "0". That is the
intended change.

## Tests

| Suite | Result |
|---|---|
| RynthInventory.Tests | 220 passed |
| RynthCore.RynthAiHostTests | 677 of 678 passed |

The one RynthAiHostTests failure is the map-bake check against the real game data files, which needs
the dats on the PC; it fails the same way without these changes.

Full run on 2026-10-13: RynthAi, ub-Rythai, RynthInventory, the engine and StatusAgent all build.
Every test project in RynthSuite `Tools` and RynthCore `tools` passes (51 projects) except two, and
both failures predate this branch:

- RynthCore.RynthAiHostTests: the map-bake check above.
- RynthLua.ScriptWindowsTests (1005 of 1009 pass): two tests read `Docs/RYNTHLUA*.md`, which isn't
  in the repo, and two icon-stacking checks fail against this PC's game icons. This branch doesn't
  touch RynthLua, its tests or the engine's `AcIconDecoder`.

## Not changed

- `Panels/SkillsFace.Progression.cs` in the engine still has 5 direct ImGui text calls. It holds
  uncommitted local work, so it is left for after that lands.
