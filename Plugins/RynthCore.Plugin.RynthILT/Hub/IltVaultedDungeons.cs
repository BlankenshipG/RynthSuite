// IltVaultedDungeons.cs — Mini Remote "Vaulted Dungeons" tab (ILT / ACECustom only).
// Written by Silentkelpie (RynthSuite PR #12), a port of the UtilityBelt VaultedDungeons.lua script
// (owner 2026-09-27 / 2026-10-03): keeps the character in one Vaulted Dungeon via a tied Portal
// Recall Gem, and optionally /rc quit after N minutes without a kill.
//
// Safety rules (the owner's, added when it was taken in):
//   * ILT only. The module does nothing (no tick work, no recall, no quit, no tab) unless
//     IltServerOptions.IsIltLikeWorld says this is an Infinite Leaftide / ACECustom world, the same
//     verdict every other ILT feature follows. A command answers "not available on this server".
//   * Master switch, off by default: RynthAi's "Vaulted Dungeons" setting (Settings > Navigation),
//     read through RynthAi.Hub VaultedDungeonsEnabled. It is the only copy. While it is off the module
//     ignores AutoStart, never ticks, sends no recall and no /rc quit, and the tab is hidden. Turning
//     it off while running stops the keeper at once and says so once.
//   * Active is never resumed from the saved settings. It starts when the player turns it on, or on
//     arriving in the chosen dungeon with Auto Start On Arrival on (master on); a relog starts Off.
//   * No recall gem while RynthAi is in combat, buffing or waiting on the server, or the client is
//     busy: the recall waits until it is clear (a warning on the normal 30 s cadence).
//   * /rc quit goes out at most once per activation, and only if Active, ILT and the master are all
//     still true at the moment of sending.
//   * The no-kill timer restarts on the luminance line, on the server's killer notification (a
//     killing blow of yours) and on a pet-capture success reply. Kills by pets or fellows that pay
//     no luminance still don't count; the tab says so.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One known Vaulted Dungeon (landblock high word; the client does not see the layer).</summary>
internal readonly record struct VaultedDungeon(string Name, uint Landblock);

internal sealed class IltVaultedDungeons : IIltFeature
{
    // Keep order stable: saved picks are by 1-based index into this list.
    private static readonly VaultedDungeon[] Dungeons =
    {
        new("Tyrant's Quarry",  0x01F7),
        new("Tusgian Barracks", 0x8C03),
        new("Wailing Cells",    0x01C2),
        new("Chamber of Idols", 0x5649),
        new("Fiun Redoubt",     0x0178),   // added 2026-09-27
        new("Tumerok Tunnels",  0x5754),   // added 2026-10-03
    };

    private static readonly string[] GemNames =
    {
        "Primary Portal Recall Gem",
        "Secondary Portal Recall Gem",
    };

    private static readonly (string Name, float Value)[] Powers =
    {
        ("Fast", 0f), ("Medium", 0.5f), ("Full Power", 1f),
    };

    private static readonly int[] QuitPresets = { 1, 2, 5, 10, 15, 30 };

    private const long FirstRecallAfterMs = 1000;
    private const long RecallAfterMs = 5000;
    private const int QuitMin = 1, QuitMax = 30;
    private const long WarnEveryMs = 30_000;
    private const string SettingsFileName = "settings.json";
    private const string SettingsBadFileName = "settings.unreadable.json";

    private const string CmdMetaOff = "/vt opt set EnableMeta False";
    private const string CmdNavOff = "/vt opt set EnableNav False";
    private const string CmdRepeatOn = "/ub playeroption AutoRepeatAttack on";
    private const string CmdAttackBar = "/vt setattackbar ";
    private const string CmdQuit = "/rc quit";

    // System line only (no player sender): "You've banked 1,234 Luminance."
    private static readonly Regex KillLine = new(
        @"^You've banked [\d,]+ Luminance\.\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Why the keeper is idle (<see cref="Snapshot.Gate"/>): "" = it may run.</summary>
    private const string GateWorld = "world", GateMaster = "master";

    private readonly IltHubContext _ctx;
    private readonly object _gate = new();
    private Cfg _cfg = new();
    private bool _loaded;
    private bool _dirty;
    private string? _lastGoodJson;

    // Seams (the tests fake them): the master switch, what RynthAi is doing, the clock, the gem use.
    internal Func<bool> MasterEnabled;
    internal Func<RynthAiActivity?> Activity;
    internal Func<long> Clock = () => IltHubContext.NowMs;
    /// <summary>Uses the named gem from the pack; false when the pack has none.</summary>
    internal Func<string, bool> UseGem;

    // Runtime (pump thread).
    private long _outSinceMs;
    private long _lastRecallMs;
    private long _clockStartMs;
    private long _realKillMs;
    private bool _wasInside;
    private bool _quitSent;
    private long _lastWarnMs = -WarnEveryMs;
    private string _publishedGate = "?";
    private string _state = "paused";   // paused / setup / in / out / portal / nogem
    private long? _quitLeftMs;
    private bool? _presentBefore;
    private long _idleSecs;
    private long _inSinceMs;
    private long _portalSinceMs;
    private int _kills;
    private long _dungeonSecs;
    private long _lastTickMs;

    // Snapshot for the render thread (copied under _gate).
    private Snapshot _snap = Snapshot.Empty;

    public IltVaultedDungeons(IltHubContext ctx)
    {
        _ctx = ctx;
        MasterEnabled = () => _ctx.Ai.VaultedDungeonsEnabled;
        Activity = () => _ctx.Ai.Activity();
        UseGem = gemName =>
        {
            var gem = _ctx.Inventory.FindByName(n => n.Equals(gemName, StringComparison.OrdinalIgnoreCase));
            if (gem == null) return false;
            _ctx.Inventory.Use(gem);
            return true;
        };
    }

    /// <summary>Whether the keeper is running (tests, status).</summary>
    internal bool IsActive => _cfg.Active;

    private long Now() => Clock();

    /// <summary>
    /// The two hard rules, asked fresh each time: an ILT-like world, and the master switch on.
    /// <paramref name="gate"/> says which one failed ("" when allowed).
    /// </summary>
    private bool Allowed(out string gate)
    {
        gate = !_ctx.Options.IsIltLikeWorld ? GateWorld : !MasterEnabled() ? GateMaster : string.Empty;
        return gate.Length == 0;
    }

    /// <summary>Same, for commands and the tab: why the keeper can't be used now (null = it can).</summary>
    private static string? GateReply(string gate) => gate switch
    {
        GateWorld => "Not available on this server (Infinite Leaftide / ACECustom only).",
        GateMaster => "Off: turn on \"Vaulted Dungeons\" in Settings > Navigation first (the master switch, off by default).",
        _ => null,
    };

    // ── Persistence ─────────────────────────────────────────────────────────

    private string SettingsDir => Path.Combine(_ctx.Store.CharFolder, "VaultedDungeons");
    private string SettingsPath => Path.Combine(SettingsDir, SettingsFileName);

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize(text, IltVaultedDungeonsJsonContext.Default.Cfg);
            if (cfg == null) return;
            _cfg = Sanitize(cfg);
            _lastGoodJson = text;
        }
        catch (Exception ex)
        {
            RynthLog.Write(LogCat.IltHub, $"Vaulted Dungeons: settings load failed: {ex.Message}");
        }
    }

    private void SaveIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string path = SettingsPath;
            // Corrupt / mid-write: keep a copy, then rewrite from last good + current cfg.
            if (File.Exists(path))
            {
                try
                {
                    string existing = File.ReadAllText(path);
                    if (_lastGoodJson == null)
                        JsonSerializer.Deserialize(existing, IltVaultedDungeonsJsonContext.Default.Cfg); // throws if bad
                }
                catch
                {
                    try
                    {
                        File.Copy(path, Path.Combine(SettingsDir, SettingsBadFileName), overwrite: true);
                        _ctx.Chat("[Vaulted Dungeons] settings.json would not read — kept a copy as "
                                  + SettingsBadFileName + ".");
                    }
                    catch { /* best effort */ }
                }
            }
            string json = JsonSerializer.Serialize(_cfg, IltVaultedDungeonsJsonContext.Default.Cfg);
            File.WriteAllText(path, json);
            _lastGoodJson = json;
        }
        catch (Exception ex)
        {
            RynthLog.Write(LogCat.IltHub, $"Vaulted Dungeons: settings save failed: {ex.Message}");
        }
    }

    private static Cfg Sanitize(Cfg c)
    {
        // Never resumed from disk: the file keeps its "active" field, but a relog (or turning the
        // master switch back on) starts Off. Auto Start On Arrival is the only way back in by itself.
        c.Active = false;
        c.Dungeon = ClampInt(c.Dungeon, 0, Dungeons.Length, 0);
        c.Gem = ClampInt(c.Gem, 0, GemNames.Length, 0);
        c.QuitMinutes = ClampInt(c.QuitMinutes, QuitMin, QuitMax, 2);
        c.AttackPower = Powers[PowerIndex(c.AttackPower)].Value;
        return c;
    }

    private static int ClampInt(int v, int lo, int hi, int def)
        => v < lo || v > hi ? def : v;

    private static int PowerIndex(float v)
    {
        int best = 0;
        float bestGap = float.MaxValue;
        for (int i = 0; i < Powers.Length; i++)
        {
            float gap = Math.Abs(Powers[i].Value - v);
            if (gap < bestGap) { best = i; bestGap = gap; }
        }
        return best;
    }

    private static string PowerText(float p)
    {
        string t = p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return t;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        // The hard rules first: ILT only, master switch on. Nothing below runs otherwise (not even
        // reading the settings file), and a keeper that was running is stopped on the spot.
        if (!Allowed(out string gate))
        {
            StopForGate(gate);
            PublishGate(gate);
            return;
        }

        EnsureLoaded();
        SaveIfDirty();

        // Time in dungeon (kills/hour): only while last state was "in".
        if (_lastTickMs > 0 && _state == "in")
            _dungeonSecs += Math.Max(0, (nowMs - _lastTickMs) / 1000);
        _lastTickMs = nowMs;

        if (!_ctx.Host.HasGetPlayerPose || !_ctx.Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) || cell == 0)
        {
            Publish();
            return;
        }

        uint landblock = cell >> 16;
        bool portaling = _ctx.Host.HasIsPortaling && _ctx.Host.IsPortaling();
        VaultedDungeon? dungeon = _cfg.Dungeon > 0 && _cfg.Dungeon <= Dungeons.Length
            ? Dungeons[_cfg.Dungeon - 1]
            : null;

        // Present check (for Auto Start / arrive actions) even while Off.
        if (dungeon == null)
            _presentBefore = null;
        else if (!portaling)
        {
            bool here = landblock == dungeon.Value.Landblock;
            if (here && _presentBefore != true) OnArrive(dungeon.Value, nowMs);
            _presentBefore = here;
        }

        _quitLeftMs = null;
        if (!_cfg.Active)
        {
            _state = "paused";
            _outSinceMs = 0;
            Publish();
            return;
        }

        string? gemName = _cfg.Gem > 0 && _cfg.Gem <= GemNames.Length ? GemNames[_cfg.Gem - 1] : null;
        if (dungeon == null || gemName == null)
        {
            _state = "setup";
            _wasInside = false;
            Warn(nowMs, dungeon == null
                ? "Pick a dungeon in the Vaulted Dungeons tab."
                : "Pick a recall gem in the Vaulted Dungeons tab.");
            Publish();
            return;
        }

        bool inside = landblock == dungeon.Value.Landblock;
        if (inside && !portaling)
        {
            if (!_wasInside)
            {
                _clockStartMs = nowMs;
                _inSinceMs = nowMs;
            }
            _wasInside = true;
            _outSinceMs = 0;
            _portalSinceMs = 0;
            _state = "in";
            _idleSecs = Math.Max(0, (nowMs - _clockStartMs) / 1000);
            if (_cfg.Quit)
            {
                long quitAfterMs = _cfg.QuitMinutes * 60_000L;
                _quitLeftMs = Math.Max(0, quitAfterMs - (nowMs - _clockStartMs));
                if (nowMs - _clockStartMs >= quitAfterMs && !_quitSent)
                {
                    // Once per activation, and only if ILT, the master switch and Active are all
                    // still true right now (asked again: they may have changed since the top of the tick).
                    string nowGate = string.Empty;
                    if (!_cfg.Active || !Allowed(out nowGate))
                    {
                        StopForGate(nowGate);
                        PublishGate(nowGate);
                        return;
                    }
                    _quitSent = true;
                    _ctx.Chat($"[Vaulted Dungeons] No kill in {_cfg.QuitMinutes} minute"
                              + (_cfg.QuitMinutes == 1 ? "" : "s") + " — quitting.");
                    Invoke(CmdQuit);
                }
            }
            Publish();
            return;
        }

        _wasInside = false;
        if (portaling)
        {
            _state = "portal";
            _outSinceMs = 0;
            if (_portalSinceMs == 0) _portalSinceMs = nowMs;
            Publish();
            return;
        }
        _portalSinceMs = 0;

        if (_outSinceMs == 0) _outSinceMs = nowMs;
        _state = "out";

        bool tried = _lastRecallMs >= _outSinceMs;
        bool due = (!tried && nowMs - _outSinceMs >= FirstRecallAfterMs)
                   || (tried && nowMs - _lastRecallMs >= RecallAfterMs);
        if (due)
        {
            // Not in the middle of a fight, a buff chain or an unanswered use/cast: the recall waits
            // (still due, so it goes out on the first clear tick) and says so on the normal cadence.
            string? busy = BusyReason();
            if (busy != null)
            {
                _state = "busy";
                Warn(nowMs, $"Waiting to use the {gemName}: {busy}.");
            }
            else if (UseGem(gemName))
            {
                _lastRecallMs = nowMs;
                _ctx.Chat($"[Vaulted Dungeons] Not in {dungeon.Value.Name} — using the {gemName}.");
            }
            else
            {
                _state = "nogem";
                Warn(nowMs, $"No {gemName} in your pack.");
            }
        }
        Publish();
    }

    /// <summary>
    /// Why a recall gem must not be used now, or null when it may. RynthAi's activity (its "RynthAi.Activity"
    /// bits: combat, buffing, loot, salvage, crafting, a use/cast waiting on the server) and the client's
    /// busy hourglass; RynthAi not answering counts as busy (nothing is known, so nothing is used).
    /// </summary>
    private string? BusyReason()
    {
        RynthAiActivity? a = Activity();
        if (a is null) return "RynthAi is not ready";
        RynthAiActivity f = a.Value;
        if ((f & RynthAiActivity.Combat) != 0) return "RynthAi is in combat";
        if ((f & RynthAiActivity.Buffing) != 0) return "RynthAi is buffing";
        if ((f & RynthAiActivity.ServerAction) != 0) return "RynthAi is waiting on the server";
        if ((f & RynthAiActivity.Looting) != 0) return "RynthAi is looting";
        if ((f & RynthAiActivity.Salvaging) != 0) return "RynthAi is salvaging";
        if ((f & RynthAiActivity.Crafting) != 0) return "RynthAi is crafting";
        if (_ctx.Inventory.IsBusy) return "the client is busy";
        return null;
    }

    /// <summary>
    /// A rule failed (not ILT any more, or the master switch went off): a running keeper stops at once
    /// and says so once; an idle one has nothing to do. Safe to call every tick.
    /// </summary>
    private void StopForGate(string gate)
    {
        _outSinceMs = 0;
        _wasInside = false;
        _quitLeftMs = null;
        _presentBefore = null;
        if (!_cfg.Active) return;
        _cfg.Active = false;
        _dirty = true;
        _state = "paused";
        _ctx.Chat("[Vaulted Dungeons] Stopped: " + (gate == GateWorld
            ? "this is not an Infinite Leaftide / ACECustom world."
            : "\"Vaulted Dungeons\" was turned off in Settings > Navigation."));
        SaveIfDirty();
    }

    /// <summary>The idle snapshot (only when the reason changes: nothing else runs while gated).</summary>
    private void PublishGate(string gate)
    {
        if (gate == _publishedGate) return;
        _publishedGate = gate;
        Publish(gate);
    }

    public bool OnChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string line = text.TrimEnd();
        bool luminance = KillLine.IsMatch(line);
        // A pet-capture success counts as a kill for the timer too (the same replies RynthAi's Pets
        // tab matches); nothing else is guessed.
        if (!luminance && !PetCaptureLines.IsSuccess(line)) return false;
        if (!_cfg.Active || !Allowed(out _)) return false;
        NoteKill(Now(), countKill: luminance);
        return false; // never eat the luminance line
    }

    /// <summary>
    /// The server's killer notification: this character landed a killing blow (RynthILTPlugin
    /// .OnKillNotification, pump thread). Restarts the no-kill timer, like the luminance line.
    /// </summary>
    public void OnKillNotification()
    {
        if (!_cfg.Active || !Allowed(out _)) return;
        NoteKill(Now(), countKill: false);
    }

    /// <summary>Restarts the Kill Client timer. The kill counter moves only on the luminance line (one per kill).</summary>
    private void NoteKill(long now, bool countKill)
    {
        _clockStartMs = now;
        _realKillMs = now;
        if (countKill && _state == "in") _kills++;
        Publish();
    }

    public void OnLogout()
    {
        // Logging out ends the run: Active is never carried over (see Sanitize).
        _cfg.Active = false;
        SaveIfDirty();
        _wasInside = false;
        _outSinceMs = 0;
        _presentBefore = null;
        _state = "paused";
        Publish();
    }

    // ── Arrive / commands ───────────────────────────────────────────────────

    private void OnArrive(VaultedDungeon dungeon, long nowMs)
    {
        // Only called while ILT and the master switch are on (Tick's gate), so Auto Start is honoured here only.
        if (_cfg.AutoStart && !_cfg.Active)
        {
            Activate(nowMs);
            _ctx.Chat($"[Vaulted Dungeons] Arrived in {dungeon.Name} — switched On (Auto Start On Arrival).");
        }
        // The arrival settings are part of running the keeper: nothing is sent while it is Off.
        if (!_cfg.Active) return;
        if (_cfg.DisableMeta) Invoke(CmdMetaOff);
        if (_cfg.DisableNav) Invoke(CmdNavOff);
        if (_cfg.RepeatAttack) Invoke(CmdRepeatOn);
        if (_cfg.SetPower) Invoke(CmdAttackBar + PowerText(_cfg.AttackPower));
    }

    private void Invoke(string cmd)
    {
        if (_ctx.Host.HasInvokeChatParser) _ctx.Host.InvokeChatParser(cmd);
    }

    private void Warn(long nowMs, string text)
    {
        if (nowMs - _lastWarnMs < WarnEveryMs) return;
        _lastWarnMs = nowMs;
        _ctx.Chat("[Vaulted Dungeons] " + text);
    }

    /// <summary>Pump-thread: /ilt vaulted [on|off|toggle|status].</summary>
    public bool HandleCommand(string[] args)
    {
        // ILT only, master switch on: otherwise the only answer is why (the keeper itself stays idle).
        if (!Allowed(out string gate))
        {
            if (_cfg.Active) StopForGate(gate);
            _ctx.Chat("[Vaulted Dungeons] " + GateReply(gate));
            return true;
        }

        EnsureLoaded();
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "on":
            case "start":
                SetActive(true);
                return true;
            case "off":
            case "stop":
                SetActive(false);
                _ctx.Chat("[Vaulted Dungeons] Off.");
                return true;
            case "toggle":
                SetActive(!_cfg.Active);
                if (!_cfg.Active) _ctx.Chat("[Vaulted Dungeons] Off.");
                return true;
            default:
                string d = _cfg.Dungeon > 0 && _cfg.Dungeon <= Dungeons.Length
                    ? Dungeons[_cfg.Dungeon - 1].Name : "(none)";
                string g = _cfg.Gem > 0 && _cfg.Gem <= GemNames.Length
                    ? GemNames[_cfg.Gem - 1] : "(none)";
                _ctx.Chat($"[Vaulted Dungeons] {(_cfg.Active ? "On" : "Off")} — dungeon={d}, gem={g}, "
                          + $"kill-client={(_cfg.Quit ? $"{_cfg.QuitMinutes}m" : "off")}, state={_state}.");
                _ctx.Chat("[Vaulted Dungeons] /ilt vaulted [on|off|toggle|status]  (Mini Remote tab; master switch: Settings > Navigation > Vaulted Dungeons)");
                return true;
        }
    }

    /// <summary>Starts the keeper (the player's own action, or Auto Start On Arrival). Pump thread; ILT and master already checked.</summary>
    private void Activate(long nowMs)
    {
        _cfg.Active = true;
        _dirty = true;
        _outSinceMs = 0;
        _wasInside = false;       // the no-kill clock starts when the first in-dungeon tick sees us inside
        _clockStartMs = nowMs;
        _quitSent = false;        // /rc quit: once per activation
        _lastRecallMs = 0;
        Publish();
    }

    private void SetActive(bool value)
    {
        if (value)
        {
            // From the tab's button too (posted to the pump): re-check, the switch may have gone off since.
            if (!Allowed(out string gate))
            {
                _ctx.Chat("[Vaulted Dungeons] " + GateReply(gate));
                return;
            }
            EnsureLoaded();
            if (!_cfg.Active)
            {
                Activate(Now());
                _ctx.Chat("[Vaulted Dungeons] On.");
            }
            return;
        }
        _cfg.Active = false;
        _outSinceMs = 0;
        _dirty = true;
        _clockStartMs = Now();
        _wasInside = false;
        _quitLeftMs = null;
        _state = "paused";
        Publish();
    }

    // ── Render snapshot ─────────────────────────────────────────────────────

    private void Publish(string gate = "")
    {
        _publishedGate = gate;
        lock (_gate)
        {
            _snap = new Snapshot(
                _cfg.Active, _cfg.Dungeon, _cfg.Gem, _cfg.Quit, _cfg.QuitMinutes,
                _cfg.ShowInDungeon, _cfg.AutoStart, _cfg.DisableMeta, _cfg.DisableNav,
                _cfg.RepeatAttack, _cfg.SetPower, _cfg.AttackPower,
                _state, _quitLeftMs, _idleSecs, _inSinceMs, _outSinceMs, _portalSinceMs,
                _realKillMs, _kills, _dungeonSecs, Now(), gate);
        }
    }

    private Snapshot Take()
    {
        lock (_gate) return _snap;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    private bool _showAdvanced;
    // Pending edits from the UI, applied on the next pump tick via Post.
    private void PostEdit(Action<Cfg> edit)
    {
        _ctx.Post(() =>
        {
            if (!Allowed(out _)) return;   // gated while it waited: an edit changes nothing
            EnsureLoaded();
            edit(_cfg);
            _dirty = true;
            Publish();
        });
    }

    public void Render()
    {
        // Settings load on the pump (Tick); the window only reads the snapshot.
        Snapshot s = Take();

        Vector4 colLabel = new(0.53f, 0.53f, 0.53f, 1f);
        Vector4 colValue = new(1f, 1f, 1f, 1f);
        Vector4 colGreen = new(0.27f, 0.80f, 0.40f, 1f);
        Vector4 colAmber = new(0.95f, 0.65f, 0.20f, 1f);
        Vector4 colRed = new(0.90f, 0.30f, 0.30f, 1f);
        Vector4 colPurple = new(0.72f, 0.52f, 0.95f, 1f);
        Vector4 colTitleRed = new(0.85f, 0.25f, 0.25f, 1f);

        // Title row: Vaulted (red) Dungeons (white) + On/Off + Advanced.
        ImGui.TextColored(colTitleRed, "Vaulted");
        ImGui.SameLine(0, ImGui.CalcTextSize(" ").X);
        ImGui.TextColored(colValue, "Dungeons");

        // Not offered right now (the tab is hidden then; this covers a stale frame or a direct open).
        if (s.Gate.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped(GateReply(s.Gate) ?? "Not available.");
            return;
        }
        ImGui.SameLine(0, 16);
        Vector4 pill = s.Active ? colGreen : new(0.85f, 0.10f, 0.10f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, pill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, pill);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, pill);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.05f, 0.05f, 0.05f, 1f));
        if (ImGui.SmallButton((s.Active ? " On " : " Off ") + "##vdactive"))
            _ctx.Post(() => SetActive(!s.Active));
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(s.Active
                ? "On: keeping this character in the chosen dungeon.\nClick: turn off."
                : "Off: doing nothing.\nClick: turn on.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Advanced##vdoptions")) _showAdvanced = !_showAdvanced;

        ImGui.Separator();

        float labelW = ImGui.CalcTextSize("Recall Gem").X + 12;
        void Label(string text)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(colLabel, text);
            ImGui.SameLine(labelW);
        }

        // Status
        var (statusWord, statusCol) = s.State switch
        {
            "in" => ("In Dungeon", colGreen),
            "out" => ("Recalling", colAmber),
            "portal" => ("Portal Space", colPurple),
            "setup" => ("Setup Needed", colAmber),
            "nogem" => ("No Gem", colRed),
            "busy" => ("Waiting (busy)", colAmber),
            _ => ("Not Running", colRed),
        };
        long since = s.State == "in" ? s.InSinceMs
            : s.State == "out" ? s.OutSinceMs
            : s.State == "portal" ? s.PortalSinceMs : 0;
        string statusText = statusWord;
        if (since > 0)
        {
            long secs = Math.Max(0, (s.NowMs - since) / 1000);
            long h = secs / 3600, m = (secs / 60) % 60, sec = secs % 60;
            statusText += h > 0 ? $" ({h}:{m:D2}:{sec:D2})" : $" ({m}:{sec:D2})";
        }
        Label("Status");
        ImGui.TextColored(statusCol, statusText);

        // Dungeon combo (0 = pick, 1..N = Dungeons[i-1])
        Label("Dungeon");
        string[] dungeonChoices = BuildDungeonChoices();
        int dungeonIdx = s.Dungeon;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("##vddungeon", ref dungeonIdx, dungeonChoices, dungeonChoices.Length))
            PostEdit(c => { c.Dungeon = dungeonIdx; });

        Label("Recall Gem");
        string[] gemChoices = { "Pick a gem", GemNames[0], GemNames[1] };
        int gemIdx = s.Gem;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("##vdgem", ref gemIdx, gemChoices, gemChoices.Length))
            PostEdit(c => { c.Gem = gemIdx; });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tie this gem to the dungeon's portal first.");

        Label("Last Kill");
        if (s.State == "in" && s.RealKillMs > 0)
            ImGui.TextUnformatted($"{(s.NowMs - s.RealKillMs) / 1000} s ago");
        else
            ImGui.TextUnformatted("-");

        Label("Session");
        string perHour = s.DungeonSecs < 60 ? "-"
            : Commas((long)(s.Kills * 3600.0 / Math.Max(1, s.DungeonSecs)));
        ImGui.TextUnformatted($"{Commas(s.Kills)} kills ({perHour} Per Hour)");

        if (s.QuitLeftMs is long left && s.QuitMinutes > 0)
        {
            float frac = (float)(left / (double)(s.QuitMinutes * 60_000L));
            string barLabel = $"Kill Client ({left / 60_000}:{(left / 1000) % 60:D2})";
            ImGui.ProgressBar(Math.Clamp(frac, 0f, 1f), new Vector2(-1, ImGui.GetTextLineHeight()), barLabel);
        }

        if (_showAdvanced)
        {
            ImGui.Separator();
            ImGui.TextColored(colValue, "Advanced");

            bool quit = s.Quit;
            if (ImGui.Checkbox("Kill Client##vdquit", ref quit))
            {
                bool on = quit;
                _ctx.Post(() =>
                {
                    if (!Allowed(out _)) return;
                    EnsureLoaded();
                    _cfg.Quit = on;
                    _cfg.QuitMinutes = ClampInt(_cfg.QuitMinutes, QuitMin, QuitMax, 2);
                    _clockStartMs = Now();
                    _dirty = true;
                    Publish();
                });
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("No kill for the set minutes in the dungeon: /rc quit closes the client (once per run).\n"
                                 + "The timer restarts on the server's \"You've banked N Luminance.\" line, on your own killing blow,\n"
                                 + "and on a successful pet capture. Kills by pets or fellows that pay you no luminance\n"
                                 + "do NOT restart it, so keep the minutes generous if you hunt with pets.");

            ImGui.SameLine();
            ImGui.TextColored(colLabel, "after");
            ImGui.SameLine();
            if (ImGui.SmallButton($"{s.QuitMinutes} minute{(s.QuitMinutes == 1 ? "" : "s")}##vdmincycle"))
                _ctx.Post(() =>
                {
                    if (!Allowed(out _)) return;
                    EnsureLoaded();
                    _cfg.QuitMinutes = StepPreset(_cfg.QuitMinutes, +1);
                    _clockStartMs = Now();
                    _dirty = true;
                    Publish();
                });
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                _ctx.Post(() =>
                {
                    if (!Allowed(out _)) return;
                    EnsureLoaded();
                    _cfg.QuitMinutes = StepPreset(_cfg.QuitMinutes, -1);
                    _clockStartMs = Now();
                    _dirty = true;
                    Publish();
                });
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Minutes with no kill before Kill Client quits.\nClick: longer. Right-click: shorter. (1, 2, 5, 10, 15, 30)");

            BoolRow("Auto Start On Arrival##vdautostart", s.AutoStart,
                v => PostEdit(c => c.AutoStart = v),
                "Switch On by itself when you arrive in the chosen dungeon (logging in there counts).");

            BoolRow("Disable Meta##vdnometa", s.DisableMeta,
                v => PostEdit(c => c.DisableMeta = v),
                "On arriving: " + CmdMetaOff);

            BoolRow("Disable Nav##vdnonav", s.DisableNav,
                v => PostEdit(c => c.DisableNav = v),
                "On arriving: " + CmdNavOff);

            BoolRow("Repeat Attacks On##vdrepeat", s.RepeatAttack,
                v => PostEdit(c => c.RepeatAttack = v),
                "On arriving: " + CmdRepeatOn);

            BoolRow("Set Attack Power##vdpower", s.SetPower,
                v => PostEdit(c => c.SetPower = v),
                "On arriving: " + CmdAttackBar + PowerText(s.AttackPower));
            ImGui.SameLine();
            int pi = PowerIndex(s.AttackPower);
            if (ImGui.SmallButton(Powers[pi].Name + "##vdpowercycle"))
                PostEdit(c => c.AttackPower = Powers[(pi + 1) % Powers.Length].Value);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                PostEdit(c => c.AttackPower = Powers[(pi - 1 + Powers.Length) % Powers.Length].Value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Fast (0), Medium (0.5) or Full Power (1).\nClick: next. Right-click: back.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Note: the client sees the landblock, not the layer - standing in the retail copy also counts as \"in it\".");
        ImGui.TextDisabled("Kill Client: pet or fellow kills that pay you no luminance do not restart its timer.");
        ImGui.TextDisabled("Recalls wait while RynthAi is in combat, buffing or waiting on the server.");
        ImGui.TextDisabled("/ilt vaulted [on|off|toggle|status]");
    }

    private static void BoolRow(string id, bool value, Action<bool> set, string tip)
    {
        bool v = value;
        if (ImGui.Checkbox(id, ref v)) set(v);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static string[] BuildDungeonChoices()
    {
        var list = new string[Dungeons.Length + 1];
        list[0] = "Pick a dungeon";
        for (int i = 0; i < Dungeons.Length; i++) list[i + 1] = Dungeons[i].Name;
        return list;
    }

    private static int StepPreset(int current, int dir)
    {
        if (dir > 0)
        {
            foreach (int m in QuitPresets) if (m > current) return m;
            return QuitPresets[0];
        }
        for (int i = QuitPresets.Length - 1; i >= 0; i--)
            if (QuitPresets[i] < current) return QuitPresets[i];
        return QuitPresets[^1];
    }

    private static string Commas(long n)
    {
        string s = Math.Abs(n).ToString();
        var chars = new List<char>();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && (s.Length - i) % 3 == 0) chars.Add(',');
            chars.Add(s[i]);
        }
        return (n < 0 ? "-" : "") + new string(chars.ToArray());
    }

    // ── Config / snapshot types ─────────────────────────────────────────────

    // internal (not private) so the source-generated IltVaultedDungeonsJsonContext can reach it.
    internal sealed class Cfg
    {
        public bool Active { get; set; }
        /// <summary>0 = none, 1..N = Dungeons index.</summary>
        public int Dungeon { get; set; }
        /// <summary>0 = none, 1 = primary, 2 = secondary.</summary>
        public int Gem { get; set; }
        public bool Quit { get; set; }
        public int QuitMinutes { get; set; } = 2;
        public bool ShowInDungeon { get; set; }
        public bool AutoStart { get; set; }
        public bool DisableMeta { get; set; }
        public bool DisableNav { get; set; }
        public bool RepeatAttack { get; set; }
        public bool SetPower { get; set; }
        public float AttackPower { get; set; }
    }

    private readonly record struct Snapshot(
        bool Active, int Dungeon, int Gem, bool Quit, int QuitMinutes,
        bool ShowInDungeon, bool AutoStart, bool DisableMeta, bool DisableNav,
        bool RepeatAttack, bool SetPower, float AttackPower,
        string State, long? QuitLeftMs, long IdleSecs, long InSinceMs, long OutSinceMs, long PortalSinceMs,
        long RealKillMs, int Kills, long DungeonSecs, long NowMs, string Gate)
    {
        /// <summary>Before the first tick (the tab is only offered while the keeper may run).</summary>
        public static Snapshot Empty => new(false, 0, 0, false, 2,
            false, false, false, false, false, false, 0f,
            "paused", null, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty);
    }
}

// Source-generated JSON only: the NativeAOT build (what the installer ships) has reflection-based
// serialization turned off, so JsonSerializer.Deserialize<Cfg>(text, options) failed there and the
// Vaulted Dungeons settings never loaded or saved. Same file format as before (camelCase,
// indented, nulls left out, case-sensitive property names on read).
[JsonSerializable(typeof(IltVaultedDungeons.Cfg))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class IltVaultedDungeonsJsonContext : JsonSerializerContext { }
