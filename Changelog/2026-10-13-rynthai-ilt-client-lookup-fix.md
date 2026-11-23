# RynthAi 0.5.60-legacy-ui - the Mini Remote finds RynthILT again

Previous release: 0.5.59-legacy-ui.

## Problem

On Infinite Leaftide the Mini Remote showed no ILT tabs or sections, and the
ILT feature bits and `/ra` ILT verbs acted as if RynthILT wasn't loaded. This
happened even though:

- the engine logged `IltInfo: Infinite Leaftide: infiniteleaftide.online matches ...`;
- RynthILT logged `'InfiniteLeaftide' is ILT-like` and a successful server check
  (10 features on, 2 off).

## Cause

`MiniRemote/IltClient.Tick` looks up RynthILT's "RynthILT.Hub" table through
the host. Its "never looked" sentinel was `_checkedAt = long.MinValue`, both at
start and after `Reset()` (every login and logout). With that sentinel,
`now - _checkedAt` overflows to a negative number, so the "recheck in 3 s"
gate always said "not due". As a result the lookup never ran, and RynthAi
answered "no RynthILT" for the whole session.

## Fix

- An explicit `_checkedOnce` flag replaces the sentinel time.
- The gate is the pure `IltClient.LookupDue`:
  - always due on the first Tick and the first after `Reset`;
  - due at once when RynthILT's table has shut down (Version 0);
  - otherwise due every 3 s.

## Tests

`Tools/RynthCore.RynthAiHostTests/Tests/IltClientLookupTests.cs` (new, 3 tests):

- the first lookup is due at any clock value;
- a live table is kept for the interval;
- with no table, it retries every interval; a shut-down table is looked up again at once.

Result: 966 of 967 pass. The one failure is the map bake test, which needs the
AC dat files on disk and is unrelated.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/MiniRemote/IltClient.cs`
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs` (0.5.60-legacy-ui)
- `Tools/RynthCore.RynthAiHostTests/Tests/IltClientLookupTests.cs`, `Program.cs`
