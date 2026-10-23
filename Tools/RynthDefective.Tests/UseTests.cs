using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthDefective.Tests
{
    internal static partial class Program
    {
        private static Harness With(params CarriedItem[] items)
        {
            var h = new Harness();
            h.World.Inventory.AddRange(items);
            return h;
        }

        private static void UseKit()
        {
            Harness h = With(Kit(0x10));
            Check(h.Slot(ItemRef.From(Kit(0x10))), "queued");
            Check(h.Use.HasPending && !h.Use.InFlight, "waiting for the next tick");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "a kit is used on yourself");
            Check(h.Use.InFlight && !h.Use.HasPending, "in flight");
            Check(h.Said.Count == 1 && h.Said[0] == "Using Peerless Healing Kit...", "the status line says so: " + string.Join(";", h.Said));
            Check(h.Logged.Count == 1 && h.Logged[0].Contains("slot") && h.Logged[0].Contains("use on you"), "and the log: " + string.Join(";", h.Logged));

            h.Tick(100);
            Check(h.Use.InFlight, "no answer yet: still in flight");
            h.World.Answer();
            h.Tick(100);
            Check(!h.Use.InFlight && !h.Use.HasPending, "the server's UseDone ends it");
            Eq(h.World.Sent.Count, 1, "nothing else sent");
            Eq(h.Warned.Count, 0, "no warnings");

            // A potion is used by itself.
            h = With(Potion(0x20));
            h.Slot(ItemRef.From(Potion(0x20)));
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "use 20", "a potion is used with UseObject");
        }

        private static void UseQueue()
        {
            Harness h = With(Kit(0x10), Potion(0x20), Item(0x30, "Mana Potion", 3, 100));
            ItemRef kit = ItemRef.From(Kit(0x10)), health = ItemRef.From(Potion(0x20)), mana = ItemRef.From(h.World.Inventory[2]);
            h.Slot(kit);
            h.Tick();
            Check(h.Slot(health), "a second request while the first is in flight waits");
            Check(h.Slot(mana), "a third replaces it: the newest wish wins");
            Check(!h.Slot(mana), "the same request again (a key held down) does not queue twice");
            h.Tick(100);
            Eq(h.World.Sent.Count, 1, "nothing goes out while the first is unanswered");
            h.World.Answer();
            h.Tick(10);
            Eq(h.World.Sent.Count, 1, "and not in the same tick it was answered (a short gap)");
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "useon 10 me|use 30", "then the newest request, not the one it replaced");

            // The same item pressed again while it is in flight is a new use afterwards, once.
            h = With(Kit(0x10));
            h.Slot(kit);
            h.Tick();
            Check(h.Slot(kit), "pressed again during the use: one more is queued");
            Check(!h.Slot(kit), "but only one");
            h.World.Answer();
            h.Tick(10);
            h.Tick(300);
            Eq(h.World.Sent.Count, 2, "so a second kit goes out after the first");
        }

        private static void UseBusy()
        {
            Harness h = With(Kit(0x10));
            ItemRef kit = ItemRef.From(Kit(0x10));
            h.World.ClientBusy = true;
            h.Slot(kit);
            h.Ticks(5, 1000);
            Eq(h.World.Sent.Count, 0, "the client's busy count holds it");
            Check(h.Use.HasPending && h.Warned.Count == 0, "still waiting inside the limit");
            h.Ticks(2, 1000);
            Check(!h.Use.HasPending && h.Warned.Count == 1 && h.Warned[0] == "Peerless Healing Kit: not used, the character is busy.", "past the limit it gives up and says why: " + string.Join(";", h.Warned));

            h = With(Kit(0x10));
            h.World.Animating = true;
            h.Slot(kit);
            h.Ticks(7, 1000);
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("an animation is playing"), "a gesture holds it: " + string.Join(";", h.Warned));

            h = With(Kit(0x10));
            h.World.BotWaitingOnServer = true;
            h.Slot(kit);
            h.Ticks(3, 1000);
            Eq(h.World.Sent.Count, 0, "RynthAi waiting on the server holds it");
            h.World.BotWaitingOnServer = false;
            h.Tick(100);
            Eq(h.World.Sent.Count, 1, "and it goes the moment RynthAi is done");

            // Busy clears in time: sent, no warning.
            h = With(Kit(0x10));
            h.World.ClientBusy = true;
            h.Slot(kit);
            h.Ticks(3, 1000);
            h.World.ClientBusy = false;
            h.Tick(100);
            Check(h.World.Sent.Count == 1 && h.Warned.Count == 0, "busy cleared inside the limit: used");

            // RynthAi absent (null) never holds anything.
            h = With(Kit(0x10));
            h.World.BotWaitingOnServer = null;
            h.Slot(kit);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "no RynthAi: nothing to wait for");
        }

        private static void UseUnanswered()
        {
            Harness h = With(Potion(0x20));
            ItemRef p = ItemRef.From(Potion(0x20));
            h.Slot(p);
            h.Tick();
            h.Tick(1000);
            Check(h.Use.InFlight, "1 s with no UseDone: still waiting");
            h.Tick(600);
            Check(!h.Use.InFlight, "the client is quiet and 1.5 s have passed: let go (a refusal sends no UseDone)");

            h = With(Potion(0x20));
            h.World.Animating = true;
            h.Slot(p);
            h.Tick();   // animating holds the send
            h.World.Animating = false;
            h.Tick(100);
            h.World.ClientBusy = true;
            h.Tick(2000);
            Check(h.Use.InFlight, "busy count up: not let go early");
            h.Tick(6500);
            Check(!h.Use.InFlight, "the 8 s limit lets go");
            Check(h.Logged.Any(l => l.Contains("no UseDone")), "and logs it");
        }

        private static void UseRebind()
        {
            ItemRef r = ItemRef.From(Kit(0x10));
            Harness h = With(Kit(0x11), Potion(0x20));
            h.Slot(r);
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "useon 11 me", "the slot's object is gone: the next kit of that name is used");

            h = With(Potion(0x20));
            h.Slot(r);
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0] == "Peerless Healing Kit: you aren't carrying one.", "none left: says so, sends nothing: " + string.Join(";", h.Warned));
            Check(!h.Use.HasPending, "and doesn't wait for one");
        }

        private static void UseByNameAndTargets()
        {
            Harness h = With(Kit(3, "Lesser Stamina Kit"), Kit(4, "Greater Stamina Kit"));
            h.Named("greater stamina");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "useon 4 me", "by the name typed");

            h = With(Kit(3));
            h.Named("Elixir");
            h.Tick();
            Check(h.Warned.Count == 1 && h.Warned[0] == "No item named 'Elixir' in your pack.", "no such item: " + string.Join(";", h.Warned));

            // Use on the selected target.
            h = With(Potion(0x20, "Mana Charge"));
            ItemRef r = ItemRef.From(h.World.Inventory[0]);
            h.Slot(r, UseMode.OnSelected);
            h.Tick();
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("select a target first") && h.World.Sent.Count == 0, "no target selected: nothing sent");
            h.World.SelectedId = 0x1234;
            h.Slot(r, UseMode.OnSelected);
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "useon 20 1234", "used on the selected object");

            // A kit used by itself when the slot says so.
            h = With(Kit(0x10));
            h.Slot(ItemRef.From(Kit(0x10)), UseMode.Use);
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "use 10", "slot mode 'use it' overrides the kit rule");

            // The client turns the request away.
            h = With(Kit(0x10));
            h.World.RefuseSends = true;
            h.Slot(ItemRef.From(Kit(0x10)));
            h.Tick();
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("didn't take") && !h.Use.InFlight, "a refused send is reported and not waited on");
        }

        private static void UseWield()
        {
            Harness h = With(Melee(0x32, "Sword"));
            ItemRef sword = ItemRef.From(h.World.Inventory[0]);
            h.Slot(sword);
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "use 32", "a weapon is wielded with UseObject (the client's double-click)");
            Check(h.Said[0] == "Wielding Sword...", "status: " + h.Said[0]);
            h.Tick(100);
            Check(h.Use.InFlight && h.Wielded.Count == 0, "not in hand yet: waits for it (a wield sends no UseDone)");
            h.World.Answer();
            h.Tick(100);
            Check(h.Use.InFlight, "a UseDone does not end a wield; the item in hand does");
            h.World.Inventory[0].WieldedLocation = 0x00100000;
            h.Tick(100);
            Check(!h.Use.InFlight && h.Wielded.Count == 1 && h.Wielded[0].Id == 0x32, "in hand: done, and reported (the picker's recent list)");
            Check(h.Said[^1] == "Sword is in your hands.", "status: " + h.Said[^1]);

            // Already in hand: nothing is sent (a use would take it off).
            h = With(Melee(0x32, "Sword", wielded: true));
            h.Slot(ItemRef.From(h.World.Inventory[0]));
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Said.Count == 1 && h.Said[0] == "Sword is already in your hands.", "already wielded: no use sent: " + string.Join(";", h.Said));

            // Armor and rings are worn the same way and take no stance.
            var helm = Item(0x40, "Helm"); helm.ItemType = ItemKinds.TypeArmor; helm.ValidLocations = 1;
            h = With(helm);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(helm));
            h.Tick();
            helm.WieldedLocation = 1;
            h.Tick(100);
            Check(h.World.Sent.SequenceEqual(new[] { "use 40" }) && !h.Use.InFlight, "armor: worn, no stance change");

            // Pressing the weapon again while it is being wielded does not queue another wield.
            h = With(Melee(0x32, "Sword"));
            h.Slot(sword);
            h.Tick();
            Check(!h.Slot(sword) && !h.Use.HasPending, "the same wield again is ignored");
        }

        private static void UseWieldFails()
        {
            Harness h = With(Melee(0x32, "Sword"));
            ItemRef sword = ItemRef.From(h.World.Inventory[0]);
            h.Slot(sword);
            h.Tick();
            h.Tick(3000);
            Check(h.Use.InFlight, "3 s: still waiting");
            h.Tick(600);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("not in your hands after"), "never wielded: reported once and released: " + string.Join(";", h.Warned));
            Eq(h.World.Sent.Count, 1, "no automatic retry loop");

            h = With(Melee(0x32, "Sword"));
            h.Slot(sword);
            h.Tick();
            h.World.RefusalSeq++;
            h.World.RefusedObject = 0x99;
            h.Tick(100);
            Check(h.Use.InFlight, "a refusal about another object is not ours");
            h.World.RefusedObject = 0x32;
            h.World.RefusalSeq++;
            h.Tick(100);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("wouldn't let you wield"), "the server refused this wield: said at once: " + string.Join(";", h.Warned));
        }

        private static void UseBot()
        {
            Harness h = With(Melee(0x32, "Sword"), Kit(0x10));
            h.World.BotMacroRunning = true;
            ItemRef sword = ItemRef.From(h.World.Inventory[0]);
            h.Slot(sword);
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0].Contains("RynthAi's macro is running"), "a weapon is not wielded under RynthAi's macro: " + string.Join(";", h.Warned));

            h.Slot(ItemRef.From(h.World.Inventory[1]));
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "a kit still works (that is the point of hands-on)");

            h = With(Melee(0x32, "Sword"));
            h.World.BotMacroRunning = true;
            h.Use.Settings.SwapWhileBotRuns = true;
            h.Slot(sword);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "allowed in the settings: wielded");

            h = With(Melee(0x32, "Sword"));
            h.World.BotMacroRunning = false;
            h.Slot(sword);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "macro stopped: wielded");

            var helm = Item(0x40, "Helm"); helm.ItemType = ItemKinds.TypeArmor; helm.ValidLocations = 1;
            h = With(helm);
            h.World.BotMacroRunning = true;
            h.Slot(ItemRef.From(helm));
            h.Tick();
            Eq(h.World.Sent.Count, 1, "armor is not a weapon swap: allowed");
        }

        private static void UseStance()
        {
            static void Wield(Harness h, CarriedItem item) => h.World.Inventory.First(i => i.Id == item.Id).WieldedLocation = 0x1;

            Check(Stance.Wanted(ItemKind.Melee, false, false, Stance.NonCombat) == 0, "peace stays peace");
            Check(Stance.Wanted(ItemKind.Melee, false, false, 0) == 0, "mode unknown: left alone");
            Eq(Stance.Wanted(ItemKind.WarWand, false, false, Stance.Melee), Stance.Magic, "a wand from melee: magic");
            Eq(Stance.Wanted(ItemKind.VoidWand, false, false, Stance.Missile), Stance.Magic, "a void wand: magic");
            Eq(Stance.Wanted(ItemKind.Melee, false, false, Stance.Magic), Stance.Melee, "melee from magic");
            Eq(Stance.Wanted(ItemKind.Missile, true, true, Stance.Melee), Stance.Missile, "a bow with arrows in hand: missile");
            Eq(Stance.Wanted(ItemKind.Missile, true, false, Stance.Melee), Stance.NonCombat, "a bow without arrows: peace, not a stance it can't use");
            Eq(Stance.Wanted(ItemKind.Missile, false, false, Stance.Melee), Stance.Missile, "thrown weapons need no ammunition");
            Eq(Stance.Wanted(ItemKind.Shield, false, false, Stance.Melee), 0, "a shield leaves the stance");
            Eq(Stance.Wanted(ItemKind.Armor, false, false, Stance.Magic), 0, "so does armor");

            // Melee -> wand.
            var wand = Wand(0x50, "Wand");
            Harness h = With(wand);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(wand));
            h.Tick();
            Wield(h, wand);
            h.Tick(100);
            Check(h.Use.InFlight && h.World.Sent.Count == 1, "wielded; the stance waits for the character to settle");
            h.Tick(300);
            Eq(h.World.Sent.Count, 1, "still settling");
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "use 50|mode 8", "then magic");
            Check(!h.Use.InFlight && h.Said[^1] == "Wand is in your hands, magic stance.", "done: " + h.Said[^1]);

            // Busy delays the stance; the time limit reports.
            h = With(wand);
            wand.WieldedLocation = 0;
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(wand));
            h.Tick();
            Wield(h, wand);
            h.World.ClientBusy = true;
            h.Tick(100);
            h.Tick(1000);
            Eq(h.World.Sent.Count, 1, "busy: no stance request");
            h.World.ClientBusy = false;
            h.Tick(100);
            Eq(h.World.Sent.Last(), "mode 8", "the stance goes as soon as the character is free");

            h = With(wand);
            wand.WieldedLocation = 0;
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(wand));
            h.Tick();
            Wield(h, wand);
            h.World.Animating = true;
            h.Tick(100);
            h.Tick(4500);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("magic stance wasn't taken"), "never free: reported, not stuck: " + string.Join(";", h.Warned));

            // Magic -> melee weapon.
            var sword = Melee(0x51, "Sword");
            h = With(sword);
            h.World.CombatMode = Stance.Magic;
            h.Slot(ItemRef.From(sword));
            h.Tick();
            Wield(h, sword);
            h.Tick(100);
            h.Tick(600);
            Eq(h.World.Sent.Last(), "mode 2", "from magic to melee");

            // Already in the right stance: no request.
            h = With(sword);
            sword.WieldedLocation = 0;
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(sword));
            h.Tick();
            Wield(h, sword);
            h.Tick(100);
            h.Tick(600);
            Check(h.World.Sent.SequenceEqual(new[] { "use 51" }) && !h.Use.InFlight, "the server already put us in melee: nothing sent");

            // Bow without arrows, then with.
            var bow = Bow(0x52);
            h = With(bow);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(bow));
            h.Tick();
            Wield(h, bow);
            h.Tick(100);
            h.Tick(600);
            Eq(h.World.Sent.Last(), "mode 1", "no arrows in hand: back to peace");
            Check(h.Said[^1].Contains("no ammunition"), "and it says why: " + h.Said[^1]);

            bow.WieldedLocation = 0;
            var arrows = Arrows(0x53, wielded: true);
            h = With(bow, arrows);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(bow));
            h.Tick();
            Wield(h, bow);
            h.Tick(100);
            h.Tick(600);
            Eq(h.World.Sent.Last(), "mode 4", "arrows in hand: missile");

            // At peace the character stays at peace.
            h = With(sword);
            sword.WieldedLocation = 0;
            h.World.CombatMode = Stance.NonCombat;
            h.Slot(ItemRef.From(sword));
            h.Tick();
            Wield(h, sword);
            h.Tick(100);
            Check(h.World.Sent.SequenceEqual(new[] { "use 51" }) && !h.Use.InFlight, "peace: no stance request");

            // A shield from melee keeps melee.
            var shield = Shield(0x54);
            h = With(shield);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(shield));
            h.Tick();
            Wield(h, shield);
            h.Tick(100);
            Check(h.World.Sent.SequenceEqual(new[] { "use 54" }), "a shield: stance untouched");
        }

        private static void UseReset()
        {
            Harness h = With(Kit(0x10));
            ItemRef kit = ItemRef.From(Kit(0x10));
            h.Slot(kit);
            h.Tick();
            h.Slot(kit);
            Check(h.Use.InFlight && h.Use.HasPending, "one in flight, one waiting");
            h.World.InWorld = false;
            h.Tick(100);
            Check(!h.Use.InFlight && !h.Use.HasPending, "portal space / logout: both dropped");
            h.World.InWorld = true;
            h.Tick(1000);
            Eq(h.World.Sent.Count, 1, "and nothing is sent afterwards");

            h = With(Kit(0x10));
            h.World.InWorld = false;
            h.Slot(kit);
            h.Tick();
            Check(!h.Use.HasPending && h.World.Sent.Count == 0, "a request made out of the world is dropped, not held for later");
        }
    }
}
