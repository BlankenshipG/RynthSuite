# 2026-11-08 — Quest guide: pick any step, NPC quest offers, Dynamic Stamps hand in by themselves

## Pick the step to do
- In a quest of several steps, every step has its own button. While the run is on: "Do step N" goes to that step
  (done or not); RynthNav's trip to the old one stops. Not running: "Run from step N" starts the run there. Next and
  Back are still there. (QuestRunner.GoTo.)

## An NPC who starts a quest asks
- When an NPC talks to you ("X tells you, ..." / "X says, ..."), the wiki guide's quests that start with them are
  looked up (their "starts with" person, quotes and a note in brackets left off). Quests whose words the NPC's text
  shares come first. A small "Quest offer" window lists them (up to 4) with Run this quest, Show (opens it in the guide)
  and Not now.
- Left out: a quest being run, a one-time quest already done, one still on its timer, and one turned down this
  session. The same NPC is asked about once in 10 minutes, and never while a quest run is on. (Data/QuestOffers.cs,
  Views/QuestOfferView.cs.)

## Dynamic Stamps: Go and hand in
- The stamp box's Go is now "Go and hand in". RynthNav goes to the NPC, and on arrival a Parchment from your packs is
  handed over (the same /ub give as Give Parchment), followed by the /myquests read.
- With no Parchment in your packs, RynthAi first buys one from a vendor near you, then RynthNav sets off. If none could
  be bought there, it tries again at the NPC's place before the hand-in. "Buy one" next to "No Parchment" does it by hand.
- RynthAi "questloot buy <item>" (/ra questloot buy <item>): opens the vendors within 80 m in turn, a Scribe or a shop
  first, then the nearest (ACE walks you to each), until one sells it, and buys one. It says "[RynthAi] Quest: bought
  <item>." once it is in the pack, or "[RynthAi] Quest: couldn't buy <item>: <why>.".
