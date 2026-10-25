# Dynamic stamps from the /qb list, and Prismatic universal damage (2026-10-19)

RynthOracle 0.6.3, RynthILT 0.2.25 and RynthAi 0.5.93-legacy-ui, all ProductVersion 2026.10.19.3.
Previous releases: Oracle 0.6.2, RynthILT 0.2.24 and RynthAi 0.5.92 (2026.10.19.2). The Items panel
change is in RynthCore engine 2026.10.19.3.

## Dynamic stamps not showing done (Silentkelpie)

Oracle read every `/myquests` completely (2,653 flags, the same each time). It decided a Dynamic
stamp only from those flags, and two things kept stamps out of reach:

- **`/myquests` lists only flags the server holds quest text for.** ACECustom's handler skips a
  flag with no quest definition. Kelpie has 2,653 flags in `/myquests` against 15,000+ in `/qb`.
- **RynthILT's `/qb list` reader dropped 93 of the 243 listed stamps.** Its flag-line filter
  allowed only letters, digits and `_ - ! @ ( ) ?`, and stamps use `#`, `$`, `&` and `^`
  (`Dynamic_1_&9jiyTB!`, `Dynamic_1_XH!!nh#^NaRp!`).

### Changed

- **RynthILT.** The `/qb` flag-line filter also accepts `# $ & ^ % * + . ~ =`. Lines with spaces
  (chat, sentences) and channel lines are still refused.
- **RynthOracle: two sources.**
  - A Dynamic stamp is done when it is in `/myquests` or in the completed `/qb` list. Both use the
    same matching: exact, the lost `!` added, and the unique prefix.
  - "Not in the list" stamps come from both sources too.
  - The rows are rebuilt when a new `/qb` list arrives.
- **RynthOracle: the Dynamic stamps bar.**
  - It reads the `/qb` list once per session if RynthILT hasn't yet (the server allows one a minute).
  - It says whether the `/qb` list is counted, and has a "Read /qb" button.
- **RynthOracle: log.** One line whenever the counts change:
  `Dynamic stamps: N Dynamic flag(s) in /myquests, M in the /qb list (K completed flags read); X of 243 listed stamps done.`

## Items: Prismatic damage type (missile weapons and ammo)

The owner, 2026-10-19: prismatic ammo is universal damage for all types.

- **The Items panel** offers "Prismatic" as the damage type for missile weapons and ammo only.
  Other kinds keep the eight elements. It works in both the ImGui and the Avalonia faces, with its
  own colour.
- **RynthAi.**
  - A launcher set to Prismatic, or prismatic ammo (DamageType Base, a Prismatic name, or ammo set
    to Prismatic in Items), does the target's best element. That is the Monsters rule's damage
    type when set, otherwise the weakest element.
  - Prismatic ammo ranks as that element when the ammo is chosen and wielded.
  - With nothing known about the target, it behaves as before: the launcher's element, else Pierce.
  - "Prismatic" is never an element of its own: it never reaches the weapon planner or the
    weakness tables.

## Tests

- **RynthOracle.Tests** 460/460. New: a stamp only in the `/qb` list is done; the `/qb` list takes
  the same rules; "not in the list" comes from both sources; the log counts.
- **RynthILT.Tests** 123/124. New: stamp lines with `# $ & ^` are read and chat lines aren't.
  "move: ... redirect" was already failing.
- **RynthAi host tests** 1161/1163. New "ammo: Prismatic is universal damage". "map bake" and
  "mini remote" were already failing.
