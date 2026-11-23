// Trade Shop floating window wiring on the plugin (Inventory button / /ra tradeshop ui).
using System;
using RynthCore.Plugin.RynthAi.Trade;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private TradeShopWindow? _tradeShopUi;

    private void EnsureTradeShopWindow()
    {
        if (_tradeShopUi != null || _dashboard == null) return;
        _tradeShopUi = new TradeShopWindow(
            _dashboard.Settings,
            () => _tradeShop?.Status ?? "Idle",
            () => _tradeShop?.BankingSupported == true,
            () => Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0,
            () => _objectCache,
            cmd =>
            {
                if (string.IsNullOrWhiteSpace(cmd)) return;
                // Prefer the real chat parser so /ra routes like a typed command.
                if (Host.HasInvokeChatParser) Host.InvokeChatParser(cmd.Trim());
                else HandleRaCommand(cmd.Trim());
            });
    }

    private void RenderTradeShopWindow()
    {
        EnsureTradeShopWindow();
        try { _tradeShopUi?.Render(); }
        catch (Exception ex) { Host.Log($"[RynthAi] TradeShop window: {ex.Message}"); }
    }

    /// <summary>Toggle the Trade Shop editor (Inventory Storefront / /ra tradeshop ui).</summary>
    internal void ToggleTradeShopWindow()
    {
        if (_dashboard?.Settings == null) return;
        _dashboard.Settings.ShowTradeShopWindow = !_dashboard.Settings.ShowTradeShopWindow;
    }
}
