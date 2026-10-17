# RynthILT 0.2.12 - Bank balances: no chat spam

Previous: 0.2.11. ProductVersion: 2026.10.16.1 (previous live 2026.10.15.8).

## Why

Silentstorm was getting a full `[BANK] Your balances are:` dump in chat every
120 seconds while Mini Remote was open. RynthILT's AutoRefresh (default 120)
re-issued quiet `/bank` refreshes; those lines still reached chat.

## What

- Default `AutoRefreshSeconds` is **0** (login probe + manual Refresh only).
- `[BANK]` balance lines are eaten unless the player typed `/bank` / bare `/b`,
  or asked for a loud refresh (`RequestRefresh(quiet: false)`).
- Existing hub.json files that still had 120 were zeroed on this deploy for
  local chars (Silentstorm and any other with AutoRefresh &gt; 0).

## Files

- `Hub/IltBanking.cs`, `Hub/IltHubState.cs`, `RynthILTPlugin.cs`

## Deploy

`dotnet publish` RynthILT `-p:Version=2026.10.16.1` → `C:\Games\RynthSuite\RynthILT\`
(reload plugins).
