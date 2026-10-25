# RynthOracle: Dynamic stamps match the server's flags (2026-10-19)

RynthOracle 0.6.4, ProductVersion 2026.10.19.5. Previous release: RynthOracle 0.6.3
(2026.10.19.3).

## Fixed: done Dynamic stamps still shown as not done (Silentkelpie)

After 0.6.3, Oracle read Kelpie's `/qb` list and found 3 Dynamic flags in it, but matched 0 of the
243 listed stamps. Kelpie has done at least 2.

The reason is in the stamp list. ACECustom names each Dynamic errand `Dynamic_1_` plus random
letters, digits and `! @ # $ ^ &` (`QuestManager.ComputeDynamicQuest`), and says
`You've stamped {flag}!` when it stamps one. 238 of the 243 stamps on the players' sheet end in
`!`: the closing `!` of that chat line was copied with the flag. Oracle only ever tried adding a
`!`, never removing one, so a real flag like `Dynamic_1_Bq7Brhg` could not match the sheet's
`Dynamic_1_Bq7Brhg!`.

### Changed

- **Matching.** A stamp is tried as listed, then without the copied closing `!`, then with `!`
  added. Next comes its errand's delivery flag (`_d`), and last, for a row cut short, the one
  errand whose flag starts with the stamp without its `!`.
- **Chain errands.** The NPC the item is delivered to stamps the errand's flag with `_d` added.
  That flag counts as the same stamp. It isn't shown as a separate "not in the list" stamp or as an
  unknown quest flag. An unlisted errand shows once, by its own flag.
- **Both halves of the `/qb` list.** Dynamic stamps are now looked up in both the completed and the
  incomplete sections. ACECustom writes a stamp's account row with one completion when the NPC
  stamps it. Two reads of Kelpie's list 90 seconds apart split the same 3,859 flags 3,740 / 119 and
  3,132 / 727, so a stamp could move into the incomplete section and read as not done. Only the
  Dynamic flags are kept from the list.
- **Log.** The `Dynamic stamps:` line also names up to 10 Dynamic flags that no listed stamp
  matched. The bar under the stamps says how many Dynamic flags the `/qb` list has.

## Tests

- **RynthOracle.Tests** 470/470. New: the server's flag without `!` matches the sheet's stamp with
  `!`; a sheet `!!` matches a flag ending in one `!`; an errand's own flag and its `_d` count as one
  stamp; a `_d` flag alone marks it done; an unlisted errand shows once; a cut-short row matches by
  its stem, but not when two errands share it.

## Known

- Why RynthILT's completed / incomplete split moved between the two reads isn't known yet. The
  line count was the same both times, and the capture keeps lines in order.
