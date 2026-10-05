// IltCharmTracker.cs — Snapshot of every ACECustom registry charm for the engine Settings
// panel's "Charms Tracking" tab (RynthPluginGetCharmsJson).
//
// Per charm it reports:
//   acquired — "carried" (in pack / equipped now), "seen" (carried earlier this or a past
//              session, remembered in IltCharacterState.CharmsSeen), or "no".
//   active   — "on" / "off" / "unknown". The charm's appraisal Use text ("Status: ON") wins;
//              otherwise the player ability PropertyBool the server sets while it's active.
//              The server deactivates abilities whose charm isn't possessed, so not carried = off.
//   server   — "off" when the server refused / reported the charm disabled, "on" when a feature
//              dump reported it enabled, else "unknown" (clients can't query /charms).
// Pump thread only (inventory and property reads).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltCharmTracker
{
    private readonly IltHubContext _ctx;

    public IltCharmTracker(IltHubContext ctx) => _ctx = ctx;

    /// <summary>One charm's tracked state.</summary>
    internal readonly record struct CharmState(
        IltCharmDef Def, string Acquired, int Count, int Tier, int MaxTier,
        IltTri Active, IltTri Server, DateTime LastSeenUtc);

    /// <summary>Evaluates every registry charm and refreshes the "seen" memory for carried ones.</summary>
    public List<CharmState> Evaluate()
    {
        var inv = _ctx.Inventory;
        var seenMap = _ctx.State.Character.CharmsSeen;
        var result = new List<CharmState>(IltInventory.Charms.Length);
        // One inventory pass, grouped by charm definition, instead of a WCID scan per charm.
        var carried = new Dictionary<IltCharmDef, List<WorldObject>>();
        foreach (var wo in inv.Items())
        {
            var def = inv.CharmDefFor(wo);
            if (def == null) continue;
            if (!carried.TryGetValue(def, out var list)) carried[def] = list = new();
            list.Add(wo);
        }

        foreach (var def in IltInventory.Charms)
        {
            carried.TryGetValue(def, out var items);
            int count = items?.Count ?? 0;
            int tier = 0, maxTier = 0;
            IltTri active = IltTri.Off;

            if (count > 0)
            {
                // Server allows only one active charm per ability, so any ON item means ON.
                bool anyOn = false, allKnown = true;
                foreach (var wo in items!)
                {
                    var (t, m) = inv.CharmTier(wo);
                    if (t > tier) { tier = t; maxTier = m; }
                    var st = inv.CharmStatus(wo);
                    if (st == IltTri.On) anyOn = true;
                    else if (st == IltTri.Unknown) allKnown = false;
                }
                if (anyOn) active = IltTri.On;
                else if (def.PlayerFlag != 0 && inv.PlayerBool(def.PlayerFlag)) active = IltTri.On;
                else active = allKnown ? IltTri.Off : IltTri.Unknown;

                if (!seenMap.TryGetValue(def.Name, out var seen)) seenMap[def.Name] = seen = new IltCharmSeen();
                if (tier > seen.Tier) { seen.Tier = tier; seen.MaxTier = maxTier; }
                // Day resolution keeps the persisted state from changing (and re-saving) every poll.
                var today = DateTime.UtcNow.Date;
                if (seen.LastSeenUtc != today) seen.LastSeenUtc = today;
            }

            seenMap.TryGetValue(def.Name, out var memory);
            string acquired = count > 0 ? "carried" : memory != null ? "seen" : "no";
            if (count == 0 && memory != null) { tier = memory.Tier; maxTier = memory.MaxTier; }

            result.Add(new CharmState(def, acquired, count, tier, maxTier, active,
                ServerState(def), memory?.LastSeenUtc ?? default));
        }
        return result;
    }

    /// <summary>Server-side enabled state as far as the client has learned it.</summary>
    private IltTri ServerState(IltCharmDef def)
    {
        var opts = _ctx.Options;
        var tri = opts.Get(IltFeature.Charm(def.Name));
        // Universal Summoning Mastery also has a ServerConfig switch, reported separately.
        if (Array.IndexOf(def.Wcids, IltInventory.WcidUniversalMasteryCharm) >= 0)
        {
            var cfg = opts.Get(IltFeature.UniversalMastery);
            if (cfg == IltTri.Off || tri == IltTri.Off) return IltTri.Off;
            if (cfg == IltTri.On || tri == IltTri.On) return IltTri.On;
        }
        return tri;
    }

    /// <summary>JSON for RynthPluginGetCharmsJson. "available" is false off ILT-like worlds.</summary>
    public string BuildJson()
    {
        var sb = new StringBuilder(4096);
        bool available = _ctx.Options.IsIltLikeWorld;
        sb.Append("{\"available\":").Append(available ? "true" : "false");
        sb.Append(",\"world\":\"").Append(RynthAiPlugin.JsonEscape(_ctx.Options.WorldName ?? string.Empty)).Append('"');
        if (!available) return sb.Append(",\"charms\":[]}").ToString();

        var rows = Evaluate();
        sb.Append(",\"total\":").Append(rows.Count);
        sb.Append(",\"acquiredCount\":").Append(rows.Count(r => r.Acquired != "no"));
        sb.Append(",\"activeCount\":").Append(rows.Count(r => r.Active == IltTri.On));
        sb.Append(",\"charms\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(RynthAiPlugin.JsonEscape(r.Def.Name)).Append('"');
            sb.Append(",\"effect\":\"").Append(RynthAiPlugin.JsonEscape(r.Def.Effect)).Append('"');
            sb.Append(",\"acquired\":\"").Append(r.Acquired).Append('"');
            sb.Append(",\"count\":").Append(r.Count);
            sb.Append(",\"tier\":").Append(r.Tier);
            sb.Append(",\"maxTier\":").Append(r.MaxTier);
            sb.Append(",\"active\":\"").Append(TriText(r.Active)).Append('"');
            sb.Append(",\"server\":\"").Append(TriText(r.Server)).Append('"');
            sb.Append(",\"lastSeen\":\"")
              .Append(r.LastSeenUtc == default ? string.Empty : r.LastSeenUtc.ToString("yyyy-MM-dd"))
              .Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string TriText(IltTri t) => t switch { IltTri.On => "on", IltTri.Off => "off", _ => "unknown" };
}
