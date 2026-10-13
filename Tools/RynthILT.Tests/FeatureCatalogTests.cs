using System;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// Leaftide feature list (IltFeatureCatalog) and the "RynthILT.Server" profile it builds: every
// feature the player asked RynthILT to cover is listed, states follow the server bits, and the JSON
// other plugins read (RynthOracle's Fellowship tab) is valid ASCII with the fellowship rules.
internal static class FeatureCatalogTests
{
    public static void Register(Runner r)
    {
        r.Add("features: banking, guardian, attributes, augs, paragon x3, split arrows, T11 and fellowship are listed", Listed);
        r.Add("features: state follows the server bit on ILT, built-ins are on, nothing is known off ILT", States);
        r.Add("features: profile JSON is valid ASCII with features and the fellowship rules", ProfileJson);
        r.Add("features: the interface copies the JSON only when it fits, NUL-terminated", InterfaceWrite);
        r.Add("features: the /qb list rides the profile JSON on ILT worlds only", QuestBonusJson);
    }

    private static void QuestBonusJson()
    {
        var qb = new IltQuestBonusInfo(false, 2, 41, "2 completed, 1 incomplete",
                                       new[] { "flag\"one", "flagTwo" }, new[] { "flagThree" });
        string json = IltFeatureCatalog.BuildProfileJson(true, "Infinite Leaftide", false, "probe", DateTime.MinValue, _ => IltTri.On, qb);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement q = doc.RootElement.GetProperty("questBonus");
        Check.False(q.GetProperty("disabled").GetBoolean(), "enabled");
        Check.Eq(q.GetProperty("generation").GetInt32(), 2, "generation");
        Check.Eq(q.GetProperty("count").GetInt64(), 41L, "count");
        Check.Eq(q.GetProperty("status").GetString(), "2 completed, 1 incomplete", "status");
        Check.Eq(q.GetProperty("completed").GetArrayLength(), 2, "completed flags");
        Check.Eq(q.GetProperty("completed")[0].GetString(), "flag\"one", "quotes in a flag survive");
        Check.Eq(q.GetProperty("incomplete")[0].GetString(), "flagThree", "incomplete flags");
        Check.True(doc.RootElement.TryGetProperty("fellowship", out _), "the fellowship rules are still there");

        string off = IltFeatureCatalog.BuildProfileJson(false, "", false, "", DateTime.MinValue, _ => IltTri.On, qb);
        using JsonDocument d2 = JsonDocument.Parse(off);
        Check.False(d2.RootElement.TryGetProperty("questBonus", out _), "off ILT: no /qb list");
    }

    private static readonly string[] Requested =
    {
        "bank", "guardian", "attributes", "augmentations", "paragon_armor", "paragon_weapons",
        "paragon_jewelry", "split_arrows", "t11", "fellowship_large", "zone_share", "fellowship_blackjack",
    };

    private static void Listed()
    {
        foreach (string key in Requested)
            Check.NotNull(IltFeatureCatalog.Find(key), "listed: " + key);
        Check.Null(IltFeatureCatalog.Find("no_such_feature"), "an unknown key is not found");

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IltFeatureInfo f in IltFeatureCatalog.All)
        {
            Check.True(seen.Add(f.Key), "keys are unique: " + f.Key);
            Check.True(Array.IndexOf(IltFeatureCatalog.Groups, f.Group) >= 0, $"{f.Key} is in a known section ({f.Group})");
            Check.True(f.Label.Length > 0 && f.Where.Length > 0 && f.Description.Length > 0, f.Key + " has a label, a place and a description");
        }
    }

    private static void States()
    {
        IltFeatureInfo bank = IltFeatureCatalog.Find("bank")!;
        IltFeatureInfo guardian = IltFeatureCatalog.Find("guardian")!;
        Check.Eq(IltFeatureCatalog.StateOf(bank, true, _ => IltTri.On), IltTri.On, "bank on when the server answers /bank");
        Check.Eq(IltFeatureCatalog.StateOf(bank, true, _ => IltTri.Off), IltTri.Off, "bank off when the server says so");
        Check.Eq(IltFeatureCatalog.StateOf(bank, true, _ => IltTri.Unknown), IltTri.Unknown, "bank unknown before the check");
        Check.Eq(IltFeatureCatalog.StateOf(guardian, true, _ => IltTri.Off), IltTri.On, "a built-in feature is on with the ILT world");
        Check.Eq(IltFeatureCatalog.StateOf(guardian, false, _ => IltTri.On), IltTri.Unknown, "nothing is claimed off ILT");
        Check.Eq(IltFeatureCatalog.StateText(IltTri.Unknown), "checking", "unknown reads 'checking'");
        Check.Eq(IltFeatureCatalog.FellowshipMax(true), 29, "ILT fellowships hold 29");
        Check.Eq(IltFeatureCatalog.FellowshipMax(false), 9, "retail fellowships hold 9");
    }

    private static void ProfileJson()
    {
        string json = IltFeatureCatalog.BuildProfileJson(true, "Infinite \"Leaftide\"", false, "probe",
            new DateTime(2026, 10, 13, 21, 25, 37, DateTimeKind.Utc),
            key => key == IltFeature.FBlackjack ? IltTri.Off : IltTri.On);
        bool ascii = true;
        foreach (char c in json) if (c < 0x20 || c > 0x7E) ascii = false;
        Check.True(ascii, "plain printable ASCII");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.True(root.GetProperty("ilt").GetBoolean(), "ilt");
        Check.Eq(root.GetProperty("world").GetString(), "Infinite \"Leaftide\"", "quotes in the world name survive");
        Check.Eq(root.GetProperty("checked").GetString(), "2026-10-13T21:25:37Z", "checked time in UTC");
        Check.Eq(root.GetProperty("features").GetArrayLength(), IltFeatureCatalog.All.Length, "every feature in the JSON");
        JsonElement fel = root.GetProperty("fellowship");
        Check.Eq(fel.GetProperty("maxMembers").GetInt32(), 29, "fellowship size");
        Check.Eq(fel.GetProperty("zoneShare").GetInt32(), 1, "Zone Share on (built in)");
        Check.Eq(fel.GetProperty("blackjack").GetInt32(), 0, "Fellowship Blackjack off (server bit)");

        string off = IltFeatureCatalog.BuildProfileJson(false, "", false, "", DateTime.MinValue, _ => IltTri.On);
        using JsonDocument d2 = JsonDocument.Parse(off);
        Check.Eq(d2.RootElement.GetProperty("fellowship").GetProperty("maxMembers").GetInt32(), 9, "off ILT: retail size");
        Check.Eq(d2.RootElement.GetProperty("checked").GetString(), "", "never checked: empty time");
    }

    private static unsafe void InterfaceWrite()
    {
        const string json = "{\"ilt\":true}";
        byte* small = stackalloc byte[4];
        small[0] = 0x55;
        Check.Eq(ServerInterface.Write(json, small, 4), json.Length, "too small: the length is returned");
        Check.Eq(small[0], (byte)0x55, "too small: nothing written");

        byte* exact = stackalloc byte[json.Length];
        Check.Eq(ServerInterface.Write(json, exact, json.Length), json.Length, "no room for the NUL: too small");

        byte* big = stackalloc byte[64];
        int n = ServerInterface.Write(json, big, 64);
        Check.Eq(n, json.Length, "fits: the length without the NUL");
        Check.Eq(big[n], (byte)0, "NUL-terminated");
        Check.Eq(Encoding.UTF8.GetString(big, n), json, "the JSON copied whole");
    }
}
