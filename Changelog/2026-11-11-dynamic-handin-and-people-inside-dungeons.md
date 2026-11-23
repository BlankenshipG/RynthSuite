# 2026-11-11 — Dynamic hand-ins answered; quest people inside dungeons and the Bur

Silentkelpie: "look at logs dynmanic also look at quests some npc can be inside dungeons or bur and should
route to them correctly".

## Dynamic stamps: the hand-in
- The log (Garaena the Emissary, 08:34) showed the stamp landing, but RynthAi's give queue reported "no answer,
  skipping" and "0 item(s) given". A dynamic-quest NPC looks at the Parchment and hands it back ("You allow
  Garaena the Emissary to examine your Parchment."); that line is now an answer: the queue says
  "1 examined and handed back" and stops re-sending.
- The first give went out 2 s after the arrival, while the character was still moving. It sat in the server's
  walk to the NPC, and the re-send 3 s later cancelled that walk ("Action cancelled!"). Now:
  - Go and hand in waits 1.5 s after RynthNav arrives before it gives.
  - A give to someone out of reach waits for the server's walk to them (3 s plus about 1 s per 3 m, up to
    20 s more) before it counts as unanswered.

## Quest people inside dungeons and the Bur
- RynthNav's go now finds people, vendors and landmarks inside dungeons. The Atlas has about 3,100 of them
  with no map position; before, a go to one said "no place called ... on the map". The way in, in order:
  1. Already in that dungeon: arrived.
  2. A portal on the map into that dungeon's landblock: walked to and into (the nearest; one that needs no
     quest first when there's a choice).
  3. A recall you know that lands there: cast. Bur Recall takes you into the Bur, and a learned Lifestone or
     Portal Recall works the same way.
- A name that matches a place on the map exactly still goes there. Someone inside wins only when their name
  is exact and the map's match is partial.
- The arrival line names the person's cell ("(Drogur is in cell 009C0270)"). The quest run then has RynthAi
  walk the dungeon's map to that cell ("questloot dungeon to <cell>"), fighting, looting and opening doors on
  the way. The talk or hand-in goes out at once and keeps looking for the person during the walk (up to
  4 min). When they are within 15 m, the walk stops so the server's own walk to them isn't fought.
- Already inside a dungeon, a talk or hand-in step first asks RynthNav which of its people is here
  ("/rnav inside a || b"). It never travels. If one is here, they are walked to. If not, the dungeon is
  patrolled as before.
