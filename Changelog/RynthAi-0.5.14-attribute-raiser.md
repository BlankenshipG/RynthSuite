# RynthAi 0.5.14-legacy-ui — infinite-attribute raiser and Server Features

Needs the matching RynthCore SK-local engine (Progression tab section and the Settings text row).

## Attribute raiser (Skills panel > Progression)

For servers with infinite attributes (ACECustom / Infinite Leaftide), where attributes and vitals
go past the retail cap and the client's XP tables stop applying. Modelled on UB's Leaftide
XP Calculator, using the same server commands:

- **Costs** come from the server: `/xp all` ("[XP] Your XP cost for next 1 Strength level is: N").
- **Raises** use `/attr <abbr> 1` and wait for the "[ATTR] ... raised by 1, costing N" reply,
  then re-read the costs before the next raise.
- **Raise now** spends unassigned XP on the ticked stats until nothing ticked is affordable,
  a reply is not a success, or 200 raises (the next run carries on).
- **Auto-raise** does the same every N minutes (default 5; first run 30 s after it is turned on).
- **Order**: up/down arrows set the priority of all nine stats (default Coordination, Quickness,
  Strength, Endurance, Focus, Self, Health, Stamina, Mana).
- **Raise order** mode: Priority (top of the order first, fills it while affordable), Round robin
  (one level each in order), or Cheapest next level first.
- **Keep in reserve**: unassigned XP the raiser never spends (k / m / b / t suffixes).
- **+1 / +10** per stat send `/attr <abbr> N` directly; the server's reply shows in chat.
- Never runs at the same time as auto-enlighten's spend-before-/enl loop.
- Chat: `/ra hub attr run|stop|costs|status|auto on|off|every <min>|raise <stat> <n>`.
- Saved per character in `ilt-hub.json` (`AttrAutoRaise`, `AttrAutoRaiseMinutes`, `AttrRaiseMode`,
  `AttrKeepXp`, `AttrOrder`, `AttrRaiseStats`).

## Server Features (Settings > Misc)

One gate for the ILT Hub, the Mini Remote, the Progression planners and the attribute raiser,
replacing the hard-coded "InfiniteLeaftide / contains leaftide" check:

- **Servers**: comma-separated world names; `*` is a wildcard, case does not matter.
  Default `InfiniteLeaftide, *Leaftide*` (what the old check accepted).
- **Force enable on this server (manual override)**.
- A status line says whether the current world is on and why.
- The older per-character ILT Hub "Treat this world as ILT" / `/ra hub force on` still works.
- Features still follow what the server reports (for example `/xp` off keeps the raiser off).
- Saved in the RynthAi profile (`FeatureServerNames`, `ForceServerFeatures`).

## Tests

`Tools/RynthCore.RynthAiHostTests` — `AttributeRaiserTests`: name patterns, list parsing and the
override, pick per mode, ticks / reserve / unknown costs, order repair, server line parsing, a full
run on the fake engine (costs → raise → re-read → stop), and gating off a non-listed world.
