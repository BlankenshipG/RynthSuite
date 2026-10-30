# RynthAi: the lethal-ring escape no longer stops after 1.5 s (2026-10-30)

Silentkelpie's log (08:14:52): an Empyrean Handler died 0.4 s before RynthILT heard its ring.
RynthAi started the run, but the arbiter had given the tick to Looting. With Loot priority on,
combat's Think doesn't run during Looting, so the orphan watch ("combat stopped driving it")
turned autorun off 1.5 s later. The character stood 2 m from the ring and opened the corpse.

- While a ring escape runs, the arbiter keeps the tick for combat. Buffing, Loot priority,
  Nav priority and loot starvation wait until the escape is done.
- The combat heartbeat drives the run whenever Think doesn't. The run ends on its own limits:
  outside the ring plus 4 m, or 5 s, then a hold until the ring has gone off. It still ends at
  once when combat or the macro is switched off, or on death.
- Nav's stop on a change of activity (nav autorun off, StopCompletely) no longer cuts the run.
  The run turns away and autoruns again at once, and no StopCompletely is sent.
- A Handler killed as it channels leaves the client (its corpse is another object). The run
  goes on away from the spot it stood on, instead of stopping as "the caster is gone".
- The ring tests (RingEscapeTests, RingEvadeTests) were not being run by the host test exe.
  They are registered again, with three new ones.
- RynthILT: when RynthAi is already running its own escape, the [RingEvade] line now says so,
  instead of only "RynthAi isn't fighting".
