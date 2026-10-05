# Third-party notices: RynthOracle

RynthOracle is based on **Oracle of Dereth**, a Decal plugin for Asheron's Call by
**Advis Eveldan** (GitHub: [advis61](https://github.com/advis61/OracleOfDereth)), with small
contributions from aunrela and porranlc (one commit each). Thank you, Advis!

- Upstream: https://github.com/advis61/OracleOfDereth
- Ported from commit `ee618c6847b98b656a47ca87cc347441d206c547` (2026-10-01, version 2.3.0).
- Licence: MIT. The upstream README states "License: MIT". The repository has no separate
  LICENSE file and names no copyright line, so the standard MIT text is reproduced below
  with the author and the years of the repository's history.

Not from upstream: `quests.aelrynth.csv` is generated from Aelrynth's own world database by
`Tools\RynthOracle.QuestPackGen`; where a quest is also in Advis's list, that list's name and notes
are kept (the pack only adds Aelrynth's timers and who sets the flag).

What came from upstream:

- The quest, augmentation, aug-gem quest, society quest, title and exploration marker lists
  in `Resources\` (`quests.csv`, `augmentations.csv`, `augquests.csv`, `society.csv`,
  `titles.csv`, `markers.csv`, `customquests.csv`), copied unchanged, except that the
  master quest list is split by its own Server column into `quests.csv` (every retail
  quest), `quests.conquest.csv` and `quests.levistras.csv` (each server's own rows, loaded
  only on that server); the lines themselves are untouched. `quests.csv` is Oracle of Dereth's curator-maintained master quest list;
  its README says the Quest Flags list "was assembled with AI assistance and may contain
  errors". The curator is the same author (every commit that updates it is Advis Eveldan's); the
  file carries no licence of its own, so it is treated as part of the MIT-licensed repository.
- The logic of the ported features (quest flag parsing and status rules, luminance costs,
  society ranks and ribbon limits, the status summary, exploration markers, Void
  damage-over-time timers, and the ConquestAC features: bank, advanced and enlightenment
  augmentations, XP bonuses and levels past 275, the fellowship list, the /top boards),
  rewritten for RynthCore. Ported files say so in their header.

---

MIT License

Copyright (c) 2024-2026 Advis Eveldan (GitHub: advis61) and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
