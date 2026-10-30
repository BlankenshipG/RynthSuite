# RynthAi reads Infinite Leaftide's /myquests cooldowns (Guardian rows were always "Ready")

Infinite Leaftide (ACECustom) puts a space between a /myquests line's timestamp and its quoted
description: `templeattributefocuswait - 42 solves (1792844530) "TempleAttributeFocusWait" -1 571000`.
RynthAi's quest tracker expected `)"` with no space, so the description, max solves and repeat time
went unread: every flag had no cooldown, and RynthILT's Guardian rows (which read the flags through
RynthAi.Hub) all said "Ready now" however recently a turn-in was made. The parser now allows the
space; retail ACE's format reads as before. Tests: ExpressionTests "expr: quest flags".
