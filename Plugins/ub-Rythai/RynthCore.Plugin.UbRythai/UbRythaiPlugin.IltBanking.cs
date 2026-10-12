using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ImGuiNET;
using System.Numerics;

namespace RynthCore.Plugin.UbRythai;

public sealed partial class UbRythaiPlugin
{
    private enum BankTransferItem
    {
        Pyreals,
        Luminance,
        LegendaryKeys,
        MythicalKeys,
        EnlightenedCoins,
        WeaklyEnlightenedCoins,
    }

    private struct UbBankData
    {
        public long Pyreals;
        public long Luminance;
        public int LegendaryKeys;
        public int MythicalKeys;
        public int EnlightenedCoins;
        public int WeaklyEnlightenedCoins;
        public DateTime LastUpdatedUtc;
    }

    private static readonly string[] _leaftideModeLabels = { "Auto", "On", "Off" };
    private static readonly string[] _bankTransferItemLabels =
    {
        "Pyreals", "Luminance", "Legendary Keys", "Mythical Keys", "Enlightened Coins", "Weakly Enlightened Coins"
    };

    private UbBankData _bankData;
    private DateTime _lastBankRefreshUtc = DateTime.MinValue;
    private DateTime _nextBankRefreshUtc = DateTime.MinValue;
    private DateTime _nextAutoTransferUtc = DateTime.MinValue;
    private bool _bankRefreshPending;
    private string _bankTransferAmountText = "";
    private string _bankTransferTargetText = "";
    private int _bankTransferItemIndex;

    private static string NormalizeLeaftideMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "Auto";
        return mode.Trim().ToLowerInvariant() switch
        {
            "on" => "On",
            "off" => "Off",
            _ => "Auto",
        };
    }

    private bool CurrentWorldMatchesLeaftideAutoList()
    {
        if (!Host.HasGetWorldName || !Host.TryGetWorldName(out string worldName) || string.IsNullOrWhiteSpace(worldName))
            return false;

        string[] tokens = (_settings.LeaftideAutoWorldNames ?? "")
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        foreach (string token in tokens)
        {
            if (string.Equals(token, worldName.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool IsLeaftideFeaturesEnabled()
    {
        return NormalizeLeaftideMode(_settings.LeaftideFeaturesMode) switch
        {
            "On" => true,
            "Off" => false,
            _ => CurrentWorldMatchesLeaftideAutoList(),
        };
    }

    private bool IsLeaftideBankingEnabled()
    {
        string mode = NormalizeLeaftideMode(_settings.LeaftideFeaturesMode);
        if (mode == "Off")
            return false;
        if (mode == "On")
            return _settings.LeaftideManualAllowBanking;
        return CurrentWorldMatchesLeaftideAutoList();
    }

    private void HandleLeaftideCommand(string[] parts)
    {
        string action = parts.Length >= 3 ? (parts[2] ?? "").Trim().ToLowerInvariant() : "";
        if (action is "on" or "off" or "auto")
        {
            _settings.LeaftideFeaturesMode = action switch
            {
                "on" => "On",
                "off" => "Off",
                _ => "Auto",
            };
            Host.WriteToChat($"[ub-Rythai] ILT mode set to {_settings.LeaftideFeaturesMode}.", 1);
            return;
        }

        string worldName = "(unknown)";
        if (Host.HasGetWorldName && Host.TryGetWorldName(out string world) && !string.IsNullOrWhiteSpace(world))
            worldName = world.Trim();

        bool autoMatch = CurrentWorldMatchesLeaftideAutoList();
        bool iltEnabled = IsLeaftideFeaturesEnabled();
        bool bankEnabled = IsLeaftideBankingEnabled();
        Host.WriteToChat(
            $"[ub-Rythai] ILT mode={NormalizeLeaftideMode(_settings.LeaftideFeaturesMode)} | features={(iltEnabled ? "ON" : "OFF")} | banking={(bankEnabled ? "ON" : "OFF")} | world='{worldName}' | autoMatch={(autoMatch ? "yes" : "no")} | names='{_settings.LeaftideAutoWorldNames}'",
            1);
        Host.WriteToChat("[ub-Rythai] Usage: /ub leaftide on|off|auto", 1);
    }

    private void HandleBankCommand(string[] parts, string rawText)
    {
        if (!_settings.BankToolsEnabled)
        {
            Host.WriteToChat("[ub-Rythai] banking tools are disabled in settings.", 1);
            return;
        }

        if (!IsLeaftideBankingEnabled())
        {
            Host.WriteToChat("[ub-Rythai] banking is gated off (use /ub leaftide, auto world list, or manual banking flag).", 1);
            return;
        }

        string action = parts.Length >= 3 ? (parts[2] ?? "").Trim().ToLowerInvariant() : "status";
        if (action == "status")
        {
            Host.WriteToChat(
                $"[ub-Rythai] bank: updated={( _bankData.LastUpdatedUtc == DateTime.MinValue ? "never" : _bankData.LastUpdatedUtc.ToLocalTime().ToString("HH:mm:ss"))}, p={_bankData.Pyreals:N0}, lum={_bankData.Luminance:N0}, lk={_bankData.LegendaryKeys:N0}, mk={_bankData.MythicalKeys:N0}, ec={_bankData.EnlightenedCoins:N0}, wec={_bankData.WeaklyEnlightenedCoins:N0}",
                1);
            return;
        }

        if (action == "refresh")
        {
            RequestBankRefresh("manual");
            return;
        }

        if (action == "transfer")
        {
            if (parts.Length < 6)
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub bank transfer <p|l|k|mk|e|we> <amount> <target>", 1);
                return;
            }

            if (!TryParseBankTransferItem(parts[3], out BankTransferItem item))
            {
                Host.WriteToChat("[ub-Rythai] transfer type must be p|l|k|mk|e|we.", 1);
                return;
            }

            long amount = ParseBankAmount(parts[4]);
            if (amount <= 0)
            {
                Host.WriteToChat("[ub-Rythai] transfer amount must be > 0 (supports k/m/b/t suffix).", 1);
                return;
            }

            int targetStart = rawText.IndexOf(parts[5], StringComparison.Ordinal);
            string target = targetStart >= 0 ? rawText[targetStart..].Trim().Trim('"') : parts[5].Trim().Trim('"');
            string? transferCommand = BuildBankTransferCommand(item, amount, target);
            if (string.IsNullOrWhiteSpace(transferCommand))
            {
                Host.WriteToChat("[ub-Rythai] invalid transfer target.", 1);
                return;
            }

            if (TryDispatchChatCommand(transferCommand, "bank transfer"))
            {
                Host.WriteToChat($"[ub-Rythai] sent {GetBankTransferItemDisplayName(item)} transfer: {amount:N0} to {target}.", 1);
            }
            else
            {
                Host.WriteToChat("[ub-Rythai] transfer command dispatch failed.", 1);
            }
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub bank status|refresh|transfer <p|l|k|mk|e|we> <amount> <target>", 1);
    }

    private void RequestBankRefresh(string reason)
    {
        if (!Host.HasInvokeChatParser)
            return;
        _bankRefreshPending = true;
        _nextBankRefreshUtc = DateTime.UtcNow.AddMilliseconds(250);
        LogAt(UbRythaiLogLevel.Debug, $"queued /bank refresh ({reason}).");
    }

    private void TickBanking()
    {
        if (!_settings.BankToolsEnabled || !IsLeaftideBankingEnabled() || !Host.HasInvokeChatParser)
            return;

        if (_bankRefreshPending && DateTime.UtcNow >= _nextBankRefreshUtc)
        {
            if ((DateTime.UtcNow - _lastBankRefreshUtc).TotalSeconds >= 2 &&
                TryDispatchChatCommand("/bank", "bank refresh"))
            {
                _lastBankRefreshUtc = DateTime.UtcNow;
                _bankRefreshPending = false;
            }
        }

        if (_settings.BankAutoTransferEnabled)
            TickAutoTransfer();
    }

    private void TickAutoTransfer()
    {
        if (DateTime.UtcNow < _nextAutoTransferUtc)
            return;

        int intervalMinutes = Math.Clamp(_settings.BankAutoTransferIntervalMinutes, 1, 240);
        _nextAutoTransferUtc = DateTime.UtcNow.AddMinutes(intervalMinutes);

        string target = (_settings.BankAutoTransferTarget ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target))
            return;

        int pct = Math.Clamp(_settings.BankAutoTransferPercent, 1, 100);
        int sent = 0;
        sent += TryAutoTransferItem(_settings.BankAutoTransferPyreals, BankTransferItem.Pyreals, _bankData.Pyreals, pct, target);
        sent += TryAutoTransferItem(_settings.BankAutoTransferLuminance, BankTransferItem.Luminance, _bankData.Luminance, pct, target);
        sent += TryAutoTransferItem(_settings.BankAutoTransferLegendaryKeys, BankTransferItem.LegendaryKeys, _bankData.LegendaryKeys, pct, target);
        sent += TryAutoTransferItem(_settings.BankAutoTransferMythicalKeys, BankTransferItem.MythicalKeys, _bankData.MythicalKeys, pct, target);
        sent += TryAutoTransferItem(_settings.BankAutoTransferEnlightenedCoins, BankTransferItem.EnlightenedCoins, _bankData.EnlightenedCoins, pct, target);
        sent += TryAutoTransferItem(_settings.BankAutoTransferWeaklyEnlightenedCoins, BankTransferItem.WeaklyEnlightenedCoins, _bankData.WeaklyEnlightenedCoins, pct, target);
        if (sent > 0)
            RequestBankRefresh("auto-transfer follow-up");
    }

    private int TryAutoTransferItem(bool enabled, BankTransferItem item, long currentBalance, int pct, string target)
    {
        if (!enabled || currentBalance <= 0)
            return 0;
        long amount = (long)Math.Floor(currentBalance * (pct / 100.0));
        if (amount <= 0)
            return 0;
        string? cmd = BuildBankTransferCommand(item, amount, target);
        if (string.IsNullOrWhiteSpace(cmd))
            return 0;
        return TryDispatchChatCommand(cmd, "bank auto-transfer") ? 1 : 0;
    }

    private void TryParseBankChatLine(string line)
    {
        if (!_settings.BankToolsEnabled || !IsLeaftideBankingEnabled())
            return;
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("[BANK]", StringComparison.OrdinalIgnoreCase))
            return;

        bool changed = false;
        changed |= TryParseBankCurrencyLine(line, "Pyreals:", v => _bankData.Pyreals = v);
        changed |= TryParseBankCurrencyLine(line, "Luminance:", v => _bankData.Luminance = v);
        changed |= TryParseBankCurrencyLine(line, "Legendary Keys:", v => _bankData.LegendaryKeys = (int)Math.Clamp(v, 0, int.MaxValue));
        changed |= TryParseBankCurrencyLine(line, "Mythical Keys:", v => _bankData.MythicalKeys = (int)Math.Clamp(v, 0, int.MaxValue));
        changed |= TryParseBankCurrencyLine(line, "Enlightened Coins:", v => _bankData.EnlightenedCoins = (int)Math.Clamp(v, 0, int.MaxValue));
        changed |= TryParseBankCurrencyLine(line, "Weakly Enlightened Coins:", v => _bankData.WeaklyEnlightenedCoins = (int)Math.Clamp(v, 0, int.MaxValue));
        if (changed)
            _bankData.LastUpdatedUtc = DateTime.UtcNow;
    }

    private static bool TryParseBankCurrencyLine(string line, string marker, Action<long> set)
    {
        int idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        string valuePart = line[(idx + marker.Length)..].Trim();
        Match m = Regex.Match(valuePart, @"^(?<num>[\d,]+)");
        if (!m.Success)
            return false;
        if (!long.TryParse(m.Groups["num"].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            return false;

        set(parsed);
        return true;
    }

    private static bool TryParseBankTransferItem(string token, out BankTransferItem item)
    {
        item = BankTransferItem.Pyreals;
        string normalized = (token ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "p" or "pyreals" => Assign(BankTransferItem.Pyreals, out item),
            "l" or "lum" or "luminance" => Assign(BankTransferItem.Luminance, out item),
            "k" or "lk" or "legendary" => Assign(BankTransferItem.LegendaryKeys, out item),
            "mk" or "mythical" => Assign(BankTransferItem.MythicalKeys, out item),
            "e" or "ec" or "enlightened" => Assign(BankTransferItem.EnlightenedCoins, out item),
            "we" or "wec" or "weakly" => Assign(BankTransferItem.WeaklyEnlightenedCoins, out item),
            _ => false,
        };
    }

    private static bool Assign(BankTransferItem v, out BankTransferItem item)
    {
        item = v;
        return true;
    }

    private static long ParseBankAmount(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return 0;
        Match match = Regex.Match(input.Trim().ToLowerInvariant(), @"^(?<num>[\d,.]+)(?<sfx>[kmbt]?)$");
        if (!match.Success)
            return 0;
        if (!double.TryParse(match.Groups["num"].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return 0;
        value *= match.Groups["sfx"].Value switch
        {
            "k" => 1_000d,
            "m" => 1_000_000d,
            "b" => 1_000_000_000d,
            "t" => 1_000_000_000_000d,
            _ => 1d,
        };
        return value <= 0 ? 0 : (long)Math.Floor(value);
    }

    private static string? BuildBankTransferCommand(BankTransferItem item, long amount, string targetName)
    {
        if (amount <= 0)
            return null;
        string target = (targetName ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target))
            return null;

        string type = item switch
        {
            BankTransferItem.Pyreals => "p",
            BankTransferItem.Luminance => "l",
            BankTransferItem.LegendaryKeys => "k",
            BankTransferItem.MythicalKeys => "mk",
            BankTransferItem.EnlightenedCoins => "e",
            BankTransferItem.WeaklyEnlightenedCoins => "we",
            _ => "p",
        };

        return $"/bank t {type} {amount} {target}";
    }

    private static string GetBankTransferItemDisplayName(BankTransferItem item)
    {
        return item switch
        {
            BankTransferItem.Pyreals => "Pyreals",
            BankTransferItem.Luminance => "Luminance",
            BankTransferItem.LegendaryKeys => "Legendary Keys",
            BankTransferItem.MythicalKeys => "Mythical Keys",
            BankTransferItem.EnlightenedCoins => "Enlightened Coins",
            BankTransferItem.WeaklyEnlightenedCoins => "Weakly Enlightened Coins",
            _ => "Unknown",
        };
    }

    private void RenderIltAndBankUi()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "P3 ILT Gameplay + Bank");

        int modeIndex = NormalizeLeaftideMode(_settings.LeaftideFeaturesMode) switch
        {
            "On" => 1,
            "Off" => 2,
            _ => 0,
        };
        if (ImGui.Combo("ILT mode##iltmode", ref modeIndex, _leaftideModeLabels, _leaftideModeLabels.Length))
            _settings.LeaftideFeaturesMode = _leaftideModeLabels[Math.Clamp(modeIndex, 0, _leaftideModeLabels.Length - 1)];
        ImGui.InputText("Auto world names##iltautonames", ref _settings.LeaftideAutoWorldNames, 512);
        ImGui.Checkbox("Manual On allows banking##iltbankallow", ref _settings.LeaftideManualAllowBanking);
        bool autoMatch = CurrentWorldMatchesLeaftideAutoList();
        bool iltEnabled = IsLeaftideFeaturesEnabled();
        bool bankEnabled = IsLeaftideBankingEnabled();
        string world = "(unknown)";
        if (Host.HasGetWorldName && Host.TryGetWorldName(out string worldName) && !string.IsNullOrWhiteSpace(worldName))
            world = worldName.Trim();
        ImGui.TextDisabled($"World={world}; autoMatch={(autoMatch ? "yes" : "no")}; features={(iltEnabled ? "ON" : "OFF")}; banking={(bankEnabled ? "ON" : "OFF")}");

        ImGui.Checkbox("Enable spellcraft queue (phase-in)##p3sp", ref _settings.EnableSpellcraftQueue);
        ImGui.Checkbox("Enable temple/guardian tools (phase-in)##p3tg", ref _settings.EnableTempleGuardianTools);
        ImGui.Checkbox("Enable fellowship games (phase-in)##p3fg", ref _settings.EnableFellowshipGames);
        ImGui.Checkbox("Enable quest/economy tools (phase-in)##p3qe", ref _settings.EnableQuestEconomyTools);

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "UB-ILT Ports (Mini Remote / XPMeter / Guardian / Bank)");
        ImGui.Checkbox("XP meter overlay##ubxp", ref _settings.XpMeterOverlayEnabled);
        ImGui.Checkbox("XP meter show totals##ubxpt", ref _settings.XpMeterShowTotals);
        ImGui.Checkbox("XP meter show time##ubxptime", ref _settings.XpMeterShowTime);
        ImGui.Checkbox("Mini remote portal gem buttons##ubmr", ref _settings.MiniRemotePortalGemsEnabled);
        ImGui.Checkbox("Temple guardian auto-chat##ubtgchat", ref _settings.TempleGuardianAutoChat);
        ImGui.Checkbox("Temple guardian auto hand-in (best-effort)##ubtghand", ref _settings.TempleGuardianAutoHandIn);

        ImGui.Checkbox("Bank tools enabled##ubbanken", ref _settings.BankToolsEnabled);
        ImGui.Checkbox("Bank auto-refresh on login##ubbanklogin", ref _settings.BankAutoRefreshOnLogin);
        ImGui.TextDisabled($"Bank: p={_bankData.Pyreals:N0}, lum={_bankData.Luminance:N0}, lk={_bankData.LegendaryKeys:N0}, mk={_bankData.MythicalKeys:N0}, ec={_bankData.EnlightenedCoins:N0}, wec={_bankData.WeaklyEnlightenedCoins:N0}");
        ImGui.TextDisabled($"Bank updated: {(_bankData.LastUpdatedUtc == DateTime.MinValue ? "never" : _bankData.LastUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}");
        if (ImGui.Button("Refresh bank now##ubbankrefresh"))
            RequestBankRefresh("ui");

        ImGui.InputText("Transfer amount##ubbankamt", ref _bankTransferAmountText, 64);
        ImGui.InputText("Transfer target##ubbanktarget", ref _bankTransferTargetText, 128);
        ImGui.Combo("Transfer item##ubbankitem", ref _bankTransferItemIndex, _bankTransferItemLabels, _bankTransferItemLabels.Length);
        if (ImGui.Button("Send bank transfer##ubbanksend"))
        {
            long amount = ParseBankAmount(_bankTransferAmountText);
            BankTransferItem item = (BankTransferItem)Math.Clamp(_bankTransferItemIndex, 0, _bankTransferItemLabels.Length - 1);
            string? cmd = BuildBankTransferCommand(item, amount, _bankTransferTargetText);
            if (!string.IsNullOrWhiteSpace(cmd) && TryDispatchChatCommand(cmd, "bank ui transfer"))
            {
                Host.WriteToChat($"[ub-Rythai] sent {GetBankTransferItemDisplayName(item)} transfer: {amount:N0} to {_bankTransferTargetText.Trim()}.", 1);
            }
            else
            {
                Host.WriteToChat("[ub-Rythai] invalid bank transfer input.", 1);
            }
        }

        ImGui.Checkbox("Auto transfer enabled##ubbankautoen", ref _settings.BankAutoTransferEnabled);
        ImGui.InputText("Auto transfer target##ubbankautotarget", ref _settings.BankAutoTransferTarget, 128);
        ImGui.InputInt("Auto transfer percent##ubbankautopct", ref _settings.BankAutoTransferPercent);
        ImGui.InputInt("Auto transfer interval min##ubbankautoint", ref _settings.BankAutoTransferIntervalMinutes);
        ImGui.Checkbox("Auto transfer pyreals##ubbankautop", ref _settings.BankAutoTransferPyreals);
        ImGui.Checkbox("Auto transfer luminance##ubbankautol", ref _settings.BankAutoTransferLuminance);
        ImGui.Checkbox("Auto transfer legendary keys##ubbankautolk", ref _settings.BankAutoTransferLegendaryKeys);
        ImGui.Checkbox("Auto transfer mythical keys##ubbankautomk", ref _settings.BankAutoTransferMythicalKeys);
        ImGui.Checkbox("Auto transfer enlightened coins##ubbankautoe", ref _settings.BankAutoTransferEnlightenedCoins);
        ImGui.Checkbox("Auto transfer weakly enlightened coins##ubbankautowe", ref _settings.BankAutoTransferWeaklyEnlightenedCoins);
        ImGui.TextDisabled("Commands: /ub leaftide … | /ub bank status|refresh|transfer … | /ub guardian …");
    }
}
