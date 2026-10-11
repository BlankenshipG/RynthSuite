using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-08: RynthAi used an Aligned Mana Stone on a wand. Only the ordinary tapping stones
// (and mana charges) are stones to RynthAi unless a ManaStone consumable rule names one.
internal static class ManaStoneChoiceTests
{
    public static void Register(Runner r)
    {
        r.Add("mana stones: only ordinary stones and charges are used; special stones only when a rule names them", OrdinaryOnly);
        r.Add("mana stones: with tapping off, charged stones and Mana Charges still refill worn gear", RefillWithoutTapping);
        r.Add("mana stones: a stone the server called charged refills worn gear though its charge can't be read", KnownChargedRefills);
        r.Add("mana stones: a stone just drained refills worn gear though its read still says empty", DrainedStoneRefills);
        r.Add("mana stones: a stone that gave its mana is not used again; 'fully charged' holds refills", GaveManaNotReused);
        r.Add("mana stones: tapping off, a stone whose charge can't be read is identified, then used", UnreadableStoneIdentified);
    }

    // ── Refill rig (2026-10-11) ───────────────────────────────────────────
    // A stone's charge is read from its last appraisal; the server never sends a change to it.
    // 2026-10-10: five charged stones whose charge couldn't be read were tried as empty stones
    // ("The Ring is already full of mana."), marked known-charged, and then never used on a worn
    // Khanda-handled Mace at 0% for over an hour. 2026-10-08: a stone that had just given its mana
    // still read charged and was used again, cancelled by the server, timed out and parked.

    private const uint Player = 0x50000E01;
    private const uint StoneA = 0x80400001, StoneB = 0x80400002, StoneC = 0x80400003;
    private const uint Mace = 0x80400010, Ring = 0x80400020;
    private const uint CurManaKey = 107, MaxManaKey = 108, WieldedSlotKey = 10;

    private sealed class Rig : IDisposable
    {
        public long Now = 1_000_000;
        public readonly LegacyUiSettings S;
        public readonly ManaStoneManager M;

        /// <param name="stones">Stone id and its charge read (null = can't be read).</param>
        public Rig(bool tapping, (uint Id, int? Mana)[] stones, bool ringToDrain = false)
        {
            FakeHost.Reset();
            FakeHost.UseOnCalls = true;
            FakeHost.WeaponCalls = true;   // RequestId, HasAppraisalData
            FakeHost.Names[Player] = "Stone Tester";
            foreach (var (id, mana) in stones)
            {
                FakeHost.Names[id] = "Moderate Mana Stone";
                FakeHost.Containers[id] = Player;
                SetMana(id, mana);
            }
            FakeHost.Names[Mace] = "Khanda-handled Mace";
            FakeHost.Wielders[Mace] = Player;
            FakeHost.Ints[(Mace, WieldedSlotKey)] = 0x100000;
            FakeHost.Ints[(Mace, MaxManaKey)] = 5000;
            FakeHost.Ints[(Mace, CurManaKey)] = 0;   // and it keeps reading 0: worn-item mana is an appraisal too
            var ids = new List<uint>();
            foreach (var st in stones) ids.Add(st.Id);
            ids.Add(Mace);
            if (ringToDrain)
            {
                FakeHost.Names[Ring] = "Ring";
                FakeHost.Containers[Ring] = Player;
                FakeHost.Ints[(Ring, MaxManaKey)] = 4000;
                FakeHost.Ints[(Ring, CurManaKey)] = 4000;
                ids.Add(Ring);
            }

            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, ids);
            S = new LegacyUiSettings { EnableManaTapping = tapping, ManaTapMinMana = 100 };
            S.IsMacroRunning = true;
            ManaStoneManager.Clock = () => Now;
            M = new ManaStoneManager(host, S, cache);
            if (ringToDrain) M.MarkLootedForTap(unchecked((int)Ring));
        }

        public static void SetMana(uint id, int? mana)
        {
            if (mana == null) FakeHost.Ints.Remove((id, CurManaKey));
            else FakeHost.Ints[(id, CurManaKey)] = mana.Value;
        }

        public void Run(long ms)
        {
            for (long t = 0; t < ms; t += 100)
            {
                Now += 100;
                M.OnHeartbeat(0);
            }
        }

        public void Chat(string line) => M.OnChatWindowText(line);

        public List<uint> OnPlayer => FakeHost.UsesOn.Where(u => u.Target == Player).Select(u => u.Source).ToList();
        public List<uint> OnRing => FakeHost.UsesOn.Where(u => u.Target == Ring).Select(u => u.Source).ToList();

        public void Dispose() => ManaStoneManager.Clock = static () => Environment.TickCount64;
    }

    private static void KnownChargedRefills()
    {
        using var rig = new Rig(tapping: true, new (uint, int?)[] { (StoneA, null), (StoneB, null) }, ringToDrain: true);
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 1, "an unreadable stone is tried as the empty stone for the looted ring");
        rig.Chat("The Ring is already full of mana.");
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 2, "the next unreadable stone is tried");
        Check.True(rig.OnRing[0] != rig.OnRing[1], "a different stone");
        rig.Chat("The Ring is already full of mana.");
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 2, "no stone left to drain into");
        Check.Eq(rig.OnPlayer.Count, 1, "a stone the server called charged refills the 0% mace");
    }

    private static void DrainedStoneRefills()
    {
        using var rig = new Rig(tapping: true, new (uint, int?)[] { (StoneA, null) }, ringToDrain: true);
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 1, "the empty stone drains the looted ring");
        rig.Chat("The Mana Stone drains 3,600 points of mana from the Ring.\nThe Ring is destroyed.");
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 1, "the stone it just charged refills the 0% mace");
        Check.Eq(rig.OnPlayer.FirstOrDefault(), StoneA, "that stone");
    }

    private static void GaveManaNotReused()
    {
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (StoneA, 3000), (StoneB, 2000), (StoneC, 1000) });
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 1, "the fullest stone refills the mace");
        Check.Eq(rig.OnPlayer[0], StoneA, "stone A (3000)");
        rig.Chat("The Mana Stone gives 3,000 points of mana to the following items: Khanda-handled Mace.\nYou need 2,000 more mana to fully charge your items.");
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 2, "the gear still needs mana: the next stone");
        Check.Eq(rig.OnPlayer[1], StoneB, "stone B, not A again (A's read still says 3000)");
        rig.Chat("The Mana Stone gives 2,000 points of mana to the following items: Khanda-handled Mace.\nYour items are fully charged.");
        rig.Run(60_000);
        Check.Eq(rig.OnPlayer.Count, 2, "fully charged: no stone for a while though the mace still reads 0%");
        rig.Run(70_000);
        Check.Eq(rig.OnPlayer.Count, 3, "after the hold the stale read is tried once more");
        Check.Eq(rig.OnPlayer[2], StoneC, "with stone C");
        rig.Chat("You have no items equipped that need mana.");
        rig.Run(60_000);
        Check.Eq(rig.OnPlayer.Count, 3, "the server says nothing needs mana: held again, C kept");
    }

    private static void UnreadableStoneIdentified()
    {
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (StoneA, null) });
        rig.Run(1000);
        Check.True(FakeHost.IdRequests.Contains(StoneA), "the stone whose charge can't be read is identified");
        Check.Eq(rig.OnPlayer.Count, 0, "not used before its charge is known");
        Rig.SetMana(StoneA, 3000);   // the appraisal lands
        rig.Run(6000);
        Check.Eq(rig.OnPlayer.Count, 1, "then it refills the mace");
        Check.Eq(FakeHost.IdRequests.Count(i => i == StoneA), 1, "identified once");
    }

    private static void OrdinaryOnly()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var cache = new WorldObjectCache(host);
        var s = new LegacyUiSettings { EnableManaTapping = true };
        var m = new ManaStoneManager(host, s, cache);
        WorldObject W(string name) => new(0x60001000, name, AcObjectClass.ManaStone);

        foreach (string n in new[] { "Minor Mana Stone", "Lesser Mana Stone", "Moderate Mana Stone", "Mana Stone", "Greater Mana Stone", "Major Mana Stone", "Titan Mana Charge" })
            Check.True(m.IsManaStone(W(n)), $"'{n}' is a stone");
        foreach (string n in new[] { "Aligned Mana Stone", "Aetheria Mana Stone", "Unstable Mana Stone", "Enchanted Mana Stone", "Adjanite Mana Stone" })
            Check.False(m.IsManaStone(W(n)), $"'{n}' is left alone");

        s.ConsumableRules.Add(new ConsumableRule { Name = "Aligned Mana Stone", Type = "ManaStone" });
        Check.True(m.IsManaStone(W("Aligned Mana Stone")), "a rule naming it opts it in");
        Check.False(m.IsManaStone(W("Mana Stone")), "with rules, only the named stones");
    }

    private static void RefillWithoutTapping()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var cache = new WorldObjectCache(host);
        var s = new LegacyUiSettings { EnableManaTapping = false };
        var m = new ManaStoneManager(host, s, cache);
        WorldObject W(string name) => new(0x60001000, name, AcObjectClass.ManaStone);

        Check.False(m.IsManaStone(W("Major Mana Stone")), "tapping off: not a stone to loot or drain into");
        Check.True(m.IsRefillStone(W("Major Mana Stone")), "but a charged one still refills gear");
        Check.True(m.IsRefillStone(W("Titan Mana Charge")), "a bought Mana Charge too");
        Check.False(m.IsRefillStone(W("Aligned Mana Stone")), "never a special stone");

        s.ConsumableRules.Add(new ConsumableRule { Name = "Aligned Mana Stone", Type = "ManaStone" });
        Check.True(m.IsRefillStone(W("Aligned Mana Stone")), "unless a rule names it");
        Check.False(m.IsRefillStone(W("Major Mana Stone")), "with rules, only the named stones");
    }
}
