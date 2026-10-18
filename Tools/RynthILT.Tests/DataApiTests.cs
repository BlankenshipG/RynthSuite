using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// "RynthILT.Data" v1 (Plugins/Shared/RynthIltDataApi.cs, DataInterface.cs): the bank JSON RynthILT
// builds (Hub/IltBankExport.cs) read back by the shared reader, the bank verb lines a reader sends
// (IltBanking.RunVerb splits them), and the table itself. The charms JSON is MoveTests'
// CharmsReaderJson. No plugin in this tree reads the interface yet.
internal static class DataApiTests
{
    public static void Register(Runner r)
    {
        r.Add("data: the bank JSON reads back whole: characters (current first), choices, notes, log", BankRoundTrip);
        r.Add("data: the bank verb lines carry their fields in RunVerb's order", BankVerbs);
        r.Add("data: bad bank JSON reads as nothing", BankBadJson);
        r.Add("data: the Temple guardian JSON reads back whole (switches, answer, hand-in, rows)", GuardianRoundTrip);
        // Last: Release() switches the table off for the rest of the process.
        r.Add("data: the RynthILT.Data table: query, functions, release", DataTable);
    }

    private static void BankRoundTrip()
    {
        var me = new IltBankState { Pyreals = 1_500_000, Luminance = 42, PrestigeCoins = 3,
                                    LastUpdated = new DateTime(2026, 10, 13, 21, 25, 37, DateTimeKind.Utc).ToLocalTime() };
        var alt = new IltBankState { Pyreals = 7, LegendaryKeys = 2, LastUpdated = DateTime.Now };
        var tx = new List<IltBankTransaction>();
        for (int i = 0; i < IltBankExport.MaxTransactions + 5; i++)
            tx.Add(new IltBankTransaction { Timestamp = new DateTime(2026, 10, 1).AddMinutes(i), Type = i % 2 == 0 ? "Sent" : "Received",
                                            Currency = "Pyreals", Amount = i, OtherPlayer = "Bob \"the\" Builder" });
        string json = IltBankExport.Build(true, 1, "InfiniteLeaftide", "Zed",
            new[] { new IltBankExport.Row("Alt", false, alt), new IltBankExport.Row("Zed", true, me) },
            "Deposited 5 Pyreals", tx);
        Check.True(json.All(c => c >= 0x20 && c <= 0x7E), "plain printable ASCII");

        IltBankView v = IltBankView.Parse(json);
        Check.True(v.Loaded && v.Available, "loaded, ILT");
        Check.Eq(v.Enabled, 1, "the server has /bank");
        Check.Eq(v.World, "InfiniteLeaftide", "world");
        Check.Eq(v.Character, "Zed", "character");
        Check.Eq(v.Result, "Deposited 5 Pyreals", "last result");
        Check.Eq(v.Characters.Count, 2, "both characters");
        Check.True(v.Characters[0].Current && v.Characters[0].Name == "Zed", "the current character first");
        Check.Eq(v.Characters[0].Pyreals, 1_500_000L, "pyreals");
        Check.Eq(v.Characters[0].PrestigeCoins, 3L, "prestige coins");
        Check.Eq(v.Characters[0].UpdatedUtc, new DateTime(2026, 10, 13, 21, 25, 37, DateTimeKind.Utc), "updated, in UTC");
        Check.Eq(v.Characters[1].LegendaryKeys, 2L, "the other character's keys");

        Check.Eq(v.Deposits.Count, IltBankCommands.Deposits.Length, "every deposit choice");
        Check.Eq(v.Withdrawals.Count, IltBankCommands.Withdrawals.Length, "every withdraw choice");
        Check.Eq(v.Transfers.Count, IltBankCommands.Transfers.Length, "every transfer choice");
        Check.True(v.Transfers.Single(t => t.Notes).Label == "Trade notes", "only trade notes count notes");
        Check.Eq(v.Notes.Count, IltBankCommands.NoteDenominations.Length, "every note denomination");
        Check.Eq(v.Notes[^1].Value, 250_000L, "MMD is 250K");

        Check.Eq(v.Transactions.Count, IltBankExport.MaxTransactions, "the log is capped");
        Check.Eq(v.Transactions[0].Amount, (long)(IltBankExport.MaxTransactions + 4), "newest first");
        Check.Eq(v.Transactions[0].Player, "Bob \"the\" Builder", "quotes in a name survive");
    }

    private static void BankVerbs()
    {
        Check.Eq(IltBankView.RefreshVerb(), "refresh", "refresh");
        Check.Eq(IltBankView.DepositVerb(3), "deposit 3", "deposit");
        Check.Eq(IltBankView.WithdrawVerb(7, 4, 9), "withdraw 7 4 9", "withdraw: index, amount, note");
        Check.Eq(IltBankView.TransferVerb(6, 2, 9, "  Some Name "), "transfer 6 2 9 Some Name", "transfer: the name last, trimmed");
        foreach (string verb in new[] { IltBankView.RefreshVerb(), IltBankView.DepositVerb(0), IltBankView.WithdrawVerb(0, 1, -1),
                                        IltBankView.TransferVerb(0, 1, -1, "Bob") })
            Check.True(verb.All(c => c >= 0x20 && c <= 0x7E), "ASCII verb (DataInterface refuses anything else): " + verb);
    }

    private static void BankBadJson()
    {
        Check.True(ReferenceEquals(IltBankView.Parse(null), IltBankView.None), "null");
        Check.True(ReferenceEquals(IltBankView.Parse("{not json"), IltBankView.None), "broken");
        Check.True(ReferenceEquals(IltBankView.Parse("[1,2]"), IltBankView.None), "not an object");
        IltBankView empty = IltBankView.Parse("{}");
        Check.True(empty.Loaded && !empty.Available && empty.Enabled == -1 && empty.Characters.Count == 0, "missing fields read as nothing known");
    }

    /// <summary>
    /// Version 3: the guardian JSON RynthILT builds (IltGuardian.BuildJson) read back by the reader
    /// RynthOracle's Quests tab uses, quotes and all.
    /// </summary>
    private static void GuardianRoundTrip()
    {
        var rows = new[]
        {
            new IltTempleAttrStatus("Strength", "Fiun \"Big\" Strongarm", 3, "Ready", "", true, true),
            new IltTempleAttrStatus("Focus", "Fiun Thinker", 1, "Cooldown", "in 5h 2m", false, false),
        };
        string json = IltGuardian.BuildJson("InfiniteLeaftide", chat: true, auto: false, buy: true, cooldownHours: 20,
            answer: "Pyreal", npc: "Guardian of Attribute Enlightenment", answerAgeSec: 42, heard: "What is \"coin\"?",
            running: true, status: "handing Pyreal to Guardian...", syncedUtc: new DateTime(2026, 10, 18, 19, 5, 0, DateTimeKind.Utc), rows);
        Check.True(json.All(c => c >= 0x20 && c <= 0x7E), "plain printable ASCII");

        IltGuardianView v = IltGuardianView.Parse(json);
        Check.True(v.Loaded && v.Available && v.World == "InfiniteLeaftide", "loaded, the world");
        Check.True(v.Chat && !v.Auto && v.Buy && v.CooldownHours == 20, "the switches");
        Check.True(v.Answer == "Pyreal" && v.Npc == "Guardian of Attribute Enlightenment" && v.AnswerAgeSec == 42 && v.Heard == "What is \"coin\"?",
                   "the last answer");
        Check.True(v.Running && v.Status.StartsWith("handing Pyreal"), "the hand-in");
        Check.Eq(v.SyncedUtc, new DateTime(2026, 10, 18, 19, 5, 0, DateTimeKind.Utc), "the /qb read time");
        Check.Eq(v.Rows.Count, 2, "both rows");
        Check.True(v.Rows[0].Npc == "Fiun \"Big\" Strongarm" && v.Rows[0].Count == 3 && v.Rows[0].Ready && v.Rows[0].Exact, "an exact, ready row");
        Check.True(v.Rows[1].Eta == "in 5h 2m" && !v.Rows[1].Ready && !v.Rows[1].Exact, "an estimated row on cooldown");

        IltGuardianView none = IltGuardianView.Parse(IltGuardian.BuildJson("x", false, false, false, 1, "", "", -1, "", false, "idle",
            DateTime.MinValue, Array.Empty<IltTempleAttrStatus>()));
        Check.True(none.Answer.Length == 0 && none.AnswerAgeSec == -1 && none.SyncedUtc == DateTime.MinValue && none.Rows.Count == 0,
                   "no answer, no /qb read, no rows");
    }

    private static unsafe void DataTable()
    {
        Check.Eq(DataInterface.Query("RynthILT.Hub", 1), IntPtr.Zero, "another interface's name: not ours");
        Check.Eq(DataInterface.Query(RynthIltDataApiV1.InterfaceName, 4), IntPtr.Zero, "a newer version than we have: none");
        var t = (RynthIltDataApiV1*)DataInterface.Query(RynthIltDataApiV1.InterfaceName, 1);
        Check.True(t != null, "v1 is served");
        Check.Eq(t->Version, 3u, "Version 3 (v1 and v2 readers take it: it starts with their tables)");
        Check.Eq(t->StructSize, (uint)sizeof(RynthIltDataApiV1), "StructSize");
        Check.Eq(sizeof(RynthIltDataApiV1), 8 + 8 * IntPtr.Size, "header + 8 functions (v1's 4, the breeding pair, the guardian pair)");
        Check.Eq(RynthIltDataApiV1.V1Size, (uint)(8 + 4 * IntPtr.Size), "a v1 table ends before the breeding pair");
        Check.Eq(RynthIltDataApiV1.V2Size, (uint)(8 + 6 * IntPtr.Size), "a v2 table ends before the guardian pair (readers still see breeding on it)");
        Check.True(t->GetCharmsJson != null && t->GetBankJson != null && t->ToggleCharm != null && t->BankCommand != null
                   && t->GetPetBreedingJson != null && t->PetBreedingCommand != null
                   && t->GetGuardianJson != null && t->GuardianCommand != null, "every function is filled in");
        Check.Eq((IntPtr)t, DataInterface.Query(RynthIltDataApiV1.InterfaceName, 1), "the same table each time");

        // No plugin instance here (not logged in): nothing to report, every action refused.
        byte* buf = stackalloc byte[64];
        Check.Eq(t->GetCharmsJson(buf, 64), 0, "no charms before login");
        Check.Eq(t->GetBankJson(buf, 64), 0, "no bank before login");
        Check.Eq(t->ToggleCharm(1234), 0, "no toggle before login");
        byte* verb = stackalloc byte[] { (byte)'r', (byte)'e', (byte)'f', (byte)'r', (byte)'e', (byte)'s', (byte)'h', 0 };
        Check.Eq(t->BankCommand(verb), 0, "no bank command before login");
        Check.Eq(t->GetPetBreedingJson(buf, 64), 0, "no breeding JSON before login");
        Check.Eq(t->PetBreedingCommand(verb), 0, "no breeding command before login");
        Check.Eq(t->GetGuardianJson(buf, 64), 0, "no guardian JSON before login");
        Check.Eq(t->GuardianCommand(verb), 0, "no guardian command before login");

        DataInterface.Release();
        Check.Eq(t->Version, 0u, "Release zeroes Version (readers re-query)");
        Check.Eq(DataInterface.Query(RynthIltDataApiV1.InterfaceName, 1), IntPtr.Zero, "and nothing is served after shutdown");
        Check.Eq(t->GetBankJson(buf, 64), 0, "the old table stays callable and answers nothing");
    }
}
