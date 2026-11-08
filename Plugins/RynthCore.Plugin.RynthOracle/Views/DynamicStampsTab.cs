using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Dynamic Stamps (Infinite Leaftide only, ServerIdentity.IsIlt): the NPC hand-ins that put a "Dynamic_N_..." flag on
/// the character, read from /myquests. Since 2026-10-18 they are rows of the Quests tab's merged
/// list (QuestsTab.cs: DYN rows, the Show filter's area picker). What is left here is the opened
/// stamp's box (RynthNav, the Parchment hand-in, chat), and the read after a hand-in. The list follows the players' "ExtraDyn" sheet (dynstamps.csv).
/// Since 2026-11-08 (Silentkelpie: "for the dynamic quests, it should also auto hand in the parchment and if one does
/// not have any it should buy one from a vendor") Go hands the Parchment in on arrival by itself, and with none in
/// the packs RynthAi buys one from a vendor near you first ("questloot buy Parchment": before setting off, and
/// again at the NPC's place when that failed).
/// </summary>
internal sealed partial class OracleView
{
    // The stamp clicked last: the box below the list offers RynthNav's auto nav to it.
    private DynamicStamp? _dynNav;
    private bool _dynNavDone;
    private string _dynNavStatus = "";

    /// <summary>
    /// The destination box (the click's pop-up): the stamp, where RynthNav will take you, and Go (plan
    /// and walk the route: portals, recalls and dungeon entrances included), Arrow, Chat and Close.
    /// </summary>
    private void DrawDynamicNavBox(UiWindow w)
    {
        DynamicStamp? s = _dynNav;
        if (s == null) return;
        List<string> targets = s.NavCandidates();
        w.SeparatorText("Navigate to " + Cut(s.Label, 40));
        w.TextDisabled(targets.Count == 0
            ? "No place, coordinates or area to go to for this stamp."
            : "Where: " + Cut(string.Join("  or  ", targets), 90));
        if (targets.Count > 0)
        {
            w.Button(s.Npc.Length > 0 ? "Go and hand in" : "Go (auto nav)", "dy.nav.go", () => StartDynAuto(s));
            w.Tooltip(s.Npc.Length > 0
                ? "RynthNav plans the route (walking, portals, recalls, the dungeon's entrance) and walks it; there, a Parchment\n"
                  + "from your packs is handed to " + s.Npc + ". With none, RynthAi buys one from a vendor near you first\n"
                  + "(a Scribe or a shop within 80 m), or at the NPC's place."
                : "RynthNav plans the route (walking, portals, recalls, the dungeon's entrance) and walks it.");
            w.SameLine();
            w.Button("Arrow", "dy.nav.arrow", () => SendNav("arrow", s));
            w.Tooltip("Only point RynthNav's arrow at it.");
            w.SameLine();
        }
        w.Button("Chat", "dy.nav.chat", () => PrintStamp(s, _dynNavDone));
        w.SameLine();
        w.Button("Close", "dy.nav.close", () => { _dynNav = null; _dynNavStatus = ""; _selKey = ""; });

        // The hand-in: a Parchment from your packs to the stamp's NPC (RynthAi's /ub give).
        if (s.Npc.Length > 0)
        {
            string? parchment = CarriedParchment();
            if (parchment != null)
            {
                w.Button("Give " + Cut(parchment, 24), "dy.nav.give", () => GiveParchment(s, parchment));
                w.Tooltip($"Hands one {parchment} to {s.Npc}. Stand near them (or Go first): AC walks you the last bit.");
            }
            else
            {
                w.TextDisabled("No Parchment in your packs to give " + Cut(s.Npc, 30) + ".");
                w.SameLine();
                w.SmallButton("Buy one", "dy.nav.buy", () =>
                    _dynNavStatus = _c.SendAi("questloot", "buy Parchment")
                        ? "RynthAi is looking for a Parchment at the vendors near you." : "RynthAi isn't loaded (it buys the Parchment).");
                w.Tooltip("RynthAi opens the vendors near you (a Scribe or a shop first, within 80 m) until one sells a Parchment, and buys one.");
            }
        }
        if (_dynNavStatus.Length > 0) w.TextDisabled(_dynNavStatus);
    }

    // ── Go and hand in (2026-11-08) ──

    private enum DynPhase { None, BuyFirst, Going, BuyThere }
    private DynamicStamp? _dynAuto;
    private DynPhase _dynPhase;
    internal const string BoughtLine = "[RynthAi] Quest: bought Parchment";
    internal const string BuyFailLine = "[RynthAi] Quest: couldn't buy Parchment";

    /// <summary>Go: the Parchment first when none is carried (bought near you), then RynthNav; the hand-in on arrival.</summary>
    private void StartDynAuto(DynamicStamp s)
    {
        _dynAuto = s;
        _dynPhase = DynPhase.None;
        if (!_c.InWorld) { _dynNavStatus = "Log in first."; return; }
        if (s.Npc.Length == 0) { SendNav("go", s); return; }
        _parchmentScanAt = 0;
        if (CarriedParchment() == null && _c.SendAi("questloot", "buy Parchment"))
        {
            _dynPhase = DynPhase.BuyFirst;
            _dynNavStatus = $"No Parchment: RynthAi is buying one near you first, then RynthNav goes to {s.Npc}.";
            return;
        }
        GoDyn(s, "");
    }

    private void GoDyn(DynamicStamp s, string before)
    {
        SendNav("go", s);
        _dynPhase = _dynNavStatus.StartsWith("RynthNav is planning", StringComparison.Ordinal) ? DynPhase.Going : DynPhase.None;
        if (_dynPhase == DynPhase.Going) _dynNavStatus = before + $"RynthNav is walking to {s.Npc}; the Parchment is handed in when it gets there.";
    }

    /// <summary>A chat line while Go and hand in runs: RynthAi's buy, RynthNav's arrival. Plugin calls it for every line.</summary>
    internal void OnDynChat(string line)
    {
        DynamicStamp? s = _dynAuto;
        if (s == null || _dynPhase == DynPhase.None) return;
        bool bought = line.Contains(BoughtLine, StringComparison.OrdinalIgnoreCase);
        bool noBuy = line.Contains(BuyFailLine, StringComparison.OrdinalIgnoreCase);
        switch (_dynPhase)
        {
            case DynPhase.BuyFirst:
                if (bought) GoDyn(s, "Bought a Parchment. ");
                else if (noBuy) GoDyn(s, "No Parchment to buy here (tried again there). ");
                break;
            case DynPhase.Going:
                if (!line.Contains("[RynthNav]", StringComparison.Ordinal)) break;
                if (line.Contains("goto: arrived", StringComparison.OrdinalIgnoreCase)) DynArrived(s);
                else if (line.Contains("goto:", StringComparison.OrdinalIgnoreCase) || line.Contains("on the map", StringComparison.OrdinalIgnoreCase))
                {
                    _dynPhase = DynPhase.None;
                    _dynNavStatus = "RynthNav didn't get there (" + Cut(line.Trim(), 80) + "): Go again, or Give by hand.";
                }
                break;
            case DynPhase.BuyThere:
                if (bought)
                {
                    _parchmentScanAt = 0;
                    _dynPhase = DynPhase.None;
                    if (CarriedParchment() is string p) GiveParchment(s, p);
                    else _dynNavStatus = "Bought a Parchment, but it isn't in your packs yet: Give it by hand.";
                }
                else if (noBuy)
                {
                    _dynPhase = DynPhase.None;
                    _dynNavStatus = $"No Parchment, and no vendor near {s.Npc} sells one: buy one in a town (a Scribe), then Go again.";
                }
                break;
        }
    }

    private void DynArrived(DynamicStamp s)
    {
        _parchmentScanAt = 0;
        if (CarriedParchment() is string p) { _dynPhase = DynPhase.None; GiveParchment(s, p); return; }
        if (_c.SendAi("questloot", "buy Parchment"))
        {
            _dynPhase = DynPhase.BuyThere;
            _dynNavStatus = $"At {s.Npc}, no Parchment: RynthAi is buying one from a vendor here.";
            return;
        }
        _dynPhase = DynPhase.None;
        _dynNavStatus = $"At {s.Npc}, but no Parchment in your packs.";
    }

    // A /myquests read this long after a Parchment hand-in (the walk there and the NPC's answer), so
    // the new stamp shows as had; 0 = none due.
    private const long HandInRefreshMs = 8000;
    private long _dynRefreshAt;

    /// <summary>The read after a hand-in, when it is due: now, or when the server's once-a-minute wait ends.</summary>
    private void RefreshAfterHandIn(QuestFlagStore flags)
    {
        if (_dynRefreshAt == 0 || _c.NowMs < _dynRefreshAt) return;
        if (flags.Loading) return;
        int wait = flags.ThrottleSecondsLeft(_c.NowMs);
        if (wait > 0) { _dynRefreshAt = _c.NowMs + wait * 1000L; return; }
        _dynRefreshAt = 0;
        _c.RequestQuests();
    }

    // Packs scanned for a Parchment at most this often (the box draws every frame).
    private const long ParchmentScanMs = 1500;
    private long _parchmentScanAt;
    private string? _parchmentName;

    /// <summary>
    /// The name of a Parchment you carry (the main pack and its side packs), or null. Any item
    /// whose name has "Parchment" in it; read again every <see cref="ParchmentScanMs"/>.
    /// </summary>
    private string? CarriedParchment()
    {
        if (_c.NowMs - _parchmentScanAt < ParchmentScanMs && _parchmentScanAt != 0) return _parchmentName;
        _parchmentScanAt = _c.NowMs == 0 ? 1 : _c.NowMs;
        _parchmentName = null;
        RynthCore.PluginSdk.RynthCoreHost host = _c.Host;
        uint me = _c.Character.PlayerId;
        if (!_c.InWorld || me == 0 || !host.HasGetContainerContents) return null;
        var buf = new uint[512];
        var packs = new List<uint> { me };
        for (int p = 0; p < packs.Count && p < 16; p++)
        {
            int n = Math.Min(host.GetContainerContents(packs[p], buf), buf.Length);
            for (int i = 0; i < n; i++)
            {
                uint id = buf[i];
                if (!host.TryGetObjectName(id, out string name)) continue;
                if (name.IndexOf("Parchment", StringComparison.OrdinalIgnoreCase) >= 0) { _parchmentName = name; return name; }
                // A side pack in the main pack: look inside it too (only one level deep, as AC packs go).
                if (p == 0) packs.Add(id);
            }
        }
        return null;
    }

    /// <summary>"/ub give 1 &lt;parchment&gt; to &lt;npc&gt;": RynthAi finds the NPC by name and hands it over.</summary>
    private void GiveParchment(DynamicStamp s, string parchment)
    {
        if (!_c.InWorld) { _dynNavStatus = "Log in first."; return; }
        bool sent = _c.RunCommand($"/ub give 1 {parchment} to {s.Npc}");
        _parchmentScanAt = 0;   // read the packs again after the give
        // The stamp is a quest flag: read /myquests again once the NPC has had time to answer.
        if (sent) _dynRefreshAt = _c.NowMs + HandInRefreshMs;
        _dynNavStatus = sent
            ? $"Giving {parchment} to {s.Npc} (RynthAi does the give; it says so in chat if {s.Npc} isn't in sight)."
            : "Could not send the give.";
    }

    /// <summary>Clicking a row: select it and ask RynthNav for the route (shown in chat and its overlay).</summary>
    private void SelectDynamicStamp(DynamicStamp s, bool done)
    {
        _dynNav = s;
        _dynNavDone = done;
        _dynNavStatus = "";
        if (s.NavCandidates().Count > 0) SendNav("route", s);
    }

    /// <summary>"/rnav &lt;verb&gt; a || b || c" to RynthNav (it takes the first candidate on its map).</summary>
    private void SendNav(string verb, DynamicStamp s)
    {
        List<string> targets = s.NavCandidates();
        if (targets.Count == 0) return;
        _dynNavStatus = SendRynthNav(verb, targets);
    }

    /// <summary>Chat: the NPC, its stamp and whether this character has it, where, the reward and
    /// any coordinates (RynthChat makes those a RynthNav arrow link).</summary>
    private void PrintStamp(DynamicStamp s, bool done)
    {
        string where = s.Place.Length > 0 ? $"{s.Area}, {s.Place}" : s.Area;
        string details = $"{where}" + (s.Reward.Length > 0 ? $" - gives {s.Reward}" : "") + (s.Note.Length > 0 ? $" - {s.Note}" : "");
        PrintEntry(s.Label, s.Stamp, done ? "have" : "missing", details, "");
    }
}
