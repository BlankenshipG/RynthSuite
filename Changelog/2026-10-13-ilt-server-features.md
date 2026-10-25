# 2026-10-13 - RynthILT 0.2.5: the ILT settings page lists Infinite Leaftide's features

RynthILT 0.2.5. Previous release: 0.2.4.

The Features part of the ILT settings page showed only a few raw keys. It now lists what
Infinite Leaftide (ACECustom) runs beyond retail, grouped by section, with the state of each one.

Detection is client-side only, and the server needs no change. The server type comes from the
login: the world name, and RynthCore's "ilt" server verdict. RynthILT's ILT profile is the known set
of what Infinite Leaftide runs, taken from ACECustom: its ServerConfig defaults, and
`deploy/PROD_DEPLOY_PLAN.md` for production values. What the server returns on its own (login probe
replies and chat lines) refines the profile.

## What changed

- **`IltFeatureCatalog` is the ILT profile (50 entries).** The entries include:
  - banking and bank notes;
  - augmentations and augmentation charms, ILT skills, leaderboards;
  - T11 and T11 scaling, the T11+ crafting rules (crafting uniqueness), split arrows, charms;
  - the pet systems: capture, breeding, maturity, bond, potency, strain, pyreal refill, Universal
    Summoning Mastery, naming;
  - `/fship`;
  - the combat options: smart ring, damage format, overkill, missile power bar, T11 hit gate,
    V11 endgame, defense scaling;
  - rifts, room assignment, portal requirements and `/unstuck`.
- The groups are now Character, Items, Pets, Fellowship, Combat and Activities.
- Upstream ACE's `fellowship_additive` option is not an ILT feature, so it isn't listed.
- **Each entry says how its state is known (`IltKnown`).**
  - **Profile (on in ILT):** for example T11, split arrows, pet breeding and maturity, pet bond
    and potency (both on in production, per the prod dump in the deploy plan), monster capture,
    Universal Mastery, the missile power bar and the T11 hit gate. These show as on with the
    ILT world, and a refusal in chat turns one off.
  - **Profile off (off in ILT):** pet strain (off since launch), defense scaling and the pyreal
    refill (both off by default). These show as off. A line that only an enabled feature sends
    turns one on: ACECustom's three summon-time refill messages now mark the refill on.
  - **Checked:** the login probe answers it (`/bank`, `/aug`, `/xp`, `/pets`, `/shinies`,
    `/pb`, `/blackjack`, `/fblackjack`, `/clap`, `/qb`, `/myquests`). It reads "checking"
    until the reply arrives.
  - Content with no switch (the Temple guardians, Paragon gear) is on with the world.
  - Off an ILT world nothing is claimed.
- **The server is no longer asked for a feature list.** The login refresh used to send
  `/ilt features` first, and ACECustom answers it with "Coming Soon". It now goes straight to the
  read-only probes. The unused `ILTFEATURE` line parser is removed.
- **Settings page layout.**
  - Each group is a collapsible section titled "Group (n of m on)".
  - Each row has a tooltip saying what the feature is, where it lives in the tools, and where
    its state comes from.
  - Charms get their own section, named from RynthILT's charm list and on with ILT unless
    refused in chat.
  - Any other bit seen in chat is listed under Other, which starts collapsed.
- The Hub's "Leaftide features" list and "/ra hub features" use the same state text.
- The Hub feature rows (what RynthOracle and other plugins read) carry the same catalog, charm and
  extra rows.

## Tests

- RynthILT.Tests: 73 of 73 pass. The new tests cover the following:
  - profile features are on with the world, and a refusal in chat turns one off;
  - probed features read "checking"; options ILT runs off are off until chat shows them on,
    including the three refill messages;
  - charm states and row descriptions;
  - the login refresh never sends `/ilt features`.
- RynthOracle.Tests: 191 of 191 pass.
