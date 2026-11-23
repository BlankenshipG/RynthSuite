// Trade Shop ImGui window — catalog prices + bank/payment conversion rates (opened from Inventory).
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>
/// Dedicated Trade Shop editor: list prices, conversion rates, and shop mode.
/// Opened from the Inventory panel Storefront button or <c>/ra tradeshop ui</c>.
/// </summary>
internal sealed class TradeShopWindow
{
    private readonly SavedWindow _spot = new("RynthAi.TradeShop");
    private readonly LegacyUiSettings _settings;
    private readonly Func<string> _status;
    private readonly Func<bool> _bankingSupported;
    private readonly Func<uint> _selectedItemId;
    private readonly Func<WorldObjectCache?> _cache;
    private readonly Action<string>? _runCommand;

    private string _newName = "";
    private string _newSku = "";
    private string _newAcct = "";
    private string _newDesc = "";
    private int _newPrice;

    public TradeShopWindow(
        LegacyUiSettings settings,
        Func<string> status,
        Func<bool> bankingSupported,
        Func<uint> selectedItemId,
        Func<WorldObjectCache?> cache,
        Action<string>? runCommand = null)
    {
        _settings = settings;
        _status = status;
        _bankingSupported = bankingSupported;
        _selectedItemId = selectedItemId;
        _cache = cache;
        _runCommand = runCommand;
    }

    public void Render()
    {
        if (!_settings.ShowTradeShopWindow) return;

        ImGui.SetNextWindowSize(new Vector2(520, 560), ImGuiCond.FirstUseEver);
        _spot.Apply();
        bool open = _settings.ShowTradeShopWindow;
        if (!ImGui.Begin("Trade Shop##RynthAiTradeShop", ref open))
        {
            _spot.Track();
            ImGui.End();
            _settings.ShowTradeShopWindow = open;
            return;
        }
        _spot.Track();
        _settings.ShowTradeShopWindow = open;

        DrawBody();
        ImGui.End();
    }

    /// <summary>Shared body (also usable from Advanced Settings).</summary>
    public void DrawBody()
    {
        string status = _status?.Invoke() ?? "Not logged in";
        SafeImGui.TextDisabled($"Status: {status}");
        ImGui.Spacing();

        ImGui.Checkbox("Shop enabled##TSw", ref _settings.TradeShopEnabled);
        ImGui.SameLine();
        ImGui.Checkbox("Test mode##TSw", ref _settings.TradeShopTestMode);

        int mode = (int)_settings.TradeShopMode;
        ImGui.RadioButton("Mode 1: stock + trade window + tells##TSw", ref mode, 0);
        ImGui.SameLine();
        ImGui.RadioButton("Mode 2: text / multi-account##TSw", ref mode, 1);
        _settings.TradeShopMode = (TradeShopMode)mode;

        bool banking = _bankingSupported?.Invoke() == true;
        if (banking)
            ImGui.Checkbox("Banking hook (this server supports payments)##TSw", ref _settings.TradeShopBankingHook);
        else
            SafeImGui.TextDisabled("Banking hook: not available on this world.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Auto-advertise");
        ImGui.Separator();
        DrawAdvertise();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Payment / bank conversion rates (UB-IT)");
        ImGui.Separator();
        DrawRates();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Catalog — set each item's price (pyreals)");
        ImGui.Separator();
        DrawCatalog();
    }

    private void DrawAdvertise()
    {
        ImGui.Checkbox("Auto-advertise catalog##TSad", ref _settings.TradeShopAdvertiseEnabled);
        SafeImGui.SetItemTooltip("Posts the price sheet into an AC chat window on a timer (shop must be enabled).");

        int ch = (int)_settings.TradeShopAdvertiseChannel;
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Chat window##TSad", ref ch, "Trade (/ct)\0General (/cg)\0LFG (/clfg)\0Fellowship (/f)\0Allegiance (/a)\0Local (say)\0Roleplay (/crp)\0Society (/cs)\0"))
            _settings.TradeShopAdvertiseChannel = (TradeShopAdvertiseChannel)Math.Clamp(ch, 0, 7);

        int every = TradeShopAdvertise.ClampIntervalSec(_settings.TradeShopAdvertiseIntervalSec);
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Every (seconds)##TSad", ref every))
            _settings.TradeShopAdvertiseIntervalSec = TradeShopAdvertise.ClampIntervalSec(every);
        SafeImGui.TextDisabled($"Min {TradeShopAdvertise.MinIntervalSec}s, max {TradeShopAdvertise.MaxIntervalSec}s.");

        string prefix = _settings.TradeShopAdvertisePrefix ?? "";
        ImGui.SetNextItemWidth(280);
        if (ImGui.InputText("Prefix##TSad", ref prefix, 64))
            _settings.TradeShopAdvertisePrefix = prefix;

        if (ImGui.Button("Advertise now##TSad"))
            _runCommand?.Invoke("/ra tradeshop advertise now");
        ImGui.SameLine();
        SafeImGui.TextDisabled("Buyers: tell !help / !list / !price <slot|name|value> / !buy …");
    }

    private void DrawRates()
    {
        _settings.TradeShopRates ??= new TradeShopRates();
        var rates = _settings.TradeShopRates;

        ImGui.SetNextItemWidth(110);
        int pyMmd = (int)Math.Clamp(rates.PyrealPerMmd, 1, int.MaxValue);
        if (ImGui.InputInt("Pyreals per MMD##TSw", ref pyMmd))
            rates.PyrealPerMmd = Math.Max(1, pyMmd);

        LongRate("MMD per 1 EC (buy)##TSw", ref rates.ConvBuyEcMmd);
        LongRate("Lum per 1 EC (buy)##TSw", ref rates.ConvBuyEcLum);
        LongRate("LK per 1 EC (buy)##TSw", ref rates.ConvBuyEcLk);
        LongRate("MK per 1 EC (buy)##TSw", ref rates.ConvBuyEcMk);
        LongRate("WEC per 1 EC (buy)##TSw", ref rates.ConvBuyEcWec);

        ImGui.TextDisabled(rates.RatesHelpLine());
        ImGui.Spacing();
        ImGui.Text("Accept payment in:");
        ImGui.Checkbox("Py##TSwa", ref rates.AcceptPyreals); ImGui.SameLine();
        ImGui.Checkbox("MMD##TSwa", ref rates.AcceptMmd); ImGui.SameLine();
        ImGui.Checkbox("Lum##TSwa", ref rates.AcceptLuminance); ImGui.SameLine();
        ImGui.Checkbox("LK##TSwa", ref rates.AcceptLegendaryKeys);
        ImGui.Checkbox("MK##TSwa", ref rates.AcceptMythicalKeys); ImGui.SameLine();
        ImGui.Checkbox("EC##TSwa", ref rates.AcceptEnlightenedCoins); ImGui.SameLine();
        ImGui.Checkbox("WEC##TSwa", ref rates.AcceptWeaklyEnlightenedCoins);
    }

    private void DrawCatalog()
    {
        var rates = _settings.TradeShopRates ??= new TradeShopRates();
        var list = _settings.TradeShopListings ??= new();
        int removeAt = -1;

        if (ImGui.BeginTable("##TScat", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Price (py)", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Alt", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableHeadersRow();

            for (int i = 0; i < list.Count; i++)
            {
                var row = list[i];
                if (row == null) continue;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Checkbox("##en", ref row.Enabled);
                ImGui.TableNextColumn();
                string label = (row.Sku.Length > 0 ? row.Sku + " — " : "") + (row.Name.Length > 0 ? row.Name : "(unnamed)");
                ImGui.TextUnformatted(label);
                if (row.LocatedOnAccount.Length > 0)
                    SafeImGui.TextDisabled("@" + row.LocatedOnAccount);
                ImGui.TableNextColumn();
                int price = (int)Math.Clamp(row.PricePyreals, 0, int.MaxValue);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##price", ref price))
                    row.PricePyreals = Math.Max(0, price);
                ImGui.TableNextColumn();
                SafeImGui.TextDisabled(row.PricePyreals > 0 ? rates.FormatAlternatives(row.PricePyreals) : "—");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Del"))
                    removeAt = i;
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (removeAt >= 0)
            list.RemoveAt(removeAt);

        ImGui.Spacing();
        ImGui.Text("Add listing");
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("Default price (py)##TSwnew", ref _newPrice);
        if (ImGui.Button("Add selected item##TSw"))
            TryAddSelected(list);
        SafeImGui.TextDisabled("Multi-add: Ctrl/Shift-select in Inventory → right-click → Add to Trade Shop (sets default price below).");

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Name##TSwnew", ref _newName, 128);
        ImGui.SetNextItemWidth(80);
        ImGui.InputText("SKU##TSwnew", ref _newSku, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputText("Account##TSwnew", ref _newAcct, 64);
        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Description##TSwnew", ref _newDesc, 256);
        if (ImGui.Button("Add typed listing##TSw"))
        {
            string n = _newName.Trim();
            if (n.Length > 0 && _newPrice > 0)
            {
                list.Add(new TradeShopListing
                {
                    Sku = _newSku.Trim().Length > 0 ? _newSku.Trim() : "S" + (list.Count + 1),
                    Name = n,
                    Description = _newDesc.Trim(),
                    PricePyreals = _newPrice,
                    LocatedOnAccount = _newAcct.Trim(),
                    Enabled = true,
                });
                _newName = "";
                _newSku = "";
                _newAcct = "";
                _newDesc = "";
                _newPrice = 0;
            }
        }
    }

    private void TryAddSelected(List<TradeShopListing> list)
    {
        uint sel = _selectedItemId?.Invoke() ?? 0;
        if (sel == 0) return;
        if (list.Exists(l => l != null && l.ObjectId == sel)) return;
        WorldObjectCache? cache = _cache?.Invoke();
        WorldObject? wo = cache?[(int)sel];
        if (wo == null || string.IsNullOrWhiteSpace(wo.Name)) return;
        if (_newPrice <= 0) _newPrice = 1;
        list.Add(new TradeShopListing
        {
            Sku = _newSku.Trim().Length > 0 ? _newSku.Trim() : "S" + (list.Count + 1),
            Name = wo.Name,
            Description = _newDesc.Trim(),
            PricePyreals = _newPrice,
            ObjectId = sel,
            LocatedOnAccount = _newAcct.Trim(),
            Enabled = true,
        });
    }

    private static void LongRate(string label, ref long value)
    {
        ImGui.SetNextItemWidth(100);
        int v = (int)Math.Clamp(value, 1, int.MaxValue);
        if (ImGui.InputInt(label, ref v))
            value = Math.Max(1, v);
    }
}
