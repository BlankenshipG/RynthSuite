# Aetheria chunking uses only the Lugian Aetheria Hammer (Infinite Leaftide)

Date: 2026-10-15

| Plugin | Version | Previous release |
|---|---|---|
| RynthCore.Plugin.RynthILT | 0.2.8 | 0.2.7 |

## The problem

Gear > Chunking showed "Status: nothing eligible" with "Auto-chunk low aetheria" on and 9
Coalesced Aetheria under level 9 in the pack, and nothing was chunked.

RynthILT picked the chunking tool by name: any item ending in " Lugian Hammer". On Silentkelpie
that matched a **Flaming Lugian Hammer**, a loot weapon. Using a weapon on aetheria does nothing:
the server answers the use with no result line. Each aetheria waited 10 s, was then skipped for
the rest of the session, and once all 9 were skipped the status said "nothing eligible". The logs
show this in four sessions on 2026-10-15.

## Fixes

- **The tool is only the "Lugian Aetheria Hammer"** (exact name, any case), and never an equipped
  item. With no such hammer in the pack, the status says "no Lugian Aetheria Hammer carried".
- **Items with no answer are no longer hidden.** The status adds "N skipped with no answer (Chunk
  now retries them)", and each timeout is written to the log (category IltGear).
- **"Chunk eligible items now" retries the skipped items**: a manual pass clears the skip list.
- **Corrupted-weapon results are recognised.** The server's line names the item and the tool
  ("You smash the Corrupted Sword with the Lugian Aetheria Hammer."). A line naming a corrupted
  item with the hammer, powder or shatter now ends the wait as a result, as UtilityBelt's chunk
  policy does. Combat lines ("You smash Corrupted Shadow for 147 ...") are not results.
- The section header reads "Chunking (Lugian Aetheria Hammer)".

## Files

- `Plugins/RynthCore.Plugin.RynthILT/Hub/IltChunkClap.cs`: `IsChunkHammer`, `ClassifyChat`,
  the skip count in the status, the timeout log, the manual-pass retry.
- `Plugins/RynthCore.Plugin.RynthILT/RynthILTPlugin.cs`: version 0.2.8.

## Tests

- `Tools\RynthILT.Tests`: 91 tests, 1501 checks, all passed. 2 are new: only the Lugian Aetheria
  Hammer is the tool (the Flaming, plain and Acid Lugian Hammers are not), and the result-line
  classifier (corrupted line, aetheria line, refused, busy, and a combat line that is no result).

## Install

Published as NativeAOT and installed to `C:\Games\RynthSuite\RynthILT\`. The 0.2.7 DLL (and PDB)
is first saved to `RynthILT\PreviousReleases\0.2.7`.

Silentkelpie needs a **Lugian Aetheria Hammer** in the pack; the Flaming Lugian Hammer is not one.
