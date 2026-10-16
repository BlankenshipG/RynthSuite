# RynthILT 0.2.15 — Progress Augments Refresh current fills Current

## Problem

Oracle **Progress → Augmentations → Refresh current** left every Current at 0.

## Cause

Opening Progress / clicking Refresh while the login `/aug` probe was still queued set
`_augRefreshAgain`, but the probe's `OnComplete` is not `RequestAugs`' callback, so the
queued refresh never drained. Banner-only capture finishes also dropped late `Creature:`
lines before the passive reader could take them.

## Fix

- `AfterExternalAugCapture()` drains a queued Refresh and retries `/aug` once if Current is empty.
- Longer `/aug` timeouts; banner-only keeps the passive window open; Info log `IltHub.AugLoad`.
- Slightly looser `AugLine` number group.

## Version

| Component | Version |
|---|---|
| RynthILT HandWritten | **0.2.15** (was 0.2.14) |
| ProductVersion stamp | **2026.10.16.17** |
| Launcher | **0.1.9** / ProductVersion **2026.10.16.17** (DXVK toggle + scroll fix) |