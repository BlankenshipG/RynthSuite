// IltGames.cs — ILT Hub "Games" tab + hand HUD (P2): Blackjack, Fellowship Blackjack, Powerball.
//
// Every bet / ticket purchase spends banked luminance, so each one needs a confirm.
// Blackjack is commented out on the current ACECustom build ("Unknown command:
// blackjack"); the pad only appears when the login probe found the command.
//   /blackjack <bet>, /hit, /stand, /double, /split   (fellowship: /fblackjack, /fhit, ...; max 100B)
//   /pb (info), /pb buy <1-500>, /pb tickets
// The HUD is a small always-on-top window shown while a hand is in progress.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltGames : IIltFeature
{
    private const long FBlackjackMaxBet = 100_000_000_000;
    private static readonly Regex Dealt = new(@"^You were dealt (?:a |an )?(?<card>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex MyValue = new(@"Your (?:initial )?hand value is (?:now )?(?<v>\d+)", RegexOptions.Compiled);
    private static readonly Regex DealerValue = new(@"Rand's (?:current )?hand value is (?<v>\d+)", RegexOptions.Compiled);

    private readonly IltHubContext _ctx;

    // Hand state (pump thread writes, render thread reads — simple fields / snapshot list)
    private volatile bool _handActive;
    private volatile bool _fellowshipHand;
    private volatile string[] _cards = Array.Empty<string>();
    private volatile int _myValue, _dealerValue;
    private volatile string _lastResult = string.Empty;

    // Powerball info
    private volatile string _pbDraw = string.Empty, _pbJackpot = string.Empty, _pbPrice = string.Empty, _pbStatus = string.Empty;

    // Render-thread input
    private string _bjBetText = string.Empty, _fbjBetText = string.Empty;

    public IltGames(IltHubContext ctx) => _ctx = ctx;

    private IltGamesState S => _ctx.State.Games;

    public bool AnyGameAvailable => _ctx.Options.IsOn(IltFeature.Blackjack) || _ctx.Options.IsOn(IltFeature.FBlackjack)
                                    || !_ctx.Options.IsOff(IltFeature.Powerball);

    // ── Commands (pump thread) ──────────────────────────────────────────────

    private void Send(string cmd)
    {
        if (_ctx.Host.HasInvokeChatParser) _ctx.Host.InvokeChatParser(cmd);
    }

    private void StartBlackjack(bool fellowship, long bet)
    {
        _fellowshipHand = fellowship;
        _cards = Array.Empty<string>();
        _myValue = _dealerValue = 0;
        _lastResult = string.Empty;
        Send($"/{(fellowship ? "fblackjack" : "blackjack")} {bet}");
    }

    private void HandAction(string verb) => Send("/" + (_fellowshipHand ? "f" : "") + verb);

    public void RequestPowerballInfo()
    {
        if (_ctx.Options.IsOff(IltFeature.Powerball) || _ctx.Capture.IsPending("/pb")) return;
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/pb",
            IsResponseLine = t => t.StartsWith("[Powerball]", StringComparison.OrdinalIgnoreCase)
                               || t.StartsWith("Draw #", StringComparison.OrdinalIgnoreCase)
                               || t.StartsWith("Jackpot Pool", StringComparison.OrdinalIgnoreCase)
                               || t.StartsWith("Ticket Price", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 1200,
            FirstLineTimeoutMs = 8000,
            Eat = true,
            OnComplete = r =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Powerball, IltTri.Off); return; }
                foreach (string l in r.Lines) ParsePowerball(l);
                _pbStatus = r.Lines.Count > 0 ? "updated " + DateTime.Now.ToString("t") : "no reply";
            },
        });
    }

    private void ParsePowerball(string l)
    {
        string t = l.Trim();
        if (t.StartsWith("Draw #", StringComparison.OrdinalIgnoreCase)) _pbDraw = t;
        else if (t.StartsWith("Jackpot Pool", StringComparison.OrdinalIgnoreCase)) _pbJackpot = t;
        else if (t.StartsWith("Ticket Price", StringComparison.OrdinalIgnoreCase)) _pbPrice = t;
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs) { }

    public bool OnChat(string text)
    {
        // Powerball lines typed by the player still update the panel.
        if (text.StartsWith("[Powerball]", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("You bought", StringComparison.OrdinalIgnoreCase) || text.Contains("Insufficient", StringComparison.OrdinalIgnoreCase))
                _pbStatus = text;
            return false;
        }
        string t = text.Trim();
        if (t.StartsWith("Draw #", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Jackpot Pool", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Ticket Price", StringComparison.OrdinalIgnoreCase))
        {
            ParsePowerball(t);
            return false;
        }

        // Blackjack
        var d = Dealt.Match(text);
        if (d.Success)
        {
            var list = new List<string>(_cards) { d.Groups["card"].Value };
            _cards = list.ToArray();
            _handActive = true;
            return false;
        }
        var mv = MyValue.Match(text);
        if (mv.Success) { _myValue = int.Parse(mv.Groups["v"].Value); _handActive = true; }
        var dv = DealerValue.Match(text);
        if (dv.Success) _dealerValue = int.Parse(dv.Groups["v"].Value);
        if (text.StartsWith("The game begins!", StringComparison.OrdinalIgnoreCase)) _handActive = true;

        if (text.StartsWith("Blackjack! You win", StringComparison.OrdinalIgnoreCase) || text.StartsWith("You won with", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("You tied with Rand", StringComparison.OrdinalIgnoreCase) || text.StartsWith("You lost with", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("You busted", StringComparison.OrdinalIgnoreCase))
            _lastResult = text;
        if (text.StartsWith("The blackjack game has ended", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("You are not currently in a blackjack game", StringComparison.OrdinalIgnoreCase))
            _handActive = false;
        if (text.StartsWith("You don't have enough luminance to place this bet", StringComparison.OrdinalIgnoreCase))
        {
            _lastResult = text;
            _handActive = false;
        }
        return false;
    }

    public void OnLogout()
    {
        _handActive = false;
        _cards = Array.Empty<string>();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        bool bj = _ctx.Options.IsOn(IltFeature.Blackjack), fbj = _ctx.Options.IsOn(IltFeature.FBlackjack);
        if (!bj && !fbj && _ctx.Options.IsOff(IltFeature.Powerball))
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "No games are enabled on this server.");
            return;
        }
        bool hud = _ctx.State.GamesHudVisible;
        if (ImGui.Checkbox("Show hand HUD while playing", ref hud)) _ctx.State.GamesHudVisible = hud;

        if (bj && ImGui.CollapsingHeader("Blackjack", ImGuiTreeNodeFlags.DefaultOpen)) RenderBlackjack(false, ref _bjBetText);
        if (fbj && ImGui.CollapsingHeader("Fellowship Blackjack", ImGuiTreeNodeFlags.DefaultOpen)) RenderBlackjack(true, ref _fbjBetText);
        if (!bj && !fbj) ImGui.TextDisabled("Blackjack is not enabled on this server.");
        if (!_ctx.Options.IsOff(IltFeature.Powerball) && ImGui.CollapsingHeader("Powerball", ImGuiTreeNodeFlags.DefaultOpen)) RenderPowerball();
    }

    private void RenderBlackjack(bool fellowship, ref string betText)
    {
        string id = fellowship ? "fbj" : "bj";
        if (betText.Length == 0) betText = IltParse.Compact(fellowship ? S.FellowshipBlackjackBet : S.BlackjackBet).ToLowerInvariant();
        ImGui.SetNextItemWidth(160);
        ImGui.InputTextWithHint($"Bet (luminance)##{id}", "e.g. 1m, 500k", ref betText, 24u);
        bool ok = IltParse.TryParseAmount(betText, out long bet) && (!fellowship || bet <= FBlackjackMaxBet);
        ImGui.SameLine();
        ImGui.TextDisabled(ok ? IltParse.N0(bet) : fellowship ? "invalid (max 100B)" : "invalid");

        ImGui.BeginDisabled(!ok || _handActive);
        if (ImGui.Button($"Deal...##{id}"))
        {
            long b = bet;
            if (fellowship) S.FellowshipBlackjackBet = b; else S.BlackjackBet = b;
            _ctx.Confirm(fellowship ? "Fellowship Blackjack" : "Blackjack",
                $"Bet {IltParse.N0(b)} banked luminance on a new hand?", () => StartBlackjack(fellowship, b), "Bet");
        }
        ImGui.EndDisabled();

        if (_handActive && _fellowshipHand == fellowship) RenderHandButtons(id);
        if (_lastResult.Length > 0 && _fellowshipHand == fellowship) ImGui.TextWrapped(_lastResult);
    }

    private void RenderHandButtons(string id)
    {
        ImGui.TextUnformatted($"Hand: {string.Join(", ", _cards)}  ({_myValue})   Rand: {(_dealerValue > 0 ? _dealerValue.ToString() : "?")}");
        if (ImGui.Button($"Hit##{id}")) _ctx.Post(() => HandAction("hit"));
        ImGui.SameLine();
        if (ImGui.Button($"Stand##{id}")) _ctx.Post(() => HandAction("stand"));
        ImGui.SameLine();
        if (ImGui.Button($"Double...##{id}"))
            _ctx.Confirm("Double down", "Double your bet on this hand?", () => HandAction("double"), "Double");
        ImGui.SameLine();
        if (ImGui.Button($"Split...##{id}"))
            _ctx.Confirm("Split", "Split this hand (places a second bet)?", () => HandAction("split"), "Split");
    }

    private void RenderPowerball()
    {
        if (ImGui.SmallButton("Refresh##pb")) _ctx.Post(RequestPowerballInfo);
        ImGui.SameLine();
        if (ImGui.SmallButton("My tickets")) _ctx.Post(() => Send("/pb tickets"));
        if (_pbDraw.Length > 0) ImGui.TextUnformatted(_pbDraw);
        if (_pbJackpot.Length > 0) ImGui.TextUnformatted(_pbJackpot);
        if (_pbPrice.Length > 0) ImGui.TextUnformatted(_pbPrice);
        int qty = S.PowerballQuantity;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Tickets", ref qty)) S.PowerballQuantity = Math.Clamp(qty, 1, 500);
        ImGui.SameLine();
        if (ImGui.Button("Buy..."))
        {
            int q = S.PowerballQuantity;
            _ctx.Confirm("Powerball", $"Buy {q} Powerball ticket(s)?\n{(_pbPrice.Length > 0 ? _pbPrice : "Tickets cost banked luminance.")}",
                () => Send($"/pb buy {q}"), "Buy");
        }
        if (_pbStatus.Length > 0) ImGui.TextWrapped(_pbStatus);
    }

    /// <summary>Small HUD with the current hand; called every frame by the Hub UI.</summary>
    public void RenderHud()
    {
        if (!_handActive || !_ctx.State.GamesHudVisible) return;
        ImGui.SetNextWindowSize(new Vector2(300, 0), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Blackjack HUD##iltbjhud", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, _fellowshipHand ? "Fellowship Blackjack" : "Blackjack");
            RenderHandButtons("hud");
        }
        ImGui.End();
        if (!open) _ctx.State.GamesHudVisible = false;
    }
}
