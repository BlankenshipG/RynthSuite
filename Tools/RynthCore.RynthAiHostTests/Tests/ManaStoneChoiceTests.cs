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
        r.Add("mana charges: a stack of 5 refills the gear 5 times", StackOfFiveRefillsFiveTimes);
        r.Add("mana charges: a single stone (stack of 1) that gave its mana is not used again", SingleStoneNotReused);
        r.Add("mana charges: used up, the next charge of that name is found (retail: each use destroys one)", NextChargeFoundByName);
        r.Add("mana charges: never taken for the empty stone to drain loot into", ChargeIsNeverEmptyStone);
        r.Add("mana charges: refill gear even when ManaStone rules name only other stones", ChargesIgnoreStoneRules);
        r.Add("mana stones: the server's 'low on Mana' refills gear whose read is stale-high", ServerLowRefills);
        r.Add("mana stones: worn gear is identified again, so a stale-high read follows the gear", GearReadRefreshed);
        r.Add("mana charges: overnight idle, the gear never runs dry (re-identify)", () => Overnight(identify: true));
        r.Add("mana charges: overnight idle, the gear never runs dry (server warnings only)", () => Overnight(identify: false));
        r.Add("mana tapping: one item at a time - the stone first, then one item, drained before the next", OneTapItemAtATime);
        r.Add("mana tapping: no drain until the pickup is confirmed in the pack", NoDrainBeforeArrival);
        r.Add("mana tapping: a drain that never completes keeps the item's place, tries the next stone, then gives up", FailedDrainKeepsPlace);
    }

    private const uint ChargeA = 0x80400031, ChargeB = 0x80400032, ChargeC = 0x80400033;
    private const string ManaCharge = "Great Mana Charge";
    private const string NeedMore = "The Mana Stone gives 1,000 points of mana to the following items: Khanda-handled Mace.\nYou need 2,000 more mana to fully charge your items.";

    // 2026-10-11 report: armor ran dry overnight with Mana Charges in the pack. The prime suspect
    // (a stack of charges marked empty after one refill) doesn't happen with retail data: ACE Mana
    // Charges (wcid 4612-4616, 9060, 20179, 27329) don't stack and every use destroys one. But a stack
    // must still count as a source until it's gone.
    private static void StackOfFiveRefillsFiveTimes()
    {
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (ChargeA, 1000) }, stoneName: ManaCharge);
        FakeHost.Ints[(ChargeA, StackSizeKey)] = 5;
        for (int i = 1; i <= 5; i++)
        {
            rig.Run(1000);
            Check.Eq(rig.OnPlayer.Count, i, $"refill {i} from the stack");
            Check.Eq(rig.OnPlayer[i - 1], ChargeA, "the same stack");
            rig.Chat(NeedMore);
            int left = FakeHost.Ints[(ChargeA, StackSizeKey)] - 1;
            if (left > 0) FakeHost.Ints[(ChargeA, StackSizeKey)] = left;
            else rig.Destroy(ChargeA);
        }
        rig.Run(10_000);
        Check.Eq(rig.OnPlayer.Count, 5, "the stack is used up: nothing more to use");
    }

    private static void SingleStoneNotReused()
    {
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (StoneA, 3000), (StoneB, 1000) });
        FakeHost.Ints[(StoneA, StackSizeKey)] = 1;
        FakeHost.Ints[(StoneB, StackSizeKey)] = 1;
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 1, "the fuller stone first");
        Check.Eq(rig.OnPlayer[0], StoneA, "stone A");
        rig.Chat(NeedMore);   // A survived its destroy roll; its read still says 3000
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 2, "the gear still needs mana");
        Check.Eq(rig.OnPlayer[1], StoneB, "stone B, not A again");
    }

    private static void NextChargeFoundByName()
    {
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (ChargeA, 1000), (ChargeB, 1000) }, stoneName: ManaCharge);
        for (int i = 0; i < 2; i++)
        {
            rig.Run(1000);
            rig.Chat(NeedMore);
            rig.Destroy(rig.OnPlayer[^1]);           // ACE: Mana Charges always break (destroy chance 1.0)
            rig.Chat("The Mana Stone is destroyed.");
        }
        Check.Eq(rig.OnPlayer.Count, 2, "both charges used");
        Check.True(rig.OnPlayer[0] != rig.OnPlayer[1], "one each");
        rig.Run(10_000);
        Check.Eq(rig.OnPlayer.Count, 2, "none left");
        rig.AddStone(ChargeC, 1000);                  // bought another
        rig.Run(6000);
        Check.Eq(rig.OnPlayer.Count, 3, "the new charge is found by name");
        Check.Eq(rig.OnPlayer[2], ChargeC, "charge C");
    }

    private static void ChargeIsNeverEmptyStone()
    {
        using var rig = new Rig(tapping: true, new (uint, int?)[] { (ChargeA, null) }, ringToDrain: true, stoneName: ManaCharge, maceMana: 5000);
        rig.Run(3000);
        Check.Eq(rig.OnRing.Count, 0, "an unread Mana Charge is not used to drain the looted ring (it would pour its charge into it)");
        Check.Eq(rig.M.CountEmptyStones(), 0, "no empty stone slots from charges");
        Check.False(rig.M.HasEmptyManaStone(), "no empty stone");
    }

    private static void ChargesIgnoreStoneRules()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var cache = new WorldObjectCache(host);
        var s = new LegacyUiSettings { EnableManaTapping = true };
        s.ConsumableRules.Add(new ConsumableRule { Name = "Greater Mana Stone", Type = "ManaStone" });
        var m = new ManaStoneManager(host, s, cache);
        WorldObject W(string name) => new(0x60001000, name, AcObjectClass.ManaStone);
        Check.True(m.IsRefillStone(W("Massive Mana Charge")), "a Mana Charge refills gear though the rules name only a stone");
        Check.False(m.IsRefillStone(W("Major Mana Stone")), "an unnamed stone still doesn't");
        s.EnableManaTapping = false;
        Check.True(m.IsRefillStone(W("Tiny Mana Charge")), "tapping off too");
    }

    private static void ServerLowRefills()
    {
        // The mace was identified at 90% and reads that forever (the fake never updates it).
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (ChargeA, 1000), (ChargeB, 1000) }, stoneName: ManaCharge, maceMana: 4500);
        rig.Run(30_000);
        Check.Eq(rig.OnPlayer.Count, 0, "the read says 90%: nothing to do");
        rig.Chat("Your Khanda-handled Mace is low on Mana.");
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 1, "the server's warning wins over the read");
        rig.Chat(NeedMore);
        rig.Destroy(rig.OnPlayer[0]);
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 2, "still short of full: the next charge");
        rig.Chat("The Mana Stone gives 1,000 points of mana to the following items: Khanda-handled Mace.");
        rig.Chat("Your items are fully charged.");   // as a line of its own
        rig.Run(5 * 60_000);
        Check.Eq(rig.OnPlayer.Count, 2, "full: back to the reads (90%)");
    }

    private static void GearReadRefreshed()
    {
        using var rig = new Rig(tapping: false, new (uint, int?)[] { (ChargeA, 1000) }, stoneName: ManaCharge, maceMana: 4500);
        rig.Run(2000);
        int first = FakeHost.IdRequests.Count(i => i == Mace);
        Check.True(first >= 1, "the worn mace is identified");
        rig.Run(5 * 60_000);
        Check.Eq(FakeHost.IdRequests.Count(i => i == Mace), first, "not again within 10 minutes");
        rig.Run(6 * 60_000);
        Check.Eq(FakeHost.IdRequests.Count(i => i == Mace), first + 1, "again after 10 minutes");
        FakeHost.Ints[(Mace, CurManaKey)] = 1000;   // that appraisal shows it at 20%
        rig.Run(1000);
        Check.Eq(rig.OnPlayer.Count, 1, "the fresh read refills it");
    }

    /// <summary>
    /// Twelve hours with the macro running and nothing else to do. The mace burns 1 mana every 5 s
    /// (8,640 over the night, it starts at 4,500 of 5,000); its read changes only when it is
    /// identified, or never (identify: false). The server side follows ACE: "low on Mana" two
    /// minutes before empty, a charge gives what the gear needs and is destroyed.
    /// </summary>
    private static void Overnight(bool identify)
    {
        var charges = new (uint, int?)[10];
        for (int i = 0; i < charges.Length; i++) charges[i] = (0x80400100u + (uint)i, 1000);
        using var rig = new Rig(tapping: false, charges, stoneName: ManaCharge, maceMana: 4500, identify: identify);
        const int max = 5000;
        int real = 4500, lowest = real, idsSeen = 0;
        double acc = 0;
        bool warned = false;
        const long step = 500;
        for (long t = 0; t < 12L * 3600 * 1000; t += step)
        {
            rig.Now += step;
            acc += 0.2 * step / 1000.0;
            if (acc >= 1) { int burn = Math.Min((int)acc, real); real -= burn; acc -= (int)acc; }
            lowest = Math.Min(lowest, real);
            if (real * 5 <= 120) { if (!warned) { warned = true; rig.Chat(real > 0 ? "Your Khanda-handled Mace is low on Mana." : "Your Khanda-handled Mace is out of Mana."); } }
            else warned = false;

            int usesBefore = FakeHost.UsesOn.Count;
            rig.M.OnHeartbeat(0);

            for (; idsSeen < FakeHost.IdRequests.Count; idsSeen++)
                if (FakeHost.IdRequests[idsSeen] == Mace) FakeHost.Ints[(Mace, CurManaKey)] = real;   // the appraisal lands

            for (int u = usesBefore; u < FakeHost.UsesOn.Count; u++)
            {
                var (src, target) = FakeHost.UsesOn[u];
                if (target != Player) continue;
                int charge = FakeHost.Ints.TryGetValue((src, CurManaKey), out int c) ? c : 0;
                int give = Math.Min(max - real, charge);
                if (give <= 0) { rig.Chat("You have no items equipped that need mana."); continue; }
                real += give;
                rig.Chat($"The Mana Stone gives {give:N0} points of mana to the following items: Khanda-handled Mace.\n" +
                         (real >= max ? "Your items are fully charged." : $"You need {max - real:N0} more mana to fully charge your items."));
                rig.Destroy(src);
                rig.Chat("The Mana Stone is destroyed.");
            }
        }
        Check.True(lowest > 0, $"the mace never ran dry (lowest {lowest})");
        Check.True(rig.OnPlayer.Count >= 4, $"charges were used ({rig.OnPlayer.Count})");
        Check.True(rig.OnPlayer.Count <= 10, "no more uses than charges");
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
    private const uint CurManaKey = 107, MaxManaKey = 108, WieldedSlotKey = 10, StackSizeKey = 12;

    private sealed class Rig : IDisposable
    {
        public long Now = 1_000_000;
        public readonly LegacyUiSettings S;
        public readonly ManaStoneManager M;
        public readonly WorldObjectCache C;
        private readonly string _stoneName;

        /// <param name="stones">Stone id and its charge read (null = can't be read).</param>
        /// <param name="identify">The engine can identify objects (RequestId).</param>
        public Rig(bool tapping, (uint Id, int? Mana)[] stones, bool ringToDrain = false,
                   string stoneName = "Moderate Mana Stone", int maceMana = 0, bool identify = true,
                   bool ringArrived = true)
        {
            FakeHost.Reset();
            FakeHost.UseOnCalls = true;
            FakeHost.WeaponCalls = identify;   // RequestId, HasAppraisalData
            FakeHost.Names[Player] = "Stone Tester";
            _stoneName = stoneName;
            foreach (var (id, mana) in stones)
            {
                FakeHost.Names[id] = stoneName;
                FakeHost.Containers[id] = Player;
                SetMana(id, mana);
            }
            FakeHost.Names[Mace] = "Khanda-handled Mace";
            FakeHost.Wielders[Mace] = Player;
            FakeHost.Ints[(Mace, WieldedSlotKey)] = 0x100000;
            FakeHost.Ints[(Mace, MaxManaKey)] = 5000;
            FakeHost.Ints[(Mace, CurManaKey)] = maceMana;   // and it keeps reading that: worn-item mana is an appraisal too
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
            C = FakeHost.MakeCache(host, Player, ids);
            S = new LegacyUiSettings { EnableManaTapping = tapping, ManaTapMinMana = 100 };
            S.IsMacroRunning = true;
            ManaStoneManager.Clock = () => Now;
            M = new ManaStoneManager(host, S, C);
            if (ringToDrain) M.MarkLootedForTap(unchecked((int)Ring), arrived: ringArrived);
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

        /// <summary>A stone or charge arrives in the pack (bought, looted).</summary>
        public void AddStone(uint id, int? mana, int stack = 0)
        {
            FakeHost.Names[id] = _stoneName;
            FakeHost.Containers[id] = Player;
            SetMana(id, mana);
            if (stack > 0) FakeHost.Ints[(id, StackSizeKey)] = stack;
            _ = C[unchecked((int)id)];
        }

        /// <summary>The server destroyed it (used up): it leaves the client and the cache.</summary>
        public void Destroy(uint id)
        {
            FakeHost.Names.Remove(id);
            FakeHost.Containers.Remove(id);
            FakeHost.Ints.Remove((id, CurManaKey));
            FakeHost.Ints.Remove((id, StackSizeKey));
            C.OnDeleteObject(id);
        }

        /// <summary>A ring with mana lands in the pack (looted).</summary>
        public void AddRing()
        {
            FakeHost.Names[Ring] = "Ring";
            FakeHost.Containers[Ring] = Player;
            FakeHost.Ints[(Ring, MaxManaKey)] = 4000;
            FakeHost.Ints[(Ring, CurManaKey)] = 4000;
            _ = C[unchecked((int)Ring)];
        }

        public List<uint> OnPlayer => FakeHost.UsesOn.Where(u => u.Target == Player).Select(u => u.Source).ToList();
        public List<uint> OnRing => FakeHost.UsesOn.Where(u => u.Target == Ring).Select(u => u.Source).ToList();

        public void Dispose() => ManaStoneManager.Clock = static () => Environment.TickCount64;
    }

    // 2026-10-15: the pack filled with items looted to tap. A drain that never completed shelved
    // its item for 5 minutes and the item stopped counting against the empty stones, so the looter
    // took another in its place; and the looter went on opening corpses over the drain.
    private static void OneTapItemAtATime()
    {
        using var rig = new Rig(tapping: true, new (uint, int?)[] { (StoneA, null), (StoneB, null), (StoneC, null) }, maceMana: 5000);
        Check.Eq(rig.M.FreeEmptyStoneSlots(), 1, "three empty stones: one item may be looted to tap");
        Check.False(rig.M.HoldsLootForDrain(), "nothing to drain: the looter goes on");
        rig.AddRing();
        rig.M.MarkLootedForTap(unchecked((int)Ring), arrived: false);
        Check.Eq(rig.M.FreeEmptyStoneSlots(), 0, "the pickup is out: no second item");
        rig.M.MarkLootedForTap(unchecked((int)Ring), arrived: true);
        rig.Run(600);
        Check.True(rig.M.HoldsLootForDrain(), "the ring is in the pack with a stone ready: the looter waits");
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 1, "the ring is drained");
        Check.True(rig.M.HoldsLootForDrain(), "the drain is out: the looter still waits");
        Check.Eq(rig.M.FreeEmptyStoneSlots(), 0, "still no second item");
        rig.Chat("The Mana Stone drains 3,600 points of mana from the Ring.\nThe Ring is destroyed.");
        rig.Destroy(Ring);
        rig.Run(200);
        Check.False(rig.M.HoldsLootForDrain(), "drained: the looter goes on");
        Check.Eq(rig.M.FreeEmptyStoneSlots(), 1, "the next item may be looted");
    }

    private static void NoDrainBeforeArrival()
    {
        using var rig = new Rig(tapping: true, new (uint, int?)[] { (StoneA, null) }, ringToDrain: true, maceMana: 5000, ringArrived: false);
        rig.Run(2000);
        Check.Eq(rig.OnRing.Count, 0, "the pickup is only sent: no drain yet");
        Check.False(rig.M.HoldsLootForDrain(), "the pickup's answer is what the looter waits for, not the drain");
        rig.M.MarkLootedForTap(unchecked((int)Ring), arrived: true);
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 1, "confirmed in the pack: drained");
    }

    private static void FailedDrainKeepsPlace()
    {
        using var rig = new Rig(tapping: true, new (uint, int?)[] { (StoneA, null), (StoneB, null), (StoneC, null) }, ringToDrain: true, maceMana: 5000);
        rig.Run(1000);
        Check.Eq(rig.OnRing.Count, 1, "first try");
        rig.Run(9000);   // no answer: the drain times out
        Check.Eq(rig.M.FreeEmptyStoneSlots(), 0, "the ring keeps its place: nothing else is looted to tap");
        Check.False(rig.M.HoldsLootForDrain(), "waiting to retry does not hold the looter");
        rig.Run(21_000);
        Check.Eq(rig.OnRing.Count, 2, "second try");
        Check.True(rig.OnRing[1] != rig.OnRing[0], "with another stone");
        rig.Run(30_000);
        Check.Eq(rig.OnRing.Count, 3, "third try");
        rig.Run(10_000);
        Check.Eq(rig.M.FreeEmptyStoneSlots(), 1, "given up after three: the next item may be looted");
        rig.Run(60_000);
        Check.Eq(rig.OnRing.Count, 3, "a given-up item is never drained again");
        Check.True(FakeHost.Chat.Any(c => c.Contains("would not drain")), "the player is told");
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
