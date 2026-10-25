# RynthAi 0.5.99: Infinite Leaftide pieces of the Mini Remote show on ILT only

Build 2026.10.20.9 (RynthAi `HandWrittenVersion` 0.5.99-legacy-ui).

Audit (owner, 2026-10-20): bounties, guardian, breeding, vaulted dungeons and the crafting ring
spells must show on Infinite Leaftide only, decided at run time (RynthILT's ILT verdict:
detected, or forced in RynthCore Options > Diagnostics), under the Mini Remote's right-click
Character menu and in the Mini Remote.

## Already gated (no change)

- RynthILT's Mini Remote tabs (Gear, Games, Vaulted Dungeons): RynthILT only marks them shown
  on an ILT world; Vaulted Dungeons also needs its master switch.
- The remote's Bank / Guardian sections and the Pets breeding tracker: RynthILT draws nothing
  off ILT.
- RynthCore's ILT panels (Bounties, Guardian, Progression): tagged server "ilt", left out of the
  Mini Remote rail unless Infinite Leaftide is active; "/rc bounties|guardian" say so off ILT.
- The Character menu's Banking and Pet Breeding entries: ILT only.
- RynthAi's own "Games" section in the engine's Mini Remote: an empty section isn't shown.

## Fixed

- **Pets > Breeding** was a sub-tab on every world (with a "not Infinite Leaftide" note). Off
  ILT, or when the server reports pet breeding off, the Pets tab is now the roster alone.
  "/ra hub open|tab breeding" answers that breeding is an ILT feature instead of opening it.
- **Crafting > Rings** (the ring inscription spells) was on every world with a "may not have
  them" note: ILT only now.
- **Crafting > Trinkets**: ILT only, unless the server plugin or the player's custom rows give
  that bench something (retail AC has no trinket tinkering).
- **Mini Remote Options**: the "Bank" and "Guardian answer" checkboxes show on ILT only, and off
  ILT those sections no longer take an (empty) column in the horizontal layout.
- New `MiniRemote/MiniRemoteGates.cs` holds these rules in one place.

## Tests

- New: "mini remote: Breeding, Rings, Trinkets, Bank and Guardian show on Infinite Leaftide
  only" (every gate, and none of them without RynthILT).
- Updated: the "/ra hub" commands test (breeding refused without RynthILT; the every-world help
  lists "pets|crafting"). It had been failing on the old help text; it passes now.
