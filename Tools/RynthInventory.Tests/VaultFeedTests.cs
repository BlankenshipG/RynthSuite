using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Scanning;

namespace RynthInventory.Tests
{
    // The Aelrynth account vault: /mule-data answers, one "~ael1" line a page (Aeshnidae.Mule MuleData.cs).
    internal static partial class Program
    {
        private static string VaultPage(int page, int pages, int count, string items, bool ready = true, string why = "") =>
            ready
                ? "~ael1 {\"t\":\"mule\",\"ready\":true,\"page\":" + page + ",\"pages\":" + pages + ",\"count\":" + count + ",\"max\":1000,\"items\":[" + items + "]}"
                : "~ael1 {\"t\":\"mule\",\"ready\":false,\"why\":\"" + why + "\"}";

        private static (VaultFeed Feed, List<string> Sent) NewVault()
        {
            var sent = new List<string>();
            return (new VaultFeed(t => { sent.Add(t); return true; }), sent);
        }

        private static void VaultTests()
        {
            const string A = "{\"o\":2147483649,\"w\":100,\"n\":\"Acid Assagai\",\"i\":100667000,\"u\":100667001,\"v\":100667002,\"s\":3,\"m\":100,\"y\":1,\"e\":1}";
            const string B = "{\"o\":2147483650,\"w\":101,\"n\":\"Pyreal\",\"i\":100667003,\"y\":8192}";

            // Two pages, asked one after the other, filed as one record.
            {
                (VaultFeed v, List<string> sent) = NewVault();
                Check(v.Ask(1000), "ask starts");
                Eq(sent.Count == 1 ? sent[0] : "?", "/mule-data", "page 1 asked first");
                Check(v.ShouldEat(VaultPage(1, 2, 2, A)), "page 1 is eaten");
                v.Tick(1100, "Tom");
                Eq(sent.Count == 2 ? sent[1] : "?", "/mule-data 2", "page 2 asked when page 1 came");
                Check(v.Record == null, "nothing filed before the last page");
                Check(v.ShouldEat(VaultPage(2, 2, 2, B)), "page 2 is eaten");
                v.Tick(1200, "Tom");
                StorageRecord? r = v.Record;
                Check(r != null && v.TakeChanged() && !v.TakeChanged(), "filed once");
                Eq(r?.Items.Count ?? -1, 2, "both pages' items");
                Eq(r?.Id ?? 0, VaultFeed.VaultId("tom"), "id follows the account, case-blind");
                Check((r?.Id ?? 0) >> 24 == 0xFF, "id is a stand-in, far from real objects");
                Eq(r?.Label ?? "", "Account vault", "label");
                ItemRecord a = r!.Items[0];
                Eq(a.Name, "Acid Assagai", "name"); Eq(a.Icon, 100667000u, "icon"); Eq(a.Underlay, 100667001u, "underlay");
                Eq(a.Overlay, 100667002u, "overlay"); Eq(a.Stack, 3, "stack"); Eq(a.MaxStack, 100, "max stack");
                Eq(a.Place, ItemPlace.Storage, "place");
                Eq(r.Items[1].Stack, 1, "no stack field is a stack of 1");
                Eq(v.Status, VaultFeed.State.Idle, "idle again");
            }

            // Other plugins' lines are left alone.
            {
                (VaultFeed v, _) = NewVault();
                v.Ask(0);
                Check(!v.ShouldEat("~ael1 {\"t\":\"inspect\",\"ok\":true}"), "the Companion's line stays visible");
                Check(!v.ShouldEat("Welcome to Aelrynth"), "ordinary chat stays visible");
                Check(!v.ShouldEat(null), "null is fine");
            }

            // A player imitating the server: their name is in front, or the chat type is theirs.
            {
                (VaultFeed v, _) = NewVault();
                v.Ask(0);
                Check(!v.ShouldEat("Bob says, \"" + VaultPage(1, 1, 1, A) + "\"", 2), "spoken data line is not ours");
                Check(!v.ShouldEat("Bob " + VaultPage(1, 1, 1, A), 12), "an emoted data line is not ours");
                Check(!v.ShouldEat("Bob " + VaultPage(1, 1, 1, A)), "a data line not at the start is not ours, type unknown");
                Check(!v.ShouldEat(VaultPage(1, 1, 1, A), 3), "a data line in a tell is not ours");
                Check(!v.ShouldEat("Bob tells you, \"Unknown command: mule-data\"", 3), "a told unknown-command line is not ours");
                Check(!v.ShouldEat("Unknown command: mule-data", 2), "unknown-command in speech is not ours");
                Check(!v.ShouldEat("Unknown command: mule-data-ish"), "another command's refusal is not ours");
                Check(v.ShouldEat("Unknown command: mule-data", 15), "ACE's real reply (Help) is ours");
            }
            {
                (VaultFeed v, _) = NewVault();
                v.Ask(0);
                Check(v.ShouldEat("  " + VaultPage(1, 1, 1, A), 0), "the server's line (Broadcast), leading space allowed");
            }

            // A server without the command.
            {
                (VaultFeed v, List<string> sent) = NewVault();
                v.Ask(0);
                Check(v.ShouldEat("Unknown command: /mule-data"), "the unknown-command reply is eaten while we wait");
                v.Tick(100, "tom");
                Eq(v.Status, VaultFeed.State.Unsupported, "unsupported");
                Check(!v.Ask(200), "not asked again");
                Eq(sent.Count, 1, "one probe, no more");
                Check(v.Ask(300, force: true), "/ginv vault asks anyway");
            }
            {
                (VaultFeed v, _) = NewVault();
                Check(!v.ShouldEat("Unknown command: /mule-data"), "an unknown-command line we did not provoke is left alone");
            }

            // A vault the server is still loading is asked again, and never recorded as empty.
            {
                (VaultFeed v, List<string> sent) = NewVault();
                v.Ask(0);
                v.ShouldEat(VaultPage(1, 1, 0, "", ready: false, why: "loading"));
                v.Tick(10, "tom");
                Eq(v.Status, VaultFeed.State.Waiting, "waiting");
                v.Tick(2000, "tom");
                Eq(sent.Count, 1, "not before the retry delay");
                v.Tick(3500, "tom");
                Eq(sent.Count, 2, "asked again after it");
                Check(v.Record == null, "never recorded");
                for (int i = 0; i < 10; i++)
                {
                    v.ShouldEat(VaultPage(1, 1, 0, "", ready: false, why: "loading"));
                    v.Tick(4000 + i * 4000, "tom");
                }
                Check(v.Status != VaultFeed.State.Asking || sent.Count < 12, "it gives up after a few tries");
                Check(v.Record == null, "still never recorded");
            }

            // A vault that changed between pages is read again, not filed half and half.
            {
                (VaultFeed v, _) = NewVault();
                v.Ask(0);
                v.ShouldEat(VaultPage(1, 2, 3, A));
                v.Tick(10, "tom");
                v.ShouldEat(VaultPage(2, 2, 3, B));   // 2 read, the server says 3
                v.Tick(20, "tom");
                Check(v.Record == null, "a mismatched read is not filed");
                Eq(v.Status, VaultFeed.State.Waiting, "and is retried");
            }

            // Silence is an idle feed, not a stuck one.
            {
                (VaultFeed v, _) = NewVault();
                v.Ask(0);
                v.Tick(20_000, "tom");
                Eq(v.Status, VaultFeed.State.Idle, "no answer in time");
                Check(v.Ask(21_000), "can ask again");
            }

            // A line typed by hand while we are not asking is eaten (it is ours) but changes nothing.
            {
                (VaultFeed v, _) = NewVault();
                Check(v.ShouldEat(VaultPage(1, 1, 1, A)), "eaten");
                v.Tick(0, "tom");
                Check(v.Record == null, "ignored when we did not ask");
            }

            // Garbage inside a mule line does not throw.
            {
                (VaultFeed v, _) = NewVault();
                v.Ask(0);
                v.ShouldEat("~ael1 {\"t\":\"mule\",\"ready\":tru");
                v.Tick(5, "tom");
                Check(v.Record == null && v.Status == VaultFeed.State.Idle, "bad JSON drops the read");
            }
        }
    }
}
