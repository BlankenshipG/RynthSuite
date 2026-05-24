# RynthBundle 0.4.9 — framework-dependent packaging

RynthCore installer packaging now publishes launcher and Suite tools as framework-dependent apps.

- `RynthCore.exe` (x86) requires **.NET Desktop Runtime 10 x86**
- `RynthCore.LootEditor` / `RynthCore.MonsterEditor` (x64) require **.NET Desktop Runtime 10 x64**
- Installer does not embed runtime downloads; it includes a Start Menu link and `RynthCore-Prerequisites.txt` with the official .NET runtime URL.
