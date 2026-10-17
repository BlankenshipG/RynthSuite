// IltVaultedDungeons.cs — Mini Remote "Vaulted Dungeons" tab (ILT / ACECustom only).
// Port of the UtilityBelt VaultedDungeons.lua script (owner 2026-09-27 / 2026-10-03):
// keeps the character in one Vaulted Dungeon via a tied Portal Recall Gem, and optionally
// /rc quit after N minutes without a real kill ("You've banked N Luminance." system line).
//
// Shown only on ILT-like worlds, and only when Options > Navigation > Vaulted Dungeons is on
// (RynthAi filters the tab; this module still runs whenever the character is on ILT so a
// saved Active state keeps working after a relog even if the tab is hidden).
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

    private readonly IltHubContext _ctx;
    private readonly object _gate = new();
    private Cfg _cfg = new();
    private bool _loaded;
    private bool _dirty;
    private string? _lastGoodJson;

    // Runtime (pump thread).
    private long _outSinceMs;
    private long _lastRecallMs;
    private long _clockStartMs;
    private long _realKillMs;
    private bool _wasInside;
    private bool _quitSent;
    private long _lastWarnMs;
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

    public IltVaultedDungeons(IltHubContext ctx) => _ctx = ctx;

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
                _quitSent = false;
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
            var gem = _ctx.Inventory.FindByName(n => n.Equals(gemName, StringComparison.OrdinalIgnoreCase));
            if (gem != null)
            {
                _lastRecallMs = nowMs;
                _ctx.Chat($"[Vaulted Dungeons] Not in {dungeon.Value.Name} — using the {gemName}.");
                _ctx.Inventory.Use(gem);
            }
            else
            {
                _state = "nogem";
                Warn(nowMs, $"No {gemName} in your pack.");
            }
        }
        Publish();
    }

    public bool OnChat(string text)
    {
        if (string.IsNullOrEmpty(text) || !KillLine.IsMatch(text.TrimEnd('\r', '\n'))) return false;
        long now = IltHubContext.NowMs;
        _clockStartMs = now;
        _quitSent = false;
        _realKillMs = now;
        if (_state == "in") _kills++;
        Publish();
        return false; // never eat the luminance line
    }

    public void OnLogout()
    {
        SaveIfDirty();
        _wasInside = false;
        _outSinceMs = 0;
        _state = "paused";
        Publish();
    }

    // ── Arrive / commands ───────────────────────────────────────────────────

    private void OnArrive(VaultedDungeon dungeon, long nowMs)
    {
        if (_cfg.AutoStart && !_cfg.Active)
        {
            _cfg.Active = true;
            _outSinceMs = 0;
            _dirty = true;
            _ctx.Chat($"[Vaulted Dungeons] Arrived in {dungeon.Name} — switched On (Auto Start On Arrival).");
        }
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
        EnsureLoaded();
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "on":
            case "start":
                SetActive(true);
                _ctx.Chat("[Vaulted Dungeons] On.");
                return true;
            case "off":
            case "stop":
                SetActive(false);
                _ctx.Chat("[Vaulted Dungeons] Off.");
                return true;
            case "toggle":
                SetActive(!_cfg.Active);
                _ctx.Chat("[Vaulted Dungeons] " + (_cfg.Active ? "On." : "Off."));
                return true;
            default:
                string d = _cfg.Dungeon > 0 && _cfg.Dungeon <= Dungeons.Length
                    ? Dungeons[_cfg.Dungeon - 1].Name : "(none)";
                string g = _cfg.Gem > 0 && _cfg.Gem <= GemNames.Length
                    ? GemNames[_cfg.Gem - 1] : "(none)";
                _ctx.Chat($"[Vaulted Dungeons] {(_cfg.Active ? "On" : "Off")} — dungeon={d}, gem={g}, "
                          + $"kill-client={(_cfg.Quit ? $"{_cfg.QuitMinutes}m" : "off")}, state={_state}.");
                _ctx.Chat("[Vaulted Dungeons] /ilt vaulted [on|off|toggle|status]  (Mini Remote tab; Options > Navigation > Vaulted Dungeons)");
                return true;
        }
    }

    private void SetActive(bool value)
    {
        _cfg.Active = value;
        _outSinceMs = 0;
        _dirty = true;
        if (!value)
        {
            _clockStartMs = IltHubContext.NowMs;
            _quitSent = false;
            _wasInside = false;
            _quitLeftMs = null;
            _state = "paused";
        }
        Publish();
    }

    // ── Render snapshot ─────────────────────────────────────────────────────

    private void Publish()
    {
        lock (_gate)
        {
            _snap = new Snapshot(
                _cfg.Active, _cfg.Dungeon, _cfg.Gem, _cfg.Quit, _cfg.QuitMinutes,
                _cfg.ShowInDungeon, _cfg.AutoStart, _cfg.DisableMeta, _cfg.DisableNav,
                _cfg.RepeatAttack, _cfg.SetPower, _cfg.AttackPower,
                _state, _quitLeftMs, _idleSecs, _inSinceMs, _outSinceMs, _portalSinceMs,
                _realKillMs, _kills, _dungeonSecs, IltHubContext.NowMs);
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
                    EnsureLoaded();
                    _cfg.Quit = on;
                    _cfg.QuitMinutes = ClampInt(_cfg.QuitMinutes, QuitMin, QuitMax, 2);
                    _clockStartMs = IltHubContext.NowMs;
                    _quitSent = false;
                    _dirty = true;
                    Publish();
                });
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("No kill for the set minutes in the dungeon → /rc quit closes the client.\n"
                                 + "A kill is the server's \"You've banked N Luminance.\" line.");

            ImGui.SameLine();
            ImGui.TextColored(colLabel, "after");
            ImGui.SameLine();
            if (ImGui.SmallButton($"{s.QuitMinutes} minute{(s.QuitMinutes == 1 ? "" : "s")}##vdmincycle"))
                _ctx.Post(() =>
                {
                    EnsureLoaded();
                    _cfg.QuitMinutes = StepPreset(_cfg.QuitMinutes, +1);
                    _clockStartMs = IltHubContext.NowMs;
                    _quitSent = false;
                    _dirty = true;
                    Publish();
                });
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                _ctx.Post(() =>
                {
                    EnsureLoaded();
                    _cfg.QuitMinutes = StepPreset(_cfg.QuitMinutes, -1);
                    _clockStartMs = IltHubContext.NowMs;
                    _quitSent = false;
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
        ImGui.TextDisabled("Note: the client sees the landblock, not the layer — standing in the retail copy also counts as \"in it\".");
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
        long RealKillMs, int Kills, long DungeonSecs, long NowMs)
    {
        public static Snapshot Empty => new(false, 0, 0, false, 2,
            false, false, false, false, false, false, 0f,
            "paused", null, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}

// Source-generated JSON only: the NativeAOT build (what the installer ships) has reflection-based
// serialization turned off, so JsonSerializer.Deserialize<Cfg>(text, options) failed there and the
// Vaulted Dungeons settings never loaded or saved. Same file format as before (camelCase,
// indented, nulls left out).
[JsonSerializable(typeof(IltVaultedDungeons.Cfg))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class IltVaultedDungeonsJsonContext : JsonSerializerContext { }
