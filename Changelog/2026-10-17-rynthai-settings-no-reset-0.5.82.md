# RynthAi 0.5.82 — Settings no longer reset after a bad read (DXVK on/off relaunch)

**Date:** 2026-10-17  
**Kind:** Hot push (HandWritten `0.5.81` → `0.5.82-legacy-ui`).

## Problem

Turning DXVK on or off (and relaunching the clients) sometimes came back with a character's
RynthAi settings — combat, buffs, looting, the Settings window pages — reset to defaults.

## Cause

Two things together:

1. The profile save (`SettingsProfiles\<server>\<character>\<profile>.json`) used
   `File.WriteAllText`, which empties the file first. A client closed or killed mid-save — the
   relaunch for a DXVK change, the launcher's stuck-client kill, a crash on exit — left the
   profile empty or cut off.
2. Loading swallowed the failed read: the character kept the defaults, and autosave then wrote
   those defaults over the profile, so one bad read reset it for good.

## Fix

- Saves are atomic: written to a temp file and swapped in, the previous profile kept as
  `<profile>.json.bak`.
- A profile that doesn't read loads the `.bak` instead (chat says so).
- If neither reads, the broken file is kept as `<profile>.json.unreadable-<time>` and nothing is
  written over it until a setting is actually changed (chat says so).

## Recovery

A profile already reset before this build has no backup yet; set it once more. From now on each
save keeps the previous one as `.bak`.
