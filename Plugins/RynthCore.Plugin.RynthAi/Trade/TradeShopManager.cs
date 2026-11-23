// Trade Shop bot: priced catalog + tell commands + Rynth trade API (Mode 1 stock / Mode 2 text).
// Mode 1: buyer !list / !price / !buy → tell price from TradeShopRates → TradeOpen/TradeAdd
// to show the item in the trade window → wait for payment → TradeAccept.
// Ungated. Banking hook only when the host says the server supports banking payments.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>
/// Mode 1 pairs the trade window (show stock via <see cref="RynthCoreHost.TradeAdd"/>) with
/// tell commands (!help / !list / !price / !buy) and Rynth trade actions (open / add / accept / reset).
/// Prices come from <see cref="TradeShopRates"/> (UB-IT conversion table).
/// Mode 2 stays text / multi-account: payment → tell listing → accept.
/// </summary>
internal sealed class TradeShopManager
{
    private const long BailMs = 30_000;
    private const long AcceptWaitMs = 10_000;
    private const long OpenTradeWaitMs = 15_000;
    /// <summary>Gap between outbound tells so AC does not drop the price sheet.</summary>
    private const long TellGapMs = 1_200;

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _cache;
    private readonly Func<bool> _bankingSupported;
    private readonly ConcurrentQueue<(string Sender, string Message)> _tells = new();
    private readonly ConcurrentQueue<(string To, string Message)> _outTells = new();
    private long _nextTellAt;
    private long _nextAdvertiseAt;

    private uint _playerId;
    private bool _baselined;
    private bool _wasOpen;
    private uint _generation;
    private uint _sessionGeneration;
    private uint _traderId;
    private string _traderName = "";

    private enum Phase
    {
        Idle,
        /// <summary>Mode 1: TradeOpen sent; waiting for the window.</summary>
        OpenTrade,
        /// <summary>Mode 1: TradeAdd sent; waiting for the item to appear on our side.</summary>
        ShowItem,
        /// <summary>Item is in the window (Mode 1) or Mode 2 payment watch; waiting for payment.</summary>
        WaitPayment,
        WaitAccept,
        Done,
    }

    private Phase _phase = Phase.Idle;
    private TradeShopListing? _active;
    private uint _pendingAddId;
    private uint _pendingBuyerId;
    private string _pendingBuyerName = "";
    private long _bailAt;
    private long _acceptDeadline;
    private volatile string _status = "Idle";

    public TradeShopManager(
        RynthCoreHost host,
        LegacyUiSettings settings,
        WorldObjectCache cache,
        uint playerId,
        Func<bool>? bankingSupported = null)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
        _playerId = playerId;
        _bankingSupported = bankingSupported ?? (() => false);
    }

    public string Status => _status;

    /// <summary>True while Mode 1 is opening/showing/waiting payment or accepting (holds the bot like AutoTrade).</summary>
    public bool HoldsBot => _phase is Phase.OpenTrade or Phase.ShowItem or Phase.WaitPayment or Phase.WaitAccept;

    public bool BankingSupported => _bankingSupported();

    public void SetPlayerId(uint id) => _playerId = id;

    public void Reset()
    {
        EndSession(clearStatus: true);
        _baselined = false;
        _wasOpen = false;
        _generation = 0;
        _traderId = 0;
        _traderName = "";
        while (_tells.TryDequeue(out _)) { }
        while (_outTells.TryDequeue(out _)) { }
        _nextTellAt = 0;
        _nextAdvertiseAt = 0;
        _status = "Idle";
    }

    /// <summary>Chat thread: queue incoming player tells that look like shop commands.</summary>
    public void OnChatWindowText(string? text, int chatType)
    {
        if (!_settings.TradeShopEnabled) return;
        if (string.IsNullOrEmpty(text)) return;
        // Prefer Tell colour; still accept a line that clearly matches the tell shape.
        if (chatType != ChatSource.Tell && chatType != -1
            && text.IndexOf(" tells you", StringComparison.OrdinalIgnoreCase) < 0)
            return;
        if (!TradeShopTellParse.TryParseTell(text, out string sender, out string msg)) return;
        string cmd = msg.Trim();
        if (cmd.Length == 0) return;
        // Shop verbs / greeting: queue; bare text also gets the rates + sheet intro.
        if (cmd[0] == '!' || TradeShopTellParse.IsShopVerb(cmd) || LooksLikeShopGreeting(cmd))
            _tells.Enqueue((sender, cmd));
    }

    public void Tick(int busyCount)
    {
        long now = Environment.TickCount64;
        DrainOutboundTells(now);
        TickAdvertise(now);
        DrainTells(now); // !list / !price / rates still work without the trade API

        if (!_host.HasTrade)
        {
            _status = "Needs engine trade API v72+";
            return;
        }

        PollTrade(now);

        if (_phase == Phase.Idle) return;

        if (now > _bailAt)
        {
            Tool("timed out");
            if (_phase is Phase.OpenTrade or Phase.ShowItem or Phase.WaitPayment)
                Tell(_pendingBuyerName.Length > 0 ? _pendingBuyerName : _traderName, "Shop timed out. Tell !buy again.");
            EndSession();
            return;
        }

        if (_phase != Phase.OpenTrade)
        {
            if (!_host.TryGetTradeState(out TradeState st) || !st.IsOpen || st.Generation != _sessionGeneration)
            {
                EndSession();
                return;
            }
        }

        switch (_phase)
        {
            case Phase.OpenTrade:
                TickOpenTrade(now);
                break;
            case Phase.ShowItem:
                TickShowItem(now);
                break;
            case Phase.WaitPayment:
                TickWaitPayment(now);
                break;
            case Phase.WaitAccept:
                TickWaitAccept(now);
                break;
        }
    }

    // ── Tell commands (Mode 1 shop front) ───────────────────────────────────

    private void DrainTells(long now)
    {
        // One inbound command per tick so the outbound tell queue can pace replies.
        if (!_tells.TryDequeue(out var tell)) return;
        HandleTell(tell.Sender, tell.Message, now);
    }

    private void HandleTell(string sender, string commandLine, long now)
    {
        if (!_settings.TradeShopEnabled) return;

        string raw = commandLine.Trim();
        if (raw.StartsWith('!')) raw = raw[1..].Trim();
        string[] parts = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1].Trim() : "";

        // Greeting / empty verb → rates + full price sheet (queued).
        if (verb.Length == 0 || LooksLikeShopGreeting(verb) || verb is "help" or "hi" or "hello" or "shop")
        {
            EnqueueIntro(sender);
            return;
        }

        switch (verb)
        {
            case "rates":
            case "rate":
            case "fx":
                QueueTell(sender, TradeShopMessages.RatesLine(Rates()));
                break;
            case "list":
            case "sheet":
            case "catalog":
            case "menu":
                EnqueueSheet(sender, includeRates: true);
                break;
            case "price":
            case "cost":
                QueueTell(sender, BuildPriceMessage(arg));
                break;
            case "buy":
                if (_settings.TradeShopMode != TradeShopMode.Mode1Stock)
                {
                    QueueTell(sender, "Mode 2: put payment in the trade window (see !list). !buy is Mode 1 only.");
                    break;
                }
                BeginMode1Buy(sender, arg, now);
                break;
            default:
                // Unknown: still help them with rates + sheet so a free-form tell gets the catalog.
                EnqueueIntro(sender);
                break;
        }
    }

    private void EnqueueIntro(string sender)
    {
        QueueTell(sender, TradeShopMessages.HelpLine());
        EnqueueSheet(sender, includeRates: true);
    }

    private void EnqueueSheet(string sender, bool includeRates)
    {
        foreach (string chunk in TradeShopMessages.BuildPriceSheetChunks(EnabledListings(), Rates(), includeRates))
            QueueTell(sender, chunk);
    }

    private void BeginMode1Buy(string buyerName, string arg, long now)
    {
        if (!HasPricedListings())
        {
            Tell(buyerName, "Shop catalog is empty.");
            return;
        }

        TradeShopListing? listing = ResolveListing(arg);
        if (listing == null)
        {
            Tell(buyerName, "Item not found. Use !list then !buy <id|item>.");
            return;
        }

        uint stockId = ResolveStock(listing);
        if (stockId == 0)
        {
            Tell(buyerName, $"Out of stock: {listing.Name}.");
            return;
        }

        // Busy with another buyer.
        if (_phase != Phase.Idle && !NamesEqual(_pendingBuyerName, buyerName) && !NamesEqual(_traderName, buyerName))
        {
            Tell(buyerName, $"Shop busy with {(_pendingBuyerName.Length > 0 ? _pendingBuyerName : _traderName)}. Try again shortly.");
            return;
        }

        TradeShopRates rates = Rates();
        string priceLine = listing.PriceLabel(rates);
        Tell(buyerName, $"Buy {listing.Name}: {priceLine}. Open trade (or accept my request); item will appear in the window. Put payment, then I accept.");

        _active = listing;
        _pendingAddId = stockId;
        _pendingBuyerName = buyerName;
        _pendingBuyerId = FindPlayerId(buyerName);

        if (_settings.TradeShopTestMode)
        {
            Tool($"TEST Mode1 buy {listing.Line(rates)} for {buyerName} (stock 0x{stockId:X8})");
            EndSession();
            return;
        }

        // Trade already open with this buyer → show item via TradeAdd.
        if (_host.TryGetTradeState(out TradeState st) && st.IsOpen
            && (NamesEqual(_traderName, buyerName) || (_pendingBuyerId != 0 && st.PartnerId == _pendingBuyerId)))
        {
            _sessionGeneration = st.Generation;
            _traderId = st.PartnerId;
            _traderName = ObjectName(st.PartnerId);
            StartShowItem(now);
            return;
        }

        // Ask them to trade via Rynth TradeOpen.
        if (_pendingBuyerId == 0)
        {
            Tell(buyerName, "I can't see you nearby to open trade. Walk up and open trade with me, then !buy again.");
            EndSession(clearStatus: false);
            _status = "Waiting for buyer nearby";
            return;
        }

        if (!_host.TradeOpen(_pendingBuyerId))
        {
            Tell(buyerName, "Could not send trade request. Open trade with me, then !buy again.");
            EndSession();
            return;
        }

        _phase = Phase.OpenTrade;
        _bailAt = now + OpenTradeWaitMs;
        _status = $"Trade requested → {buyerName}";
        _host.Log($"[RynthAi] TradeShop Mode1: TradeOpen → {buyerName} [0x{_pendingBuyerId:X8}] for {listing.Name}");
    }

    private void StartShowItem(long now)
    {
        if (_active == null || _pendingAddId == 0)
        {
            EndSession();
            return;
        }

        // Clear our side first so the window shows only this sale item.
        _host.TradeReset();

        if (!_host.TradeAdd(_pendingAddId))
        {
            Tell(_traderName.Length > 0 ? _traderName : _pendingBuyerName, $"Could not put {_active.Name} in the trade window.");
            EndSession();
            return;
        }

        _phase = Phase.ShowItem;
        _bailAt = now + BailMs;
        _status = $"Showing {_active.Name}";
        _host.Log($"[RynthAi] TradeShop Mode1: TradeAdd 0x{_pendingAddId:X8} ({_active.Name})");
        Tell(_traderName.Length > 0 ? _traderName : _pendingBuyerName,
            $"{_active.Name} is in the trade window. Pay {_active.PriceLabel(Rates())} then accept.");
    }

    // ── Trade poll / phases ─────────────────────────────────────────────────

    private void PollTrade(long now)
    {
        if (!_host.TryGetTradeState(out TradeState st))
            return;

        if (!_baselined)
        {
            _baselined = true;
            _generation = st.Generation;
            _wasOpen = st.IsOpen;
            if (st.IsOpen && st.PartnerId != 0)
            {
                _traderId = st.PartnerId;
                _traderName = ObjectName(st.PartnerId);
            }
            return;
        }

        if (_wasOpen && (!st.IsOpen || st.Generation != _generation))
            OnEndTrade();

        if (st.IsOpen && st.Generation != _generation)
            OnEnterTrade(st, now);

        _generation = st.Generation;
        _wasOpen = st.IsOpen;
    }

    private void OnEnterTrade(TradeState st, long now)
    {
        _traderId = st.PartnerId;
        if (_traderId == 0) return;
        _traderName = ObjectName(_traderId);
        _sessionGeneration = st.Generation;
        _host.Log($"[RynthAi] TradeShop: trade opened with {_traderName} [0x{_traderId:X8}]");

        if (!_settings.TradeShopEnabled)
        {
            _status = "Disabled (trade open)";
            return;
        }

        if (_settings.TradeShopMode == TradeShopMode.Mode1Stock)
        {
            // Mode 1: tell-driven. If we already have a pending !buy for this buyer, show the item.
            if (_phase == Phase.OpenTrade
                && (NamesEqual(_pendingBuyerName, _traderName)
                    || (_pendingBuyerId != 0 && _pendingBuyerId == _traderId)))
            {
                StartShowItem(now);
                return;
            }

            // Spontaneous trade: greet with rates + sheet and wait for !buy (do not auto-match payment).
            if (_phase == Phase.Idle)
            {
                EnqueueIntro(_traderName);
                _status = $"Trade open — tell !buy ({_traderName})";
            }
            return;
        }

        // Mode 2: watch payment against the catalog.
        if (!HasPricedListings())
        {
            _status = "No priced listings";
            return;
        }

        _phase = Phase.WaitPayment;
        _active = null;
        _pendingAddId = 0;
        _bailAt = now + BailMs;
        _status = $"Mode2 waiting payment from {_traderName}";
    }

    private void OnEndTrade()
    {
        if (_phase != Phase.Idle)
            EndSession();
        _traderId = 0;
    }

    private void TickOpenTrade(long now)
    {
        if (!_host.TryGetTradeState(out TradeState st) || !st.IsOpen) return;
        if (_pendingBuyerId != 0 && st.PartnerId != _pendingBuyerId
            && !NamesEqual(ObjectName(st.PartnerId), _pendingBuyerName))
            return;
        _sessionGeneration = st.Generation;
        _traderId = st.PartnerId;
        _traderName = ObjectName(st.PartnerId);
        StartShowItem(now);
    }

    private void TickShowItem(long now)
    {
        foreach (uint id in _host.GetTradeItems(TradeSide.You))
        {
            if (id != _pendingAddId) continue;
            _phase = Phase.WaitPayment;
            _bailAt = now + BailMs;
            _status = $"Waiting payment for {_active?.Name}";
            _host.Log("[RynthAi] TradeShop Mode1: item visible in trade window; waiting for payment");
            return;
        }
    }

    private void TickWaitPayment(long now)
    {
        uint[] partner = _host.GetTradeItems(TradeSide.Partner);
        if (partner.Length == 0) return;

        TradeShopRates rates = Rates();

        if (_settings.TradeShopMode == TradeShopMode.Mode1Stock)
        {
            // Payment must cover the listing we showed.
            if (_active == null || _active.PricePyreals <= 0) return;
            if (!TradeShopPayment.MeetsPrice(_cache, partner, _active.PricePyreals, rates))
                return;

            _host.Log($"[RynthAi] TradeShop Mode1: payment covers {_active.Line(rates)}");
            if (_settings.TradeShopTestMode)
            {
                Tool($"TEST would accept {_active.Line(rates)}");
                EndSession();
                return;
            }
            BeginAccept(now);
            return;
        }

        // Mode 2: any covered listing.
        TradeShopListing? hit = FindCoveredListing(partner);
        if (hit == null) return;
        _active = hit;
        _host.Log($"[RynthAi] TradeShop Mode2: payment covers {hit.Line(rates)}");
        if (_settings.TradeShopTestMode)
        {
            Tool($"TEST would sell {hit.Line(rates)}");
            EndSession();
            return;
        }
        TellBuyerMode2(hit);
        BeginAccept(now);
    }

    private void TickWaitAccept(long now)
    {
        if (!_host.TryGetTradeState(out TradeState st)) return;
        if (st.YouAccepted)
        {
            _status = "Accepted";
            _phase = Phase.Done;
            return;
        }
        if (now > _acceptDeadline)
        {
            Tool("accept timed out");
            EndSession();
            return;
        }
        _host.TradeAccept();
        _status = "Accept sent";
    }

    private void BeginAccept(long now)
    {
        _phase = Phase.WaitAccept;
        _acceptDeadline = now + AcceptWaitMs;
        _bailAt = _acceptDeadline;
        _status = "Accepting";
        if (_settings.TradeShopTestMode) return;
        _host.TradeAccept();
    }

    // ── Catalog helpers ─────────────────────────────────────────────────────

    private bool HasPricedListings()
    {
        var list = _settings.TradeShopListings;
        if (list == null) return false;
        return list.Any(l => l != null && l.Enabled && l.PricePyreals > 0
            && (!string.IsNullOrWhiteSpace(l.Name) || !string.IsNullOrWhiteSpace(l.Sku)));
    }

    private IReadOnlyList<TradeShopListing> EnabledListings()
        => (_settings.TradeShopListings ?? new List<TradeShopListing>())
            .Where(l => l != null && l.Enabled && l.PricePyreals > 0
                && (!string.IsNullOrWhiteSpace(l.Name) || !string.IsNullOrWhiteSpace(l.Sku)))
            .ToList()!;

    private TradeShopListing? ResolveListing(string arg)
        => TradeShopMessages.ResolveListing(EnabledListings(), arg);

    private TradeShopListing? FindCoveredListing(uint[] partnerItems)
    {
        TradeShopRates rates = Rates();
        foreach (TradeShopListing listing in EnabledListings().OrderByDescending(l => l.PricePyreals))
        {
            if (!TradeShopPayment.MeetsPrice(_cache, partnerItems, listing.PricePyreals, rates))
                continue;
            return listing;
        }
        return null;
    }

    private uint ResolveStock(TradeShopListing listing)
    {
        if (listing.ObjectId != 0)
        {
            WorldObject? byId = _cache[(int)listing.ObjectId];
            if (byId != null && byId.Id != 0 && !IsEquipped(byId))
                return (uint)byId.Id;
        }

        foreach (WorldObject wo in Inventory())
        {
            if (wo == null || wo.Id == 0 || IsEquipped(wo)) continue;
            if (!string.IsNullOrWhiteSpace(listing.Name)
                && string.Equals(wo.Name, listing.Name, StringComparison.OrdinalIgnoreCase))
                return (uint)wo.Id;
        }
        return 0;
    }

    private uint FindPlayerId(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        int me = unchecked((int)_playerId);
        WorldObject? partial = null;
        foreach (var wo in _cache.GetLandscapeObjects())
        {
            if (wo == null || wo.Id == me || wo.ObjectClass != AcObjectClass.Player) continue;
            if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                return unchecked((uint)wo.Id);
            if (partial == null && wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                partial = wo;
        }
        return partial != null ? unchecked((uint)partial.Id) : 0;
    }

    private List<WorldObject> Inventory()
    {
        try { return _cache.GetDirectInventory(forceRefresh: true).Where(w => w != null && w.Id != 0).ToList(); }
        catch { return new List<WorldObject>(); }
    }

    private static bool IsEquipped(WorldObject wo)
        => wo.WieldedLocation > 0 || wo.Values(LongValueKey.EquippedSlots, 0) > 0;

    private TradeShopRates Rates() => _settings.TradeShopRates ?? new TradeShopRates();

    private string BuildPriceMessage(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return "Usage: !price <slot|name|value>  e.g. !price #2 | !price Singularity | !price 250000";
        TradeShopListing? listing = ResolveListing(arg);
        if (listing == null) return "Item not found. Tell !list for the price sheet.";
        TradeShopRates rates = Rates();
        return $"{listing.Name}: {listing.PriceLabel(rates)}. Rates: {rates.RatesHelpLine()}";
    }

    /// <summary>Build the auto-advertise line (catalog summary + rates).</summary>
    public string BuildAdvertiseMessage()
    {
        string prefix = (_settings.TradeShopAdvertisePrefix ?? "").Trim();
        var list = EnabledListings();
        TradeShopRates rates = Rates();
        var chunks = TradeShopMessages.BuildPriceSheetChunks(list, rates, includeRates: true,
            header: prefix.Length > 0 ? prefix : "[Trade Shop]");
        // Advertise sends one chat line per interval; keep the first chunk (already length-capped).
        return chunks.Count > 0 ? chunks[0] : "[Trade Shop] (empty catalog)";
    }

    private void TellBuyerMode2(TradeShopListing hit)
    {
        string msg = hit.Description.Length > 0
            ? hit.Description
            : $"Sold: {hit.Name}" + (hit.LocatedOnAccount.Length > 0 ? $" (held on {hit.LocatedOnAccount})" : "");
        if (msg.Length == 0) msg = hit.Line(Rates());
        Tell(_traderName, "[TradeShop] " + msg);
        if (BankingSupported && _settings.TradeShopBankingHook)
            _host.Log("[RynthAi] TradeShop: banking payments available on this server (hook on).");
    }

    private void QueueTell(string to, string message)
    {
        string safe = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (string.IsNullOrWhiteSpace(to) || safe.Length == 0) return;
        _outTells.Enqueue((to, safe));
    }

    private void Tell(string to, string message) => QueueTell(to, message);

    private void DrainOutboundTells(long now)
    {
        if (_outTells.IsEmpty) return;
        if (now < _nextTellAt) return;
        if (!_outTells.TryDequeue(out var next)) return;
        SendTellNow(next.To, next.Message);
        _nextTellAt = now + TellGapMs;
    }

    private void SendTellNow(string to, string message)
    {
        if (string.IsNullOrWhiteSpace(to) || to.StartsWith("0x", StringComparison.Ordinal))
        {
            _host.Log($"[RynthAi] TradeShop → ?: {message}");
            return;
        }
        if (_host.HasInvokeChatParser)
            _host.InvokeChatParser($"/tell {to}, {message}");
        else
            _host.WriteToChat($"[RynthAi] TradeShop → {to}: {message}", 1);
    }

    private void TickAdvertise(long now)
    {
        if (!_settings.TradeShopEnabled || !_settings.TradeShopAdvertiseEnabled) return;
        if (_settings.TradeShopTestMode) return;
        int intervalSec = TradeShopAdvertise.ClampIntervalSec(_settings.TradeShopAdvertiseIntervalSec);
        long intervalMs = intervalSec * 1000L;
        if (_nextAdvertiseAt == 0)
            _nextAdvertiseAt = now + Math.Min(intervalMs, 5_000); // first blast shortly after enable
        if (now < _nextAdvertiseAt) return;
        _nextAdvertiseAt = now + intervalMs;

        string body = BuildAdvertiseMessage();
        if (body.Length == 0) return;
        string prefix = TradeShopAdvertise.SendPrefix(_settings.TradeShopAdvertiseChannel);
        if (_host.HasInvokeChatParser)
            _host.InvokeChatParser(prefix + body);
        else
            _host.WriteToChat($"[RynthAi] TradeShop ad: {body}", 1);
        _host.Log($"[RynthAi] TradeShop advertised on {TradeShopAdvertise.DisplayName(_settings.TradeShopAdvertiseChannel)}");
    }

    private void EndSession(bool clearStatus = false)
    {
        _phase = Phase.Idle;
        _active = null;
        _pendingAddId = 0;
        _pendingBuyerId = 0;
        _pendingBuyerName = "";
        if (clearStatus || string.IsNullOrEmpty(_status)
            || _status.StartsWith("Waiting", StringComparison.Ordinal)
            || _status.StartsWith("Showing", StringComparison.Ordinal)
            || _status.StartsWith("Adding", StringComparison.Ordinal)
            || _status.StartsWith("Accept", StringComparison.Ordinal)
            || _status.StartsWith("Trade requested", StringComparison.Ordinal)
            || _status.StartsWith("Mode2", StringComparison.Ordinal))
            _status = _settings.TradeShopEnabled ? "Idle (enabled)" : "Idle";
    }

    private void Tool(string msg)
    {
        _host.Log($"[RynthAi] TradeShop: {msg}");
        _host.WriteToChat($"[RynthAi] TradeShop: {msg}", 1);
    }

    private string ObjectName(uint id)
        => id != 0 && _host.TryGetObjectName(id, out string n) && !string.IsNullOrWhiteSpace(n) ? n : $"0x{id:X8}";

    private static bool NamesEqual(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeShopGreeting(string cmd)
    {
        string v = cmd.Trim().ToLowerInvariant();
        if (v.StartsWith('!')) v = v[1..].Trim();
        // First word only for multi-word tells ("hi there").
        if (v.IndexOf(' ') > 0) v = v.Split(' ', 2)[0];
        return v is "hi" or "hello" or "hey" or "yo" or "sup" or "shop" or "vendor" or "wts" or "prices";
    }

}
