// One priced item on the Trade Shop catalog (Mode 1 = stock on this character; Mode 2 = text listing).
using System;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>
/// How the shop fulfills a sale.
/// Mode1Stock: the item must be in this character's pack; payment → add item → accept.
/// Mode2TextMultiAccount: catalog is description-only (other accounts hold stock); payment → accept;
/// the buyer is told the listing text (fulfillment across accounts is outside this client).
/// </summary>
public enum TradeShopMode
{
    Mode1Stock = 0,
    Mode2TextMultiAccount = 1,
}

/// <summary>One sellable row in the Trade Shop.</summary>
public sealed class TradeShopListing
{
    /// <summary>Short code for tells ("buy SKU") and the UI list.</summary>
    public string Sku = "";
    /// <summary>Display name (Mode 1: from the item; Mode 2: typed).</summary>
    public string Name = "";
    /// <summary>Mode 2 (and Mode 1 notes): what the buyer is told.</summary>
    public string Description = "";
    /// <summary>Price in pyreals (trade notes / converted currencies count via TradeShopRates).</summary>
    public long PricePyreals;
    /// <summary>Mode 1: last known object id in pack (0 = resolve by weenie/name).</summary>
    public uint ObjectId;
    /// <summary>Mode 1: weenie type id when known (helps re-find after logout).</summary>
    public int WeenieId;
    /// <summary>Mode 2: which account / toon holds the stock (informational).</summary>
    public string LocatedOnAccount = "";
    /// <summary>False = listed but not for sale.</summary>
    public bool Enabled = true;

    public string PriceLabel() => PricePyreals <= 0 ? "(no price)" : PricePyreals.ToString("N0") + "p";

    /// <summary>Price plus conversion alternatives when <paramref name="rates"/> is provided.</summary>
    public string PriceLabel(TradeShopRates? rates)
    {
        if (PricePyreals <= 0) return "(no price)";
        if (rates == null) return PriceLabel();
        string alt = rates.FormatAlternatives(PricePyreals);
        return alt.Length > 0 ? alt : PriceLabel();
    }

    public string Line() =>
        (Sku.Length > 0 ? Sku + " — " : "") + (Name.Length > 0 ? Name : "(unnamed)") + " @ " + PriceLabel()
        + (Enabled ? "" : " [off]");

    public string Line(TradeShopRates? rates) =>
        (Sku.Length > 0 ? Sku + " — " : "") + (Name.Length > 0 ? Name : "(unnamed)") + " @ " + PriceLabel(rates)
        + (LocatedOnAccount.Length > 0 ? " @" + LocatedOnAccount : "")
        + (Enabled ? "" : " [off]");
}
