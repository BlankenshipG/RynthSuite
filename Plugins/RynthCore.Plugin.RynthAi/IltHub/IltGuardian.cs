// IltGuardian.cs - Temple of Enlightenment guardians (ILT Hub "Guardian" window).
//
//   * Riddle translator: incoming "Guardian of ... tells you / says" lines (when chat auto-detect
//     is on) and pasted text go through IltGuardianPhrasebook; the answer shows here and on the
//     Mini Remote.
//   * Hand-in: gives one of the answer item to the guardian (GiveObjectTo). For the Temple
//     guardian, when none is carried and "buy missing" is on, it first buys one from the open or
//     nearest vendor (VendorBuy). Runs as a small pump-thread state machine with timeouts; the
//     client busy state is respected between steps.
//   * Attribute tracker: IltTempleAttributes.
//
// Threading: OnChat / Tick / commands run on the pump thread; Render only reads volatile fields
// and posts actions through IltHubContext.Post.
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltGuardian : IIltFeature
{
    /// <summary>The same guardian + answer from chat within this window is one prompt (tells repeat lines).</summary>
    private const long DebounceMs = 2500;
    private const long HandInTimeoutMs = 45_000;
    private const long VendorOpenTimeoutMs = 10_000;
    private const long BuyTimeoutMs = 10_000;
    private const long ItemArriveTimeoutMs = 8_000;
    private const long BusyRetryMs = 400;
    private const int MaxGiveAttempts = 5;
    /// <summary>How far (meters) to look for the guardian and a vendor.</summary>
    private const double SearchRangeMeters = 60.0;
    /// <summary>The Mini Remote shows the last answer for this long.</summary>
    public static readonly TimeSpan AnswerShownFor = TimeSpan.FromMinutes(10);

    private enum Step { Idle, FindItem, OpenVendor, WaitVendor, Buy, WaitBuy, WaitItem, Give }

    private readonly IltHubContext _ctx;
    private string _lastFingerprint = string.Empty;
    private long _lastEmitAt;

    // Last prompt (written on the pump thread, read by the render thread).
    private volatile string _lastLine = string.Empty;
    private volatile string _lastAnswer = string.Empty;
    private volatile string _lastNpc = string.Empty;
    private long _lastAnswerTicksUtc;
    private volatile string _handInStatus = "idle";

    // Hand-in state machine (pump thread only).
    private Step _step = Step.Idle;
    private string _item = string.Empty;
    private string _npc = string.Empty;
    private bool _asked;
    private long _startedAt;
    private long _stepDeadline;
    private long _nextStepAt;
    private uint _vendorId;
    private uint _buyRequest;
    private int _giveAttempts;

    // Render-thread UI state.
    private string _input = string.Empty;

    public IltGuardian(IltHubContext ctx, Func<QuestTracker?> tracker, IltQuests quests)
    {
        _ctx = ctx;
        Attributes = new IltTempleAttributes(ctx, tracker, quests);
    }

    public IltTempleAttributes Attributes { get; }

    private IltGuardianState S => _ctx.State.Guardian;

    /// <summary>Last answer item ("" until a guardian prompt was translated).</summary>
    public string LastAnswer => _lastAnswer;
    /// <summary>Guardian the last answer goes to.</summary>
    public string LastNpc => _lastNpc;
    /// <summary>The hand-in sequence is running.</summary>
    public bool HandInRunning => _step != Step.Idle;
    public string HandInStatus => _handInStatus;

    /// <summary>Time since the last answer (MaxValue when there is none).</summary>
    public TimeSpan LastAnswerAge
    {
        get
        {
            long t = System.Threading.Interlocked.Read(ref _lastAnswerTicksUtc);
            return t == 0 ? TimeSpan.MaxValue : DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc);
        }
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        Attributes.Tick(nowMs);
        StepHandIn(nowMs);
    }

    public bool OnChat(string text)
    {
        if (!S.AutoDetectFromChat || !IltGuardianPhrasebook.LooksLikeIncomingGuardianLine(text)) return false;
        _lastLine = text.Trim();
        string answer = IltGuardianPhrasebook.Translate(text);
        if (!IltGuardianPhrasebook.IsAnswer(answer)) return false;

        string npc = IltGuardianPhrasebook.TurnInNpcFromLine(text);
        long now = IltHubContext.NowMs;
        string fingerprint = npc + "|" + answer;
        if (fingerprint == _lastFingerprint && now - _lastEmitAt < DebounceMs) return false;
        _lastFingerprint = fingerprint;
        _lastEmitAt = now;

        SetAnswer(answer, npc);
        _ctx.Chat($"[ILT Guardian] {npc} wants: {answer}");
        RynthLog.Write(LogCat.IltHub, $"[IltGuardian] chat answer '{answer}' for {npc}");
        if (S.AutoHandIn) StartHandIn(answer, npc, asked: false);
        return false; // never hide the guardian's own lines
    }

    public void OnLogout()
    {
        StopHandIn("stopped (logout)");
        Attributes.OnLogout();
    }

    private void SetAnswer(string answer, string npc)
    {
        _lastAnswer = answer;
        _lastNpc = npc;
        System.Threading.Interlocked.Exchange(ref _lastAnswerTicksUtc, DateTime.UtcNow.Ticks);
    }

    /// <summary>Translates pasted text; a real answer becomes the current answer. Pump thread.</summary>
    public string TranslateManual(string text)
    {
        string answer = IltGuardianPhrasebook.Translate(text);
        if (!IltGuardianPhrasebook.IsAnswer(answer)) return answer;
        string npc = IltGuardianPhrasebook.TurnInNpcFromInput(text);
        _lastLine = text.Trim();
        SetAnswer(answer, npc);
        return $"{answer} (give to {npc})";
    }

    // ── Hand-in ─────────────────────────────────────────────────────────────

    /// <summary>Starts giving <paramref name="item"/> to <paramref name="npc"/>. Pump thread.</summary>
    public string StartHandIn(string item, string npc, bool asked)
    {
        if (!IltGuardianPhrasebook.IsAnswer(item)) return "No answer to hand in yet.";
        if (!_ctx.Host.HasGiveObjectTo) return SetStatus("can't give items: update the RynthCore engine (API v62+).");
        _item = item;
        _npc = string.IsNullOrWhiteSpace(npc) ? IltGuardianPhrasebook.AttributeGuardian : npc;
        _asked = asked;
        _startedAt = IltHubContext.NowMs;
        _nextStepAt = 0;
        _vendorId = 0;
        _buyRequest = 0;
        _giveAttempts = 0;
        _step = Step.FindItem;
        RynthLog.Write(LogCat.IltHub, $"[IltGuardian] hand-in start: '{_item}' -> {_npc} ({(asked ? "asked" : "auto")})");
        return SetStatus($"handing {_item} to {_npc}...");
    }

    /// <summary>Stops a running hand-in.</summary>
    public void StopHandIn(string why = "stopped")
    {
        if (_step == Step.Idle) return;
        _step = Step.Idle;
        SetStatus(why);
        RynthLog.Write(LogCat.IltHub, $"[IltGuardian] hand-in {why}");
    }

    private string SetStatus(string text)
    {
        _handInStatus = text;
        return text;
    }

    private void Fail(string why)
    {
        _step = Step.Idle;
        SetStatus("failed: " + why);
        _ctx.Chat($"[ILT Guardian] Hand-in failed: {why}");
        RynthLog.Write(LogCat.IltHub, $"[IltGuardian] hand-in failed: {why}");
    }

    private void Next(Step step, long nowMs, long timeoutMs = 0)
    {
        _step = step;
        _stepDeadline = timeoutMs > 0 ? nowMs + timeoutMs : 0;
    }

    private void StepHandIn(long nowMs)
    {
        if (_step == Step.Idle || nowMs < _nextStepAt) return;
        if (nowMs - _startedAt > HandInTimeoutMs) { Fail("timed out"); return; }
        if (_stepDeadline != 0 && nowMs > _stepDeadline) { Fail(TimeoutText(_step)); return; }

        var host = _ctx.Host;
        switch (_step)
        {
            case Step.FindItem:
                if (FindHandInItem() != null) { Next(Step.Give, nowMs); break; }
                if (!IsTempleGuardian(_npc) || !S.BuyMissingFromVendor) { Fail($"no {_item} in your packs"); break; }
                Next(Step.OpenVendor, nowMs);
                break;

            case Step.OpenVendor:
            {
                if (!host.HasVendorTrade) { Fail($"no {_item} carried and vendor buying needs a newer RynthCore engine"); break; }
                if (host.TryGetVendorInfo(out var open)) { _vendorId = open.VendorId; Next(Step.Buy, nowMs); break; }
                var vendor = Nearest(wo => wo.ObjectClass == AcObjectClass.Vendor);
                if (vendor == null) { Fail($"no {_item} carried and no vendor nearby"); break; }
                SetStatus($"opening {vendor.Name} to buy {_item}...");
                host.UseFor(unchecked((uint)vendor.Id), "IltGuardian", $"buy {_item} for the Temple guardian",
                            _asked ? UseKind.Asked : UseKind.Auto);
                Next(Step.WaitVendor, nowMs, VendorOpenTimeoutMs);
                break;
            }

            case Step.WaitVendor:
                if (host.TryGetVendorInfo(out var info) && info.ItemCount > 0) { _vendorId = info.VendorId; Next(Step.Buy, nowMs); }
                break;

            case Step.Buy:
            {
                if (!host.TryGetVendorInfo(out var v)) { Fail("the vendor closed"); break; }
                if (v.TradeInFlight) { _nextStepAt = nowMs + BusyRetryMs; break; }
                var match = host.GetVendorItems().FirstOrDefault(i => i.Name.Equals(_item, StringComparison.OrdinalIgnoreCase));
                if (match == null) { Fail($"{v.Name} doesn't sell {_item}"); break; }
                _buyRequest = host.VendorBuy(match.ObjectId, 1, v.VendorId);
                if (_buyRequest == 0)
                {
                    string why = host.TryGetVendorTradeStatus(out var st) && st.Message.Length > 0 ? st.Message : "refused";
                    Fail($"couldn't buy {_item}: {why}");
                    break;
                }
                SetStatus($"buying {_item} from {v.Name}...");
                Next(Step.WaitBuy, nowMs, BuyTimeoutMs);
                break;
            }

            case Step.WaitBuy:
                if (!host.TryGetVendorTradeStatus(out var status) || status.RequestId != _buyRequest || !status.IsFinished) break;
                if (!status.Succeeded) { Fail($"couldn't buy {_item}: {(status.Message.Length > 0 ? status.Message : status.Result.ToString())}"); break; }
                Next(Step.WaitItem, nowMs, ItemArriveTimeoutMs);
                break;

            case Step.WaitItem:
                if (FindHandInItem() != null) Next(Step.Give, nowMs);
                break;

            case Step.Give:
                GiveStep(nowMs);
                break;
        }
    }

    private void GiveStep(long nowMs)
    {
        if (_ctx.Inventory.IsBusy)
        {
            SetStatus("waiting for the client (busy)...");
            _nextStepAt = nowMs + BusyRetryMs;
            return;
        }
        var item = FindHandInItem();
        if (item == null) { Fail($"no {_item} in your packs"); return; }
        var npc = Nearest(wo => wo.Name.Contains(_npc, StringComparison.OrdinalIgnoreCase));
        if (npc == null) { Fail($"{_npc} isn't nearby"); return; }

        int stack = _ctx.Inventory.StackSize(item);
        bool ok = _ctx.Host.GiveObjectTo(unchecked((uint)item.Id), unchecked((uint)npc.Id), stack > 1 ? 1 : 0);
        RynthLog.Write(LogCat.IltHub, $"[IltGuardian] give '{item.Name}' 0x{(uint)item.Id:X8} (stack {stack}) -> {npc.Name} 0x{(uint)npc.Id:X8}: {ok}");
        if (ok)
        {
            _step = Step.Idle;
            SetStatus($"gave {item.Name} to {npc.Name}");
            _ctx.Chat($"[ILT Guardian] Gave {item.Name} to {npc.Name}.");
            return;
        }
        if (++_giveAttempts >= MaxGiveAttempts) { Fail("the give was refused"); return; }
        _nextStepAt = nowMs + BusyRetryMs;
    }

    private static string TimeoutText(Step step) => step switch
    {
        Step.WaitVendor => "the vendor didn't open",
        Step.WaitBuy => "the vendor didn't answer the purchase",
        Step.WaitItem => "the bought item didn't arrive",
        _ => "timed out",
    };

    private static bool IsTempleGuardian(string npc) =>
        npc.Contains(IltGuardianPhrasebook.TempleGuardian, StringComparison.OrdinalIgnoreCase);

    /// <summary>A carried (not wielded) item named exactly like the answer.</summary>
    private WorldObject? FindHandInItem() =>
        _ctx.Inventory.Items().FirstOrDefault(wo => wo.Name.Equals(_item, StringComparison.OrdinalIgnoreCase)
                                                    && !_ctx.Inventory.IsEquipped(wo));

    /// <summary>Nearest landscape object matching <paramref name="match"/> within SearchRangeMeters.</summary>
    private WorldObject? Nearest(Func<WorldObject, bool> match)
    {
        var cache = _ctx.Inventory.Cache;
        int playerId = unchecked((int)_ctx.Inventory.PlayerId);
        if (cache == null || playerId == 0) return null;
        WorldObject? best = null;
        double bestDist = SearchRangeMeters;
        foreach (var wo in cache.GetLandscapeObjects())
        {
            if (!match(wo)) continue;
            double d = cache.Distance(playerId, wo.Id);
            if (d <= bestDist) { bestDist = d; best = wo; }
        }
        return best;
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>
    /// "/ra guardian translate &lt;text&gt;|handin|stop|chat on|off|auto on|off|buy on|off|refresh|status".
    /// "window" is handled by the controller. Pump thread.
    /// </summary>
    public void HandleCommand(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)) : string.Empty;
        switch (sub)
        {
            case "translate":
            case "t":
                _ctx.Chat("[ILT Guardian] " + TranslateManual(rest));
                break;
            case "handin":
            case "give":
                _ctx.Chat("[ILT Guardian] " + StartHandIn(_lastAnswer, _lastNpc, asked: true));
                break;
            case "stop":
                StopHandIn();
                _ctx.Chat("[ILT Guardian] Hand-in stopped.");
                break;
            case "chat":
                S.AutoDetectFromChat = Toggle(rest, S.AutoDetectFromChat);
                _ctx.Chat($"[ILT Guardian] Chat auto-detect {(S.AutoDetectFromChat ? "on" : "off")}.");
                break;
            case "auto":
                S.AutoHandIn = Toggle(rest, S.AutoHandIn);
                _ctx.Chat($"[ILT Guardian] Auto hand-in {(S.AutoHandIn ? "on" : "off")}.");
                break;
            case "buy":
                S.BuyMissingFromVendor = Toggle(rest, S.BuyMissingFromVendor);
                _ctx.Chat($"[ILT Guardian] Buy missing Temple items {(S.BuyMissingFromVendor ? "on" : "off")}.");
                break;
            case "refresh":
                _ctx.Chat("[ILT Guardian] " + Attributes.Refresh());
                break;
            case "status":
                _ctx.Chat($"[ILT Guardian] chat={(S.AutoDetectFromChat ? "on" : "off")} auto={(S.AutoHandIn ? "on" : "off")} buy={(S.BuyMissingFromVendor ? "on" : "off")} "
                          + $"answer={(_lastAnswer.Length > 0 ? $"{_lastAnswer} -> {_lastNpc}" : "-")} hand-in: {_handInStatus}");
                foreach (var r in Attributes.Snapshot)
                    _ctx.Chat($"[ILT Guardian] {r.Attribute,-12} {r.Npc,-16} #{r.Count} {r.Status} {r.Eta}");
                break;
            default:
                _ctx.Chat("[ILT Guardian] /ra guardian [window [show|hide]|translate <text>|handin|stop|chat on|off|auto on|off|buy on|off|refresh|status]");
                break;
        }
    }

    private static bool Toggle(string mode, bool current) => mode.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "show" => true,
        "off" or "false" or "0" or "hide" => false,
        _ => !current,
    };

    // ── UI (render thread) ──────────────────────────────────────────────────

    /// <summary>Guardian window body.</summary>
    public void Render()
    {
        if (ImGui.CollapsingHeader("Riddle translator", ImGuiTreeNodeFlags.DefaultOpen)) RenderTranslator();
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Attribute turn-ins", ImGuiTreeNodeFlags.DefaultOpen)) RenderTracker();
    }

    private void RenderTranslator()
    {
        var s = S;
        // Plain bool writes; the pump thread reads them on its next tick.
        bool chat = s.AutoDetectFromChat;
        if (ImGui.Checkbox("Detect from chat##grdchat", ref chat)) s.AutoDetectFromChat = chat;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Translate the guardians' tells as they arrive and show the answer here and on the Mini Remote.");
        ImGui.SameLine();
        bool auto = s.AutoHandIn;
        if (ImGui.Checkbox("Auto hand-in##grdauto", ref auto)) s.AutoHandIn = auto;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("After a chat answer, give one of the item to the guardian automatically.");
        ImGui.SameLine();
        bool buy = s.BuyMissingFromVendor;
        if (ImGui.Checkbox("Buy if missing##grdbuy", ref buy)) s.BuyMissingFromVendor = buy;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Temple guardian: when you carry none, buy one from the open or nearest vendor first.");

        string line = _lastLine;
        if (line.Length > 0)
        {
            ImGui.TextDisabled("Heard:");
            ImGui.SameLine();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(IltGuardianPhrasebook.MessageOf(line));
            ImGui.PopTextWrapPos();
        }

        string answer = _lastAnswer, npc = _lastNpc;
        if (answer.Length > 0)
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColTeal, answer);
            ImGui.SameLine();
            ImGui.TextDisabled("-> " + npc);
            if (HandInRunning)
            {
                if (ImGui.SmallButton("Stop##grdstop")) _ctx.Post(() => StopHandIn());
            }
            else if (ImGui.SmallButton("Hand in##grdgive"))
            {
                _ctx.Post(() => StartHandIn(answer, npc, asked: true));
            }
        }
        else ImGui.TextDisabled("No guardian answer yet.");
        ImGui.TextDisabled("Hand-in: " + _handInStatus);

        ImGui.Spacing();
        ImGui.InputTextMultiline("##grdinput", ref _input, 2048u, new Vector2(-1, 54));
        if (ImGui.Button("Translate##grdtr"))
        {
            string text = _input;
            _ctx.Post(() => _ctx.Chat("[ILT Guardian] " + TranslateManual(text)));
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear##grdclr")) _input = string.Empty;
        ImGui.SameLine();
        ImGui.TextDisabled("Paste a spell word or the riddle's question line.");
    }

    private void RenderTracker()
    {
        if (ImGui.SmallButton("Refresh##grdref")) _ctx.Post(() => _ctx.Chat("[ILT Guardian] " + Attributes.Refresh()));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-read /myquests and /qb list (the server allows each once a minute).");
        ImGui.SameLine();
        DateTime synced = Attributes.SyncedUtc;
        ImGui.TextDisabled(synced == DateTime.MinValue ? "/qb not read this session" : $"/qb read {synced.ToLocalTime():t}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset counts##grdreset"))
            _ctx.Confirm("Reset turn-in counts", "Forget the turn-in counts and cooldown estimates taken from /qb wait stamps? (/myquests counts are not affected.)",
                Attributes.ResetCounts, "Reset");

        var rows = Attributes.Snapshot;
        if (rows.Length > 0 && ImGui.BeginTable("##grdattr", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Attribute", ImGuiTableColumnFlags.None, 1.0f);
            ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.None, 1.3f);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.None, 0.35f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.None, 0.9f);
            ImGui.TableSetupColumn("Next", ImGuiTableColumnFlags.None, 1.5f);
            ImGui.TableHeadersRow();
            foreach (var r in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Attribute);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Npc);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Count.ToString());
                ImGui.TableNextColumn();
                var col = r.Ready ? LegacyDashboardRenderer.ColGreen
                    : r.Status == "Unknown" ? LegacyDashboardRenderer.ColTextMute
                    : LegacyDashboardRenderer.ColAmber;
                ImGui.TextColored(col, r.Status);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(r.FromMyQuests ? "From /myquests (exact)." : "From /qb wait stamps (count and timer are estimates).");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Eta);
            }
            ImGui.EndTable();
        }

        int hours = S.CooldownHoursAssumed;
        ImGui.SetNextItemWidth(140);
        if (ImGui.SliderInt("Assumed cooldown (h)##grdhours", ref hours, 1, 72)) S.CooldownHoursAssumed = hours;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Used only for rows /myquests doesn't report: how long a /qb wait stamp lasts.");
    }

    /// <summary>
    /// Mini Remote line: the last answer (for AnswerShownFor) with a Give / Stop button.
    /// Draws nothing when there is no recent answer. Render thread.
    /// </summary>
    public void RenderRemoteLine()
    {
        string answer = _lastAnswer, npc = _lastNpc;
        if (answer.Length == 0 || LastAnswerAge > AnswerShownFor) return;
        ImGui.TextColored(LegacyDashboardRenderer.ColTextMute, "Guardian:");
        ImGui.SameLine();
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, answer);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Give to {npc}\nHand-in: {_handInStatus}");
        ImGui.SameLine();
        if (HandInRunning)
        {
            if (ImGui.SmallButton("Stop##mrgrd")) _ctx.Post(() => StopHandIn());
        }
        else if (ImGui.SmallButton("Give##mrgrd"))
        {
            _ctx.Post(() => StartHandIn(answer, npc, asked: true));
        }
    }
}
