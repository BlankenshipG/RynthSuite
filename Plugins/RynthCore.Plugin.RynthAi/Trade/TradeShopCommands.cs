// /ra tradeshop ... — enable, list, add selected / typed listing, set rates help.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private TradeShopManager? _tradeShop;

    /// <summary>/ra tradeshop [on|off|status|list|add|addids|advertise|rates|mode1|mode2|ui]</summary>
    private void HandleTradeShopRaCommand(string[] parts)
    {
        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3).Trim() : string.Empty;
        var s = _dashboard?.Settings;
        if (s == null)
        {
            ReplyFail("[RynthAi] Settings aren't ready.");
            return;
        }

        switch (verb)
        {
            case "ui":
            case "window":
            case "open":
                if (rest is "off" or "0" or "false" or "close")
                    s.ShowTradeShopWindow = false;
                else if (rest is "on" or "1" or "true")
                    s.ShowTradeShopWindow = true;
                else
                    s.ShowTradeShopWindow = !s.ShowTradeShopWindow; // bare / toggle
                ReplyOk(s.ShowTradeShopWindow
                    ? "[RynthAi] Trade Shop window open (prices + conversion rates)."
                    : "[RynthAi] Trade Shop window closed.");
                break;
            case "on":
            case "enable":
                s.TradeShopEnabled = true;
                ReplyOk("[RynthAi] Trade Shop ON.");
                break;
            case "off":
            case "disable":
                s.TradeShopEnabled = false;
                ReplyOk("[RynthAi] Trade Shop OFF.");
                break;
            case "test":
                s.TradeShopTestMode = rest is not ("off" or "0" or "false");
                ReplyOk($"[RynthAi] Trade Shop test mode {(s.TradeShopTestMode ? "ON" : "OFF")}.");
                break;
            case "mode1":
            case "stock":
                s.TradeShopMode = TradeShopMode.Mode1Stock;
                ReplyOk("[RynthAi] Trade Shop Mode 1 (stock + trade window + !buy tells).");
                break;
            case "mode2":
            case "text":
                s.TradeShopMode = TradeShopMode.Mode2TextMultiAccount;
                ReplyOk("[RynthAi] Trade Shop Mode 2 (text / multi-account listings).");
                break;
            case "list":
            case "listings":
                ListTradeShop(s);
                break;
            case "add":
                AddTradeShopListing(s, rest);
                break;
            case "addids":
            case "addmulti":
                AddTradeShopIds(s, rest);
                break;
            case "advertise":
            case "ad":
                HandleTradeShopAdvertise(s, rest);
                break;
            case "rates":
                ReplyOk("[RynthAi] " + (s.TradeShopRates ?? new TradeShopRates()).RatesHelpLine());
                break;
            case "status":
            default:
                string st = _tradeShop?.Status ?? "not ready";
                string bank = _tradeShop != null && _tradeShop.BankingSupported
                    ? (s.TradeShopBankingHook ? "banking hook ON" : "banking available (hook off)")
                    : "no banking on this world";
                string ad = s.TradeShopAdvertiseEnabled
                    ? $"ad {TradeShopAdvertise.DisplayName(s.TradeShopAdvertiseChannel)} every {TradeShopAdvertise.ClampIntervalSec(s.TradeShopAdvertiseIntervalSec)}s"
                    : "ad off";
                ReplyOk($"[RynthAi] Trade Shop: {(s.TradeShopEnabled ? "ON" : "OFF")} {s.TradeShopMode} | {st} | {bank} | {ad}");
                break;
        }
    }

    /// <summary>/ra tradeshop advertise [on|off|now|channel Trade|interval 120|prefix text]</summary>
    private void HandleTradeShopAdvertise(LegacyUi.LegacyUiSettings s, string rest)
    {
        if (rest.Length == 0 || rest is "status")
        {
            ReplyOk(s.TradeShopAdvertiseEnabled
                ? $"[RynthAi] Advertise ON → {TradeShopAdvertise.DisplayName(s.TradeShopAdvertiseChannel)} every {TradeShopAdvertise.ClampIntervalSec(s.TradeShopAdvertiseIntervalSec)}s."
                : "[RynthAi] Advertise OFF. Usage: /ra tradeshop advertise on|off|now|channel Trade|interval 180|prefix WTS");
            return;
        }
        string[] bits = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string v = bits[0].ToLowerInvariant();
        string arg = bits.Length > 1 ? bits[1].Trim() : "";
        switch (v)
        {
            case "on":
            case "1":
            case "true":
                s.TradeShopAdvertiseEnabled = true;
                ReplyOk($"[RynthAi] Advertise ON ({TradeShopAdvertise.DisplayName(s.TradeShopAdvertiseChannel)}, every {TradeShopAdvertise.ClampIntervalSec(s.TradeShopAdvertiseIntervalSec)}s).");
                break;
            case "off":
            case "0":
            case "false":
                s.TradeShopAdvertiseEnabled = false;
                ReplyOk("[RynthAi] Advertise OFF.");
                break;
            case "now":
            case "send":
                if (_tradeShop == null) { ReplyFail("[RynthAi] Trade Shop not ready."); break; }
                string body = _tradeShop.BuildAdvertiseMessage();
                string prefix = TradeShopAdvertise.SendPrefix(s.TradeShopAdvertiseChannel);
                if (Host.HasInvokeChatParser) Host.InvokeChatParser(prefix + body);
                ReplyOk($"[RynthAi] Sent advertise on {TradeShopAdvertise.DisplayName(s.TradeShopAdvertiseChannel)}.");
                break;
            case "channel":
            case "window":
            case "chat":
                if (!TradeShopAdvertise.TryParseChannel(arg, out var ch))
                {
                    ReplyFail("[RynthAi] Channel: Trade, General, LFG, Fellowship, Allegiance, Local, Roleplay, Society.");
                    break;
                }
                s.TradeShopAdvertiseChannel = ch;
                ReplyOk($"[RynthAi] Advertise channel → {TradeShopAdvertise.DisplayName(ch)}.");
                break;
            case "interval":
            case "freq":
            case "every":
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
                {
                    ReplyFail("[RynthAi] Usage: /ra tradeshop advertise interval <seconds> (30–3600).");
                    break;
                }
                s.TradeShopAdvertiseIntervalSec = TradeShopAdvertise.ClampIntervalSec(sec);
                ReplyOk($"[RynthAi] Advertise every {s.TradeShopAdvertiseIntervalSec}s.");
                break;
            case "prefix":
                s.TradeShopAdvertisePrefix = arg;
                ReplyOk(arg.Length > 0
                    ? $"[RynthAi] Advertise prefix set to \"{arg}\"."
                    : "[RynthAi] Advertise prefix cleared.");
                break;
            default:
                ReplyFail("[RynthAi] advertise on|off|now|channel <name>|interval <sec>|prefix <text>");
                break;
        }
    }

    /// <summary>/ra tradeshop addids &lt;id,id,...&gt; [pricePy] — multi-select from Inventory menu.</summary>
    private void AddTradeShopIds(LegacyUi.LegacyUiSettings s, string rest)
    {
        s.TradeShopListings ??= new();
        if (string.IsNullOrWhiteSpace(rest))
        {
            ReplyFail("[RynthAi] Usage: /ra tradeshop addids <id>[,id...] [pricePy]");
            return;
        }

        long price = 1;
        var idTokens = new List<string>();
        foreach (string tok in rest.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            idTokens.Add(tok.Trim());

        // Optional trailing price: last token is decimal and every prior token parses as an object id.
        if (idTokens.Count >= 2
            && !idTokens[^1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(idTokens[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long trailing)
            && trailing > 0
            && idTokens.Take(idTokens.Count - 1).All(t => TryParseObjectId(t, out _)))
        {
            price = trailing;
            idTokens.RemoveAt(idTokens.Count - 1);
        }

        int added = 0;
        foreach (string tok in idTokens)
        {
            if (!TryParseObjectId(tok, out uint id) || id == 0) continue;
            WorldObject? wo = _objectCache?[(int)id];
            string name = !string.IsNullOrWhiteSpace(wo?.Name) ? wo!.Name : $"0x{id:X8}";
            // Skip duplicates of the same ObjectId still listed.
            if (s.TradeShopListings.Any(l => l != null && l.ObjectId == id))
                continue;
            s.TradeShopListings.Add(new TradeShopListing
            {
                Sku = "S" + (s.TradeShopListings.Count + 1).ToString(CultureInfo.InvariantCulture),
                Name = name!,
                PricePyreals = price,
                ObjectId = id,
                Enabled = true,
            });
            added++;
        }
        ReplyOk(added > 0
            ? $"[RynthAi] Added {added} listing(s) at {price:N0}p each (edit prices in Trade Shop window)."
            : "[RynthAi] No new ids added (check selection / duplicates).");
    }

    private static bool TryParseObjectId(string tok, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(tok)) return false;
        string t = tok.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
        // Decimal object ids from the Inventory menu.
        return uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    private void ListTradeShop(LegacyUi.LegacyUiSettings s)
    {
        var list = s.TradeShopListings;
        if (list == null || list.Count == 0)
        {
            ReplyOk("[RynthAi] Trade Shop catalog is empty.");
            return;
        }
        var rates = s.TradeShopRates;
        ReplyOk($"[RynthAi] Trade Shop ({list.Count}):");
        for (int i = 0; i < list.Count; i++)
        {
            var row = list[i];
            if (row == null) continue;
            ReplyOk($"  #{i + 1} {row.Line(rates)}");
        }
    }

    private void AddTradeShopListing(LegacyUi.LegacyUiSettings s, string rest)
    {
        s.TradeShopListings ??= new();
        // add <price> [name...]  — uses selected item when name omitted
        // add <price> name|sku=X|acct=Y
        long price = 0;
        string name = "";
        string sku = "";
        string acct = "";
        string desc = "";

        if (rest.Length > 0)
        {
            string[] bits = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (!long.TryParse(bits[0].Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out price) || price < 0)
            {
                ReplyFail("[RynthAi] Usage: /ra tradeshop add <pricePy> [name]  (or select an item and omit name).");
                return;
            }
            if (bits.Length > 1) name = bits[1].Trim();
        }

        uint objectId = 0;
        int weenieId = 0;
        if (name.Length == 0 && Host.HasGetSelectedItemId)
        {
            uint sel = Host.GetSelectedItemId();
            if (sel != 0 && _objectCache != null)
            {
                WorldObject? wo = _objectCache[(int)sel];
                if (wo != null)
                {
                    name = wo.Name ?? "";
                    objectId = sel;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name) && price <= 0)
        {
            ReplyFail("[RynthAi] Select an item or pass: /ra tradeshop add <pricePy> <name>.");
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) name = "(unnamed)";
        if (price <= 0)
        {
            ReplyFail("[RynthAi] Price must be > 0 pyreals.");
            return;
        }

        // Optional trailing tags: sku=FOO acct=Toon
        ParseListingTags(ref name, ref sku, ref acct, ref desc);

        if (sku.Length == 0)
            sku = "S" + (s.TradeShopListings.Count + 1).ToString(CultureInfo.InvariantCulture);

        var row = new TradeShopListing
        {
            Sku = sku,
            Name = name,
            Description = desc,
            PricePyreals = price,
            ObjectId = objectId,
            WeenieId = weenieId,
            LocatedOnAccount = acct,
            Enabled = true,
        };
        s.TradeShopListings.Add(row);
        ReplyOk($"[RynthAi] Listed {row.Line(s.TradeShopRates)}");
    }

    private static void ParseListingTags(ref string name, ref string sku, ref string acct, ref string desc)
    {
        // Pull sku=/acct=/desc= tokens out of the free-text name.
        string[] tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new System.Collections.Generic.List<string>();
        foreach (string t in tokens)
        {
            if (t.StartsWith("sku=", StringComparison.OrdinalIgnoreCase))
                sku = t.Substring(4);
            else if (t.StartsWith("acct=", StringComparison.OrdinalIgnoreCase))
                acct = t.Substring(5);
            else if (t.StartsWith("desc=", StringComparison.OrdinalIgnoreCase))
                desc = t.Substring(5);
            else
                kept.Add(t);
        }
        name = string.Join(" ", kept);
    }
}
