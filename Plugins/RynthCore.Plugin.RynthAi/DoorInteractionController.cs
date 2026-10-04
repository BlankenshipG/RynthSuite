// ============================================================================
//  DoorInteractionController.cs — partial class RynthAiPlugin
//
//  Detects closed doors while the macro is running and automatically opens them.
//  Optionally uses lockpicks from the Consumable Items list if the door is
//  locked (UseObject fails to open it).
//
//  State machine: None → Opening → CheckingResult → Unlocking → RetryOpen → Cooldown
//
//  Two ways in: the proximity scan (OpenDoors setting, nearest closed door in
//  range), and a requested door (RequestDoorOpen): the nav engine's recovery
//  detour asks for the closed door on its next leg. A requested door is opened
//  even with OpenDoors off, since the detour was planned through that doorway.
// ============================================================================

using System;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private int _doorTargetId;
    private long _doorActionAt;
    private int _doorAttempts;
    private bool _doorPausedNav;
    private DoorState _doorState = DoorState.None;
    private long _doorLastDiagAt;
    private int _doorRequestedId;        // asked for by the nav recovery detour; taken on the next idle tick
    private bool _doorTargetRequested;   // the current target came from a request

    // Doors we've already opened — skip for 60s so we don't re-target them
    private readonly System.Collections.Generic.Dictionary<int, long> _doorOpenedRecently = new();

    private const long DoorBusyTimeoutMs = 3000;
    private const long DoorResultSettleMs = 1200;   // a use is not judged "didn't open" sooner (below DoorBusyTimeoutMs)
    private const long DoorCooldownMs = 5000;
    private const long DoorOpenedSkipMs = 60000;
    private const int DoorMaxAttempts = 3;
    private const float YardsToMeters = 0.9144f;

    private enum DoorState
    {
        None,
        Opening,
        CheckingResult,
        Unlocking,
        RetryOpen,
        Cooldown
    }

    /// <summary>
    /// Called each tick when the macro is running.
    /// Returns true if door interaction is blocking navigation.
    /// </summary>
    private bool TickDoorInteraction()
    {
        var settings = _dashboard?.Settings;
        bool requested = _doorRequestedId != 0 || _doorTargetRequested;
        if (settings == null || !settings.IsMacroRunning || (!settings.OpenDoors && !requested))
        {
            ResetDoorState();
            _doorRequestedId = 0;
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Clean up stale entries from the recently-opened dict
        if (_doorOpenedRecently.Count > 0)
        {
            var expired = _doorOpenedRecently
                .Where(kv => now - kv.Value >= DoorOpenedSkipMs)
                .Select(kv => kv.Key).ToList();
            foreach (var key in expired)
                _doorOpenedRecently.Remove(key);
        }

        // ── Idle: scan for a nearby closed door ─────────────────────────────
        if (_doorState == DoorState.None)
        {
            int doorId = 0;
            if (_doorRequestedId != 0)
            {
                int req = _doorRequestedId;
                _doorRequestedId = 0;
                if (!IsDoorOpen(req))
                {
                    doorId = req;
                    _doorTargetRequested = true;
                }
            }
            if (doorId == 0 && settings.OpenDoors)
                doorId = FindNearestClosedDoor(settings.OpenDoorRange, now);
            if (doorId == 0) return false;

            string why = _doorTargetRequested ? " (asked for by the nav detour)" : "";
            Host.Log($"[RynthAi] Door: targeting 0x{(uint)doorId:X8}{why} (state={GetDoorStateDebug(doorId)}, busy={_busyCount})");
            _doorTargetId = doorId;
            _doorState = DoorState.Opening;
            _doorAttempts = 0;
            _doorActionAt = now;

            _navigationEngine?.Stop();
            if (Host.HasStopCompletely) Host.StopCompletely();
            _doorPausedNav = true;

            Host.UseFor(unchecked((uint)doorId), "Door",
                _doorTargetRequested ? "the nav detour asked to open this door" : $"Open Doors: closed door within {settings.OpenDoorRange:0} yd");
            return true;
        }

        // ── Timeout guard (only for action-waiting states) ────────────────
        if (_doorState is DoorState.Opening or DoorState.Unlocking or DoorState.RetryOpen
            && now - _doorActionAt > DoorBusyTimeoutMs)
        {
            // Check if the door opened while we were waiting (slow state propagation)
            if (IsDoorOpen(_doorTargetId))
            {
                Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} opened (late detect) — ready for next");
                MarkDoorOpenedAndReset(now);
                return false;
            }

            _doorAttempts++;
            if (_doorAttempts >= DoorMaxAttempts)
            {
                Host.Log($"[RynthAi] Door: max attempts ({DoorMaxAttempts}) for 0x{(uint)_doorTargetId:X8}, cooldown");
                _doorState = DoorState.Cooldown;
                _doorActionAt = now;
                return true;
            }
            Host.Log($"[RynthAi] Door: timeout in {_doorState}, retry #{_doorAttempts} for 0x{(uint)_doorTargetId:X8} (busy={_busyCount})");
            _doorState = DoorState.Opening;
            _doorActionAt = now;
            Host.UseFor(unchecked((uint)_doorTargetId), "Door", $"retry #{_doorAttempts}: the door didn't open in {DoorBusyTimeoutMs} ms");
            return true;
        }

        switch (_doorState)
        {
            case DoorState.Opening:
                if (_busyCount > 0) return true;
                if (IsDoorOpen(_doorTargetId))
                {
                    Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} opened OK — ready for next");
                    MarkDoorOpenedAndReset(now);
                    return false;
                }
                // The use is queued for AC's main thread and the server answers later: the
                // busy count was still 0 when this read "not open" ~45 ms after the use
                // (2026-10-02 logs), every time. Give the door time to swing first.
                if (now - _doorActionAt < DoorResultSettleMs) return true;
                Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} not open after use ({GetDoorStateDebug(_doorTargetId)}), checking result");
                _doorState = DoorState.CheckingResult;
                _doorActionAt = now;
                return true;

            case DoorState.CheckingResult:
                if (!settings.AutoUnlockDoors)
                {
                    Host.Log("[RynthAi] Door: not open & auto-unlock off, cooldown");
                    _doorState = DoorState.Cooldown;
                    _doorActionAt = now;
                    return true;
                }
                var lockpick = FindLockpickInConsumables();
                if (lockpick == null)
                {
                    Host.Log("[RynthAi] Door: locked but no lockpick in consumables, cooldown");
                    _doorState = DoorState.Cooldown;
                    _doorActionAt = now;
                    return true;
                }
                Host.Log($"[RynthAi] Door: using lockpick {lockpick.Name} on 0x{(uint)_doorTargetId:X8}");
                _doorState = DoorState.Unlocking;
                _doorActionAt = now;
                Host.UseOnFor(unchecked((uint)lockpick.Id), unchecked((uint)_doorTargetId), "Door", "Auto Unlock Doors: lockpick on a door that didn't open");
                return true;

            case DoorState.Unlocking:
                if (_busyCount > 0) return true;
                Host.Log($"[RynthAi] Door: unlock done, retrying open 0x{(uint)_doorTargetId:X8}");
                _doorState = DoorState.RetryOpen;
                _doorActionAt = now;
                Host.UseFor(unchecked((uint)_doorTargetId), "Door", "open again after the lockpick");
                return true;

            case DoorState.RetryOpen:
                if (_busyCount > 0) return true;
                if (IsDoorOpen(_doorTargetId))
                {
                    Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} unlocked & opened OK — ready for next");
                    MarkDoorOpenedAndReset(now);
                    return false;
                }
                if (now - _doorActionAt < DoorResultSettleMs) return true;
                _doorAttempts++;
                if (_doorAttempts >= DoorMaxAttempts)
                {
                    Host.Log($"[RynthAi] Door: max attempts after unlock for 0x{(uint)_doorTargetId:X8}, cooldown");
                    _doorState = DoorState.Cooldown;
                    _doorActionAt = now;
                }
                else
                {
                    _doorState = DoorState.Opening;
                    _doorActionAt = now;
                    Host.UseFor(unchecked((uint)_doorTargetId), "Door", $"still closed after the lockpick, try {_doorAttempts + 1}");
                }
                return true;

            case DoorState.Cooldown:
                if (now - _doorActionAt >= DoorCooldownMs)
                {
                    Host.Log($"[RynthAi] Door: cooldown done for 0x{(uint)_doorTargetId:X8}, resetting");
                    ResetDoorState();
                }
                return _doorState != DoorState.None;
        }

        return false;
    }

    private void MarkDoorOpenedAndReset(long now)
    {
        _doorOpenedRecently[_doorTargetId] = now;
        ResetDoorState();
    }

    private void ResetDoorState()
    {
        _doorTargetId = 0;
        _doorState = DoorState.None;
        _doorAttempts = 0;
        _doorPausedNav = false;
        _doorTargetRequested = false;
    }

    /// <summary>
    /// Nav recovery: open this door on the next tick, whether or not OpenDoors is on.
    /// Ignored while another door is being handled (the detour asks again later).
    /// </summary>
    private void RequestDoorOpen(int doorId)
    {
        if (doorId != 0 && _doorState == DoorState.None) _doorRequestedId = doorId;
    }

    /// <summary>
    /// Nav recovery: the closed door nearest the start of the leg (ax, ay) to (bx, by), in
    /// world units, within <paramref name="corridor"/> of it and about the same height as
    /// <paramref name="z"/>; 0 when there is none. Doors are static objects with BF_DOOR
    /// (or a door-like name when the bitfield isn't available).
    /// </summary>
    private int FindClosedDoorOnLeg(double ax, double ay, double bx, double by, double z, double corridor)
    {
        if (_objectCache == null || !Host.HasGetObjectPosition) return 0;
        bool hasBitfield = Host.HasGetObjectBitfield;
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;

        int bestId = 0;
        double bestT = double.MaxValue;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (uid >= 0x80000000u) continue;
            if (hasBitfield)
            {
                if (!Host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0) continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis")) continue;
            }
            if (IsDoorOpen(wo.Id)) continue;
            if (!Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float oz)) continue;
            if (Math.Abs(oz - z) > 4.0) continue;

            double px = ((cell >> 24) & 0xFF) * 192.0 + x;
            double py = ((cell >> 16) & 0xFF) * 192.0 + y;
            double t = len2 < 1e-6 ? 0.0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0.0, 1.0);
            double cx = ax + dx * t - px, cy = ay + dy * t - py;
            if (cx * cx + cy * cy > corridor * corridor) continue;
            if (t < bestT) { bestT = t; bestId = wo.Id; }
        }
        return bestId;
    }

    private int FindNearestClosedDoor(float rangeYards, long now)
    {
        if (_objectCache == null || _playerId == 0) return 0;

        float rangeMeters = rangeYards * YardsToMeters;
        int bestId = 0;
        double bestDist = double.MaxValue;
        bool hasBitfield = Host.HasGetObjectBitfield;

        int totalLandscape = 0, doorsFound = 0, openSkipped = 0, recentSkipped = 0;

        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            totalLandscape++;

            // Doors have static GUIDs (< 0x80000000)
            if (uid >= 0x80000000u) continue;

            // Primary: BF_DOOR bitfield check
            if (hasBitfield)
            {
                if (!Host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0)
                    continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis"))
                    continue;
            }

            doorsFound++;

            // Skip doors we recently opened
            if (_doorOpenedRecently.TryGetValue(wo.Id, out long openedAt) && (now - openedAt) < DoorOpenedSkipMs)
            {
                recentSkipped++;
                continue;
            }

            // Skip already-open doors
            if (IsDoorOpen(wo.Id))
            {
                openSkipped++;
                continue;
            }

            double dist = _objectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist > rangeMeters || dist >= bestDist) continue;

            bestDist = dist;
            bestId = wo.Id;
        }

        // Periodic diagnostic when doors are nearby but none targeted
        if (bestId == 0 && doorsFound > 0 && (now - _doorLastDiagAt) >= 5000)
        {
            _doorLastDiagAt = now;
            Host.Log($"[RynthAi] Door scan: {totalLandscape} landscape, {doorsFound} doors, {openSkipped} open, {recentSkipped} recent-skip, range={rangeMeters:F1}m");
        }

        return bestId;
    }

    private bool IsDoorOpen(int doorId)
    {
        uint uid = unchecked((uint)doorId);

        // Primary: read CPhysicsObj::m_state directly — ETHEREAL_PS (0x4) = open
        if (Host.HasGetObjectState && Host.TryGetObjectState(uid, out uint physState))
            return (physState & 0x4u) != 0;

        // Fallback: DoMotionHooks tracks MotionOn/MotionOff per object
        if (Host.HasGetObjectMotionOn && Host.TryGetObjectMotionOn(uid, out bool isOn))
            return isOn;

        return false;
    }

    private string GetDoorStateDebug(int doorId)
    {
        uint uid = unchecked((uint)doorId);
        if (Host.HasGetObjectState && Host.TryGetObjectState(uid, out uint ps))
            return $"phys=0x{ps:X8} ethereal={(ps & 0x4u) != 0}";
        if (Host.HasGetObjectMotionOn && Host.TryGetObjectMotionOn(uid, out bool on))
            return $"motionOn={on}";
        return "no-state";
    }

    private ConsumableRule? FindLockpickInConsumables()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null) return null;

        return settings.ConsumableRules
            .Where(r => r.Type.Equals("Lockpick", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(r => _objectCache[r.Id] != null);
    }
}
