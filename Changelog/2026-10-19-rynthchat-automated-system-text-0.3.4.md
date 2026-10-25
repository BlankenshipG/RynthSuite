# RynthChat 0.3.4: automated system text marked for the System tab (2026-10-19)

ProductVersion 2026.10.18.30. Previous release: 0.3.3 (its DLL was stamped 1.0.0). Needs RynthCore
engine 2026.10.18.30 for the routing; an older engine ignores the new field.

## Why

The login messages, the `/myquests` and `/qb list` replies, and the other automated replies
filled RynthChat's All tab. They should show only in the System tab.

## Changed

- Each scrollback line can carry `"auto":true` (`ChatLine.Auto`). The engine shows those lines in
  the System tab only. RynthChat marks:
  - **`/myquests` and `/qb list` lines**, on system chat types only: flag lines
    (`key - N solves (...)`), "Quest list is empty", `[MyQuests]`, the `/qb` headers and count,
    "Quest bonus list is empty", `[QB]`, and ACE's "Welcome to Asheron's Call".
  - **The login burst:** system text (not an admin's tell) from entering the world until 30 s
    after login completes.
  - **Lines a plugin ate:** the new export `RynthChatNoteEaten`, which the engine calls after
    dispatch. The newest line is marked when its text matches.
- Player speech and channels are never marked, even when they quote the same words.

## Tests

`tools/ChatHistoryBench` in RynthCore compiles this plugin's buffer and classifier. Its checks
cover the classifier, the login burst, admin tells, and marking an eaten line.
