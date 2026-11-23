# 2026-11-14 — RynthILT: a UCM check on one client shows on every client

Silentkelpie: "if your on a ilt server and you have multi clients open in rynth and one client get a ucm
check pop up, look at sending pop up to all clients that x account is getting a ucm check".

## How it works (RynthServers: Hub/IltUcmAlert.cs)
- **What counts as a UCM check.** ACECustom's check is a server yes/no prompt (ConfirmationRequest 0x0274,
  type Yes_No). RynthILT now gets server messages (RynthPluginOnServerMessage) and treats two prompt texts
  as a UCM check, on an Infinite Leaftide world only:
  - the basic one: "Is 3 + 4 = 7? ... You have 2 minutes to respond.";
  - the advanced one: "Are you sure you're not a bot? ... You have 30 seconds to respond.".
- **On the client that gets the check:**
  - Its game window flashes in the taskbar until it is brought to the front.
  - The other clients are told the character, account (login), world, question and deadline:
    - through RynthNet's "RynthILT.UCM" channel when RynthNet is on (this reaches other PCs too);
    - through a small file under %APPDATA%\RynthCore\RynthILT\ucm, which every client on this PC reads
      once a second, RynthNet or not.
- **On every other client:** a red "UCM CHECK on another client" window shows "<character> is getting a
  UCM check!", the account and world, the question, and a countdown of the time left. It says to answer in
  that client's game window.
  - The window closes itself a few seconds after the check ends: "You passed the focus test.", a fail or
    jail line, or the time running out.
  - Dismiss closes it sooner. It comes back if the check moves on to the advanced stage.
  - It draws with RynthCore's ImGui shell on or off.
- **Nothing answers the check.** The player still has to answer it in the game.

## Tests
RynthILT.Tests UcmAlertTests cover:
- which prompts count (a crafting or fellowship prompt doesn't);
- the time to answer ("2 minutes", "1 minute, 30 seconds", "30 seconds");
- the end lines;
- the message the clients pass between them.
