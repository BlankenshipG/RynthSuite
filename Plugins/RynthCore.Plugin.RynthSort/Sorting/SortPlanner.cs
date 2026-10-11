using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>What a planning pass looks at.</summary>
internal enum SortScope
{
    /// <summary>Only the main pack; an item moves once it has sat there for the settle time (unless forced).</summary>
    Main,
    /// <summary>Every pack, no settling: items in the wrong side pack move to theirs too ("Tidy").</summary>
    All,
    /// <summary>As <see cref="All"/>, and side-pack items no rule wants go back to the main pack ("Clean").</summary>
    Clean,
    /// <summary>Every side pack (or one) emptied into the main pack ("Unpack").</summary>
    Unpack,
}

/// <summary>One move: <see cref="Item"/> into <see cref="Dest"/> (pack <see cref="DestIndex"/>, 0 = main pack).</summary>
internal readonly struct SortMove
{
    public readonly SortItem Item;
    public readonly SortPack Dest;
    public readonly int DestIndex;
    /// <summary>A stack in <see cref="Dest"/> the item joins (a merge), or 0 for a plain move.</summary>
    public readonly uint MergeInto;

    public SortMove(SortItem item, SortPack dest, int destIndex, uint mergeInto)
    {
        Item = item;
        Dest = dest;
        DestIndex = destIndex;
        MergeInto = mergeInto;
    }

    public string DestLabel => DestIndex == 0 ? "main pack" : "pack " + DestIndex;
}

/// <summary>A side pack as the window lists it.</summary>
internal sealed class PackLine
{
    public string Name = string.Empty;
    public int Used;
    public int Capacity;
}

/// <summary>What the window shows about the packs after a planning pass.</summary>
internal sealed class SortView
{
    public readonly List<PackLine> Packs = new();
    public int MainUsed;
    public int MainCapacity;
    /// <summary>Items a rule wants moved that can't go yet (settling, pack full, pack missing, main full).</summary>
    public int Waiting;
    /// <summary>Side packs (1-based) a rule wants to fill that are full.</summary>
    public readonly SortedSet<int> Full = new();
    /// <summary>Pack numbers rules use that the character doesn't have.</summary>
    public readonly SortedSet<int> Missing = new();
    /// <summary>Clean or Unpack found the main pack full.</summary>
    public bool MainFull;
}

/// <summary>
/// The sorting rules applied to the packs: which items move where, one planning pass at a time.
/// A port of the Lua script's plan/planUnpack: rules are tried in order and the first match wins;
/// worn and wielded items, packs and foci never move; a full pack is only used when the item
/// joins a stack there; an item that failed to move is left alone until its block runs out.
/// Pure (no host calls): the tests drive it with hand-made inventories.
/// </summary>
internal sealed class SortPlanner
{
    public const int SideCapacityFallback = 24;
    public const int MainCapacityFallback = 102;

    private readonly Dictionary<uint, long> _firstSeen = new();
    private readonly Dictionary<uint, long> _blocked = new();

    public SortView View { get; private set; } = new();

    /// <summary>Forget everything (logout, another character).</summary>
    public void Reset()
    {
        _firstSeen.Clear();
        _blocked.Clear();
        View = new SortView();
    }

    /// <summary>Leave <paramref name="itemId"/> alone until <paramref name="untilMs"/> (a move failed).</summary>
    public void Block(uint itemId, long untilMs) => _blocked[itemId] = untilMs;

    public bool IsBlocked(uint itemId, long nowMs) => _blocked.TryGetValue(itemId, out long until) && until > nowMs;

    public int BlockedCount(long nowMs)
    {
        int n = 0;
        foreach (long until in _blocked.Values) if (until > nowMs) n++;
        return n;
    }

    /// <summary>Worn and wielded items, packs and foci never move.</summary>
    public static bool Sortable(SortItem it) =>
        !it.Wielded && it.ObjectClass != ObjectClasses.Container && it.ObjectClass != ObjectClasses.Foci;

    public static int CapacityOf(SortPack p, int fallback) => p.Capacity > 0 ? p.Capacity : fallback;

    /// <summary>
    /// A stack in <paramref name="pack"/> that <paramref name="it"/> can join (the same kind, with room
    /// left), or 0. That is what lets a full pack still take the item.
    /// </summary>
    public static uint StackTarget(SortItem it, SortPack pack)
    {
        if (it.MaxStack <= 1) return 0;
        foreach (SortItem other in pack.Items)
        {
            if (other.Id == it.Id || !SameKind(it, other)) continue;
            int max = other.MaxStack > 1 ? other.MaxStack : it.MaxStack;
            if (other.Stack < max) return other.Id;
        }
        return 0;
    }

    private static bool SameKind(SortItem a, SortItem b) =>
        a.Wcid != 0 && b.Wcid != 0 ? a.Wcid == b.Wcid : string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.Name.Length > 0;

    /// <summary>
    /// The moves to make now for <paramref name="scope"/> (not <see cref="SortScope.Unpack"/>: see
    /// <see cref="PlanUnpack"/>), first one first. Also refreshes <see cref="View"/> and the main
    /// pack's settle clock (an item's time starts when a pass first sees it in the main pack).
    /// </summary>
    public List<SortMove> Plan(SortInventory inv, IReadOnlyList<SortRule> rules, SortScope scope, bool force, long nowMs, int settleMs)
    {
        bool everyPack = scope != SortScope.Main;
        bool evict = scope == SortScope.Clean;
        var moves = new List<SortMove>();
        var view = new SortView();
        var used = new int[inv.Packs.Count + 1];
        for (int i = 0; i < inv.Packs.Count; i++) used[i + 1] = inv.Packs[i].Items.Count;
        int mainUsed = inv.Main.Items.Count;
        int mainCap = CapacityOf(inv.Main, MainCapacityFallback);

        void Consider(SortItem wo, int from, bool settled)
        {
            if (!Sortable(wo) || IsBlocked(wo.Id, nowMs)) return;
            SortRule? r = SortRules.RuleFor(rules, wo);
            if (r == null && evict && from > 0)
            {
                // In a side pack and no rule wants it: back to the main pack, while there is room.
                uint joins = StackTarget(wo, inv.Main);
                if (mainUsed < mainCap || joins != 0)
                {
                    moves.Add(new SortMove(wo, inv.Main, 0, joins));
                    if (joins == 0) mainUsed++;
                }
                else
                {
                    view.MainFull = true;
                    view.Waiting++;
                }
                return;
            }
            if (r == null || r.Pack == from) return;
            if (r.Pack < 1 || r.Pack > inv.Packs.Count)
            {
                view.Missing.Add(r.Pack);
                view.Waiting++;
                return;
            }
            SortPack dest = inv.Packs[r.Pack - 1];
            uint into = StackTarget(wo, dest);
            if (used[r.Pack] >= CapacityOf(dest, SideCapacityFallback) && into == 0)
            {
                view.Full.Add(r.Pack);
                view.Waiting++;
                return;
            }
            if (!settled)
            {
                view.Waiting++;
                return;
            }
            moves.Add(new SortMove(wo, dest, r.Pack, into));
            if (into == 0) used[r.Pack]++;
        }

        var inMain = new HashSet<uint>();
        foreach (SortItem wo in inv.Main.Items)
        {
            inMain.Add(wo.Id);
            if (!_firstSeen.TryGetValue(wo.Id, out long seen)) _firstSeen[wo.Id] = seen = nowMs;
            Consider(wo, 0, force || everyPack || nowMs - seen >= settleMs);
        }
        if (_firstSeen.Count > inMain.Count)
        {
            var gone = new List<uint>();
            foreach (uint id in _firstSeen.Keys) if (!inMain.Contains(id)) gone.Add(id);
            foreach (uint id in gone) _firstSeen.Remove(id);
        }
        if (everyPack)
        {
            for (int i = 0; i < inv.Packs.Count; i++)
                foreach (SortItem wo in inv.Packs[i].Items) Consider(wo, i + 1, true);
        }

        FillPacks(view, inv);
        View = view;
        return moves;
    }

    /// <summary>
    /// Emptying side packs into the main pack: every sortable item in side pack <paramref name="which"/>
    /// (1-based; null = all of them), while the main pack has room or the item joins a stack there.
    /// </summary>
    public List<SortMove> PlanUnpack(SortInventory inv, int? which, long nowMs)
    {
        var moves = new List<SortMove>();
        int used = inv.Main.Items.Count;
        int cap = CapacityOf(inv.Main, MainCapacityFallback);
        int waiting = 0;
        for (int i = 0; i < inv.Packs.Count; i++)
        {
            if (which != null && which.Value != i + 1) continue;
            foreach (SortItem wo in inv.Packs[i].Items)
            {
                if (!Sortable(wo) || IsBlocked(wo.Id, nowMs)) continue;
                uint joins = StackTarget(wo, inv.Main);
                if (used < cap || joins != 0)
                {
                    moves.Add(new SortMove(wo, inv.Main, 0, joins));
                    if (joins == 0) used++;
                }
                else waiting++;
            }
        }
        var view = new SortView { Waiting = waiting, MainFull = waiting > 0 };
        FillPacks(view, inv);
        View = view;
        return moves;
    }

    private static void FillPacks(SortView view, SortInventory inv)
    {
        view.MainUsed = inv.Main.Items.Count;
        view.MainCapacity = CapacityOf(inv.Main, MainCapacityFallback);
        foreach (SortPack p in inv.Packs)
            view.Packs.Add(new PackLine
            {
                Name = p.Name.Length > 0 ? p.Name : "Pack",
                Used = p.Items.Count,
                Capacity = CapacityOf(p, SideCapacityFallback),
            });
    }
}
