# Quest loot profiles from base-quest-dynamic (2026-11-05)

Silentkelpie asked for loot profiles for the quests, built from his base-quest-dynamic profile and the
quest data (the Leaftide wiki quest guide, 486 quests with loot).

- A quest's loot profile is LootProfiles\base-quest-dynamic.utl with a Keep rule per quest item in front
  ("Quest: Shiny Red Stone", the exact name, any case; a wiki note in brackets such as "(Quest Item)" may
  be left off in the game). The template's own rules and salvage combine settings follow. Written to
  LootProfiles\Quests\<quest>.utl and built again each time, so an edit to the template reaches every
  quest. No template: the quest's items only, and chat says so. (RynthCore.LootSdk QuestLootProfiles.)
- RynthOracle:
  - Run this quest makes the quest's profile the one in use while it runs, and goes back to the one
    before when it ends (a profile picked meanwhile is kept).
  - The guide's "Loot profile" button writes the open quest's profile.
  - `/ro lootprofiles` writes every quest's; `/ro lootprofile <quest>` writes one.
- RynthAi: the "questloot" broker action takes `build <quest>|<item>...`, `profile <quest>|<item>...` and
  `profile off`; `/ra questloot profile [off]` shows or ends it by hand.

## Also fixed

- Run this quest's loot never reached RynthAi's quest loot list: RynthOracle sent "questloot add X" as
  the value of the "questloot" action, and RynthAi read the verb as "questloot" and refused it. RynthOracle
  now sends "add X"; RynthAi also takes the old form.
