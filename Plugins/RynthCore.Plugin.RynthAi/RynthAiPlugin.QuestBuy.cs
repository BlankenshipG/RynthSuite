using System;
using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

// Buying an item a quest hands over (2026-11-08, Silentkelpie: "for the dynamic quests, it should also auto hand in
// the parchment and if one does not have any it should buy one from a vendor"). RynthOracle sends, through the
// "questloot" broker action:
//   "buy <item>"   one <item> from a vendor near you (within QuestVendorRange): the vendors are opened in turn,
//                  a Scribe or a shop first, then the nearest (ACE walks you to each), until one sells it; one is
//                  bought. Then "[RynthAi] Quest: bought <item>." once it is in the pack, or "[RynthAi] Quest:
//                  couldn't buy <item>: <why>." (RynthOracle's Dynamic Stamps hand-in reads both).
// Carried already: "bought <item> (already in your pack)." at once. Pump thread.
public sealed partial class RynthAiPlugin
{
    /// <summary>How near a vendor must be (metres).</summary>
    internal const double QuestVendorRange = 80.0;
    internal const int QuestVendorMax = 6;
    private const long QuestVendorOpenMs = 15000, QuestBuyConfirmMs = 8000, QuestVendorNextMs = 600;

    internal const string QuestBoughtPrefix = "[RynthAi] Quest: bought ";
    internal const string QuestBuyFailPrefix = "[RynthAi] Quest: couldn't buy ";

    private enum QuestBuyState { Idle, Open, Wait, Bought }
    private QuestBuyState _qbState;
    private string _qbItem = "";
    private readonly List<(uint Id, string Name)> _qbVendors = new();
    private int _qbIndex;
    private long _qbAt;
    private string _qbFrom = "";

    /// <summary>"buy &lt;item&gt;": starts the vendor walk (see the file's head).</summary>
    private (bool Ok, string Text)? TryQuestBuy(string line)
    {
        line = (line ?? "").Trim();
        if (!line.StartsWith("buy ", StringComparison.OrdinalIgnoreCase) && !line.Equals("buy", StringComparison.OrdinalIgnoreCase)) return null;
        string item = line.Length > 3 ? line.Substring(3).Trim().Trim('"') : "";
        if (item.Length == 0) return (false, "[RynthAi] questloot buy <item> (RynthOracle's Dynamic Stamps send this)");
        if (_objectCache == null) return Fail(item, "the object cache isn't ready");
        if (!Host.HasVendorTrade || !Host.HasUseObject) return Fail(item, "vendor trading needs a newer RynthCore engine");
        if (GiveCandidates(n => QuestItemNameMatches(n, item)).Count > 0)
        {
            ChatLine($"{QuestBoughtPrefix}{item} (already in your pack).");
            return (true, $"[RynthAi] Quest: {item} is in your pack already.");
        }

        _qbVendors.Clear();
        var near = new List<(WorldObject Wo, int Rank, double Dist)>();
        foreach (WorldObject wo in _objectCache.GetLandscapeObjects())
        {
            if (wo.ObjectClass != AcObjectClass.Vendor) continue;
            double d = _playerId != 0 ? _objectCache.Distance((int)_playerId, wo.Id) : double.MaxValue;
            if (d > QuestVendorRange) continue;
            near.Add((wo, VendorRank(wo.Name), d));
        }
        near.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Dist.CompareTo(b.Dist));
        foreach (var v in near)
        {
            _qbVendors.Add((unchecked((uint)v.Wo.Id), v.Wo.Name));
            if (_qbVendors.Count >= QuestVendorMax) break;
        }
        if (_qbVendors.Count == 0) return Fail(item, $"no vendor within {QuestVendorRange:F0} m of you (a town's Scribe or shop sells it)");
        _qbItem = item;
        _qbIndex = 0;
        _qbState = QuestBuyState.Open;
        _qbAt = 0;
        return (true, $"[RynthAi] Quest: looking for {item} at {_qbVendors.Count} vendor(s) near you.");
    }

    private (bool, string) Fail(string item, string why)
    {
        ChatLine($"{QuestBuyFailPrefix}{item}: {why}.");
        return (false, $"[RynthAi] Quest: {item}: {why}.");
    }

    /// <summary>A Scribe first (they sell parchment), then a shop, then the others.</summary>
    internal static int VendorRank(string? name)
    {
        string n = name ?? "";
        if (n.IndexOf("Scribe", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
        foreach (string w in new[] { "Shopkeep", "Shopkeeper", "Provisioner", "Trader", "Merchant", "Peddler", "Grocer" })
            if (n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        return 2;
    }

    /// <summary>Each tick: the vendor walk's next move. Pump thread.</summary>
    private void TickQuestBuy()
    {
        if (_qbState == QuestBuyState.Idle) return;
        long now = Environment.TickCount64;
        switch (_qbState)
        {
            case QuestBuyState.Open:
                if (now < _qbAt) return;
                if (_qbIndex >= _qbVendors.Count) { EndQuestBuy(false, $"none of the {_qbVendors.Count} vendor(s) near you sells it"); return; }
                var (id, name) = _qbVendors[_qbIndex];
                Host.UseFor(id, "Quest", $"quest: open {name} to buy {_qbItem}", UseKind.Asked);
                _qbAt = now;
                _qbState = QuestBuyState.Wait;
                return;

            case QuestBuyState.Wait:
            {
                var (vid, vname) = _qbVendors[_qbIndex];
                if (Host.TryGetVendorInfo(out VendorInfo info) && info.VendorId == vid && info.ItemCount > 0)
                {
                    VendorItem? want = null;
                    foreach (VendorItem it in Host.GetVendorItems())
                        if (QuestItemNameMatches(it.Name, _qbItem)) { want = it; break; }
                    if (want == null) { NextVendor(now); return; }
                    uint sent = Host.VendorBuy(vid, new List<VendorTradeEntryNative> { new(want.ObjectId, 1) });
                    if (sent == 0) { EndQuestBuy(false, $"{vname} sells it, but the buy could not be sent"); return; }
                    Log($"[Quest] buying 1 x {want.Name} from {vname} 0x{vid:X8}");
                    _qbFrom = vname;
                    _qbAt = now;
                    _qbState = QuestBuyState.Bought;
                    return;
                }
                if (now - _qbAt > QuestVendorOpenMs) NextVendor(now);   // never opened (out of reach, busy)
                return;
            }

            case QuestBuyState.Bought:
                if (GiveCandidates(n => QuestItemNameMatches(n, _qbItem)).Count > 0) { EndQuestBuy(true, ""); return; }
                if (now - _qbAt > QuestBuyConfirmMs) EndQuestBuy(false, $"{_qbFrom} didn't sell it (pyreals?)");
                return;
        }
    }

    private void NextVendor(long now)
    {
        _qbIndex++;
        _qbAt = now + QuestVendorNextMs;
        _qbState = QuestBuyState.Open;
    }

    private void EndQuestBuy(bool ok, string why)
    {
        string item = _qbItem;
        _qbState = QuestBuyState.Idle;
        _qbVendors.Clear();
        ChatLine(ok ? $"{QuestBoughtPrefix}{item}." : $"{QuestBuyFailPrefix}{item}: {why}.");
    }
}
