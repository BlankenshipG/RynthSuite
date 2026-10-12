using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// RynthCore plugin: .NET 10 / NativeAOT only. Classic UB-ILT remains net48 in the ub-IT / UB repos.
/// </summary>
public sealed partial class UbRythaiPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("ub-Rythai");

    internal static readonly IntPtr VersionPointer =
        Marshal.StringToHGlobalAnsi(PluginBuildInfo.AssemblyInformationalVersion);

    private readonly UbRythaiSettings _settings = new();
    private readonly string[] _logLevelLabels = { "Trace", "Debug", "Info", "Warn", "Error" };
    private UbRythaiChatCommandAutomation? _automation;
    private UbRythaiFileLogger? _fileLogger;
    private string _characterName = string.Empty;
    private bool _playerWasAlive = true;
    private DateTime _lastLogOutOnDeath = DateTime.MinValue;
    private uint _corpseCreateCount;
    private uint _vendorSpamFilteredCount;
    private uint _busyFilteredCount;
    private uint _fizzleFilteredCount;
    private uint _deathFilteredCount;
    private uint _attackEvadeFilteredCount;
    private uint _defenseEvadeFilteredCount;
    private uint _attackResistFilteredCount;
    private uint _defenseResistFilteredCount;
    private uint _spellCastMineFilteredCount;
    private uint _spellCastOthersFilteredCount;
    private uint _spellExpiresFilteredCount;
    private uint _periodicHealingFilteredCount;
    private uint _failedAssessFilteredCount;
    private uint _killTaskCompleteFilteredCount;
    private DateTime _lastAutoTradeAcceptUtc = DateTime.MinValue;
    private DateTime _lastAutoPercentConfirmUtc = DateTime.MinValue;
    private DateTime _lastChatDispatchUtc = DateTime.MinValue;
    private uint _busyGuardBlockedByBusyCount;
    private uint _busyGuardBlockedByGapCount;
    private readonly UbRythaiXpMeterState _xpMeter = new();
    private readonly UbRythaiTempleGuardianAutomation _templeGuardianAutomation = new();
    private string _lastGuardianPrompt = "";
    private string _lastGuardianAnswer = "";
    private string _lastGuardianNpc = "";
    private string _lastGuardianFingerprint = "";
    private DateTime _lastGuardianEmitUtc = DateTime.MinValue;
    private bool _initialized;
    private bool _loginComplete;
    private bool _windowVisible;
    private static bool _imguiResolverConfigured;

    public override int Initialize()
    {
        if (Host.ImGuiContext == IntPtr.Zero)
            return 11;

        EnsureImGuiResolver();
        // File logger defaults to local install "Logs" folder and rolls by size.
        _fileLogger = new UbRythaiFileLogger(AppContext.BaseDirectory);
        _automation = new UbRythaiChatCommandAutomation(Host, () => _settings, LogAt, TryDispatchChatCommand);
        _initialized = true;
        _loginComplete = false;
        _windowVisible = false;
        _playerWasAlive = true;
        _lastLogOutOnDeath = DateTime.MinValue;
        LogAt(UbRythaiLogLevel.Info, $"ub-Rythai: initialized ({PluginBuildInfo.DisplayVersion}; TFM=net10.0-windows, not net48).");
        return 0;
    }

    public override void Shutdown()
    {
        if (!string.IsNullOrEmpty(_characterName))
            UbRythaiSettingsStore.Save(_characterName, _settings);

        _initialized = false;
        _loginComplete = false;
        _windowVisible = false;
        _automation = null;
        _fileLogger = null;
        _characterName = string.Empty;
        _playerWasAlive = true;
    }

    public override void OnLoginComplete()
    {
        if (!_initialized)
            return;

        _characterName = string.Empty;
        uint pid = Host.GetPlayerId();
        if (pid != 0 && Host.HasGetObjectName && Host.TryGetObjectName(pid, out string? n) && !string.IsNullOrWhiteSpace(n))
            _characterName = n.Trim();

        UbRythaiSettings loaded = UbRythaiSettingsStore.LoadOrDefault(_characterName);
        CopySettings(loaded, _settings);

        _loginComplete = true;
        _automation?.OnLoginComplete();
        RunLoginMacroCommands();
        _xpMeter.SeedFromPlayer(Host, pid);
        LogAt(UbRythaiLogLevel.Info, $"login complete for character '{_characterName}'.");
        Host.WriteToChat(
            $"[ub-Rythai] Login complete — {PluginBuildInfo.DisplayVersion}. Bar action toggles window. /ub help",
            1);

        if (_settings.BankToolsEnabled && _settings.BankAutoRefreshOnLogin && IsLeaftideBankingEnabled())
        {
            _nextAutoTransferUtc = DateTime.UtcNow.AddMinutes(Math.Clamp(_settings.BankAutoTransferIntervalMinutes, 1, 240));
            RequestBankRefresh("login");
        }
    }

    public override void OnTick()
    {
        if (!_initialized || !_loginComplete)
            return;

        _automation?.OnTick();
        ApplyClientFpsPolicy();
        CheckLogOutOnDeath();
        TickBanking();

        uint pid = Host.GetPlayerId();
        if (pid != 0)
            _xpMeter.Tick(Host, pid);

        if (_settings.EnableTempleGuardianTools && _settings.TempleGuardianAutoHandIn)
        {
            _templeGuardianAutomation.Tick(
                cmd => TryDispatchChatCommand(cmd, "guardian automation"),
                msg => LogAt(UbRythaiLogLevel.Info, msg));
        }
    }

    public override void OnBarAction()
    {
        if (!_initialized || !_loginComplete)
            return;

        _windowVisible = !_windowVisible;
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (!_initialized || !_loginComplete || string.IsNullOrWhiteSpace(text))
            return;

        string trimmed = text.Trim();
        if (!trimmed.StartsWith("/ub", StringComparison.OrdinalIgnoreCase))
            return;

        eat = 1;
        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Host.WriteToChat("[ub-Rythai] /ub help | /ub build | /ub leaftide ... | /ub bank ... | /ub loginq run | /ub periodic status | /ub p2 status | /ub log status | /ub autotradeaccept ... | /ub autopercent ... | /ub guard ... | /ub xpmeter ... | /ub portal ... | /ub guardian ...", 1);
            return;
        }

        if (parts[1].Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            Host.WriteToChat($"[ub-Rythai] {PluginBuildInfo.DisplayVersion} — {PluginBuildInfo.AssemblyName}", 1);
            return;
        }

        if (parts[1].Equals("loginq", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub loginq run|diag|clear", 1);
                return;
            }

            if (parts[2].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                _automation?.RequestLoginQueueReplay();
                return;
            }

            if (parts[2].Equals("diag", StringComparison.OrdinalIgnoreCase))
            {
                string status = _automation?.GetLoginQueueDiagnosticsStatusLine() ?? "automation not ready.";
                Host.WriteToChat($"[ub-Rythai] {status}", 1);
                return;
            }

            if (parts[2].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                _automation?.ClearLoginQueueDiagnostics();
                Host.WriteToChat("[ub-Rythai] Login queue diagnostics cleared.", 1);
                return;
            }

            Host.WriteToChat("[ub-Rythai] Usage: /ub loginq run|diag|clear", 1);
            return;
        }

        if (parts[1].Equals("periodic", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3 || !parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub periodic status", 1);
                return;
            }

            string line = _automation?.GetPeriodicStatusLine() ?? "automation not ready.";
            Host.WriteToChat($"[ub-Rythai] {line}", 1);
            return;
        }

        if (parts[1].Equals("p2", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub p2 status|reset", 1);
                return;
            }

            if (parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Host.WriteToChat(
                    $"[ub-Rythai] P2 filters: busy={_busyFilteredCount}, fizzle={_fizzleFilteredCount}, vendor={_vendorSpamFilteredCount}, death={_deathFilteredCount}, atkEvade={_attackEvadeFilteredCount}, defEvade={_defenseEvadeFilteredCount}, atkResist={_attackResistFilteredCount}, defResist={_defenseResistFilteredCount}, castMine={_spellCastMineFilteredCount}, castOthers={_spellCastOthersFilteredCount}, expires={_spellExpiresFilteredCount}, periodicHeal={_periodicHealingFilteredCount}, failedAssess={_failedAssessFilteredCount}, killTask={_killTaskCompleteFilteredCount}; corpses seen={_corpseCreateCount}.",
                    1);
                return;
            }

            if (parts[2].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                ResetP2Counters();
                Host.WriteToChat("[ub-Rythai] P2 counters reset.", 1);
                return;
            }

            Host.WriteToChat("[ub-Rythai] Usage: /ub p2 status|reset", 1);
            return;
        }

        if (parts[1].Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub log status | /ub log open", 1);
                return;
            }

            if (parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                string logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
                string level = ((UbRythaiLogLevel)Math.Clamp(_settings.LogLevel, 0, 4)).ToString();
                Host.WriteToChat($"[ub-Rythai] log level={level}, folder={logsDir}", 1);
                return;
            }

            if (parts[2].Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                OpenLogsFolder();
                return;
            }

            Host.WriteToChat("[ub-Rythai] Usage: /ub log status | /ub log open", 1);
            return;
        }

        if (parts[1].Equals("autotradeaccept", StringComparison.OrdinalIgnoreCase))
        {
            HandleAutoTradeAcceptCommand(parts, trimmed);
            return;
        }

        if (parts[1].Equals("autopercent", StringComparison.OrdinalIgnoreCase))
        {
            HandleAutoPercentConfirmationCommand(parts, trimmed);
            return;
        }

        if (parts[1].Equals("guard", StringComparison.OrdinalIgnoreCase))
        {
            HandleBusyGuardCommand(parts);
            return;
        }

        if (parts[1].Equals("xpmeter", StringComparison.OrdinalIgnoreCase))
        {
            HandleXpMeterCommand(parts);
            return;
        }

        if (parts[1].Equals("portal", StringComparison.OrdinalIgnoreCase))
        {
            HandlePortalGemCommand(parts, trimmed);
            return;
        }

        if (parts[1].Equals("guardian", StringComparison.OrdinalIgnoreCase))
        {
            HandleGuardianCommand(parts, trimmed);
            return;
        }

        if (parts[1].Equals("leaftide", StringComparison.OrdinalIgnoreCase) ||
            parts[1].Equals("ilt", StringComparison.OrdinalIgnoreCase))
        {
            HandleLeaftideCommand(parts);
            return;
        }

        if (parts[1].Equals("bank", StringComparison.OrdinalIgnoreCase))
        {
            HandleBankCommand(parts, trimmed);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Unknown command. Use /ub help.", 1);
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (!_initialized || !_loginComplete || string.IsNullOrWhiteSpace(text))
            return;

        string line = text.Trim();
        TryParseBankChatLine(line);
        TryAutoTradeAcceptFromChatLine(line);
        TryAutoPercentConfirmationFromChatLine(line);
        TryTempleGuardianFromChatLine(line, chatType);

        if (!_settings.ChatFilterEnabled)
            return;
        if (_settings.FilterBusyLines &&
            (line.Contains("You're too busy", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("You are too busy", StringComparison.OrdinalIgnoreCase)))
        {
            _busyFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterSpellFizzleLines &&
            (line.Contains("fizzle", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("fails to affect", StringComparison.OrdinalIgnoreCase)))
        {
            _fizzleFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterVendorSpamLines &&
            (line.Contains("vendor", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("sold to", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("bought from", StringComparison.OrdinalIgnoreCase)))
        {
            _vendorSpamFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterMonsterDeathLines &&
            (line.Contains("dies", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("has been slain", StringComparison.OrdinalIgnoreCase)))
        {
            _deathFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterAttackEvadesLines && line.Contains(" evaded your attack.", StringComparison.OrdinalIgnoreCase))
        {
            _attackEvadeFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterDefenseEvadesLines && line.StartsWith("You evaded ", StringComparison.OrdinalIgnoreCase))
        {
            _defenseEvadeFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterAttackResistsLines && line.Contains(" resists your spell", StringComparison.OrdinalIgnoreCase))
        {
            _attackResistFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterDefenseResistsLines &&
            (line.StartsWith("You resist the spell cast by ", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("You have no appropriate target", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("You are an invalid target for the spell", StringComparison.OrdinalIgnoreCase)))
        {
            _defenseResistFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterSpellCastMineLines &&
            line.StartsWith("You say, \"", StringComparison.OrdinalIgnoreCase) &&
            line.EndsWith("\"", StringComparison.Ordinal))
        {
            _spellCastMineFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterSpellCastOthersLines &&
            line.Contains(" says, \"", StringComparison.OrdinalIgnoreCase) &&
            line.EndsWith("\"", StringComparison.Ordinal))
        {
            _spellCastOthersFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterSpellExpiresLines &&
            !line.Contains("Brilliance", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("Prodigal", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("Spectral", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("has expired.", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("have expired.", StringComparison.OrdinalIgnoreCase)))
        {
            _spellExpiresFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterPeriodicHealingLines &&
            line.StartsWith("You receive ", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(" points of periodic healing", StringComparison.OrdinalIgnoreCase))
        {
            _periodicHealingFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterFailedAssessLines &&
            line.EndsWith("tried and failed to assess you!", StringComparison.OrdinalIgnoreCase))
        {
            _failedAssessFilteredCount++;
            eat = 1;
            return;
        }

        if (_settings.FilterKillTaskCompleteLines &&
            line.StartsWith("You have killed ", StringComparison.OrdinalIgnoreCase) &&
            line.EndsWith("Your task is complete!", StringComparison.OrdinalIgnoreCase))
        {
            _killTaskCompleteFilteredCount++;
            eat = 1;
        }

    }

    public override void OnCreateObject(uint objectId)
    {
        if (!_loginComplete || !Host.HasGetObjectName || objectId == 0)
            return;

        if (Host.TryGetObjectName(objectId, out string name) &&
            name.Contains("corpse", StringComparison.OrdinalIgnoreCase))
        {
            _corpseCreateCount++;
        }
    }

    public override void OnRender()
    {
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero)
            return;

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            bool iltEnabled = IsLeaftideFeaturesEnabled();
            if (iltEnabled && _settings.XpMeterOverlayEnabled)
                RenderXpMeterOverlay();

            if (iltEnabled && _settings.MiniRemotePortalGemsEnabled)
                RenderMiniRemotePortalGemOverlay();

            if (!_windowVisible)
                return;

            ImGui.SetNextWindowSize(new Vector2(520, 460), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("ub-Rythai (.NET 10)###UbRythaiMain"))
            {
                ImGui.TextUnformatted(PluginBuildInfo.DisplayVersion);
                ImGui.Separator();
                ImGui.TextWrapped(
                    "This DLL is net10.0-windows + NativeAOT for RynthCore. " +
                    "Decal Utility Belt / UB-ILT is net48 (and net8/net10) in UB/ and ub-IT/ — a different host.");
                ImGui.TextDisabled($"Log files: {Path.Combine(AppContext.BaseDirectory, "Logs")}");

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "Logging");
                int levelIndex = Math.Clamp(_settings.LogLevel, 0, _logLevelLabels.Length - 1);
                if (ImGui.Combo("Log level##ubLogLevel", ref levelIndex, _logLevelLabels, _logLevelLabels.Length))
                {
                    _settings.LogLevel = levelIndex;
                    LogAt(UbRythaiLogLevel.Info, $"log level changed to {(UbRythaiLogLevel)_settings.LogLevel}.");
                }
                if (ImGui.Button("Open Logs Folder##ubLogOpen"))
                {
                    OpenLogsFolder();
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "P1 Foundation");
                ImGui.Checkbox("Login command queue##lr", ref _settings.LoginCommandsEnabled);
                ImGui.InputInt("Initial delay (ms)##l0", ref _settings.LoginCommandsInitialDelayMs);
                ImGui.InputInt("Gap (ms)##l1", ref _settings.LoginCommandsGapMs);
                float h1 = 120f;
                ImGui.InputTextMultiline("##loginLines", ref _settings.LoginCommandsText, 32_768,
                    new Vector2(-1, h1), ImGuiInputTextFlags.AllowTabInput);
                ImGui.TextDisabled("# or // = comment line");
                ImGui.Checkbox("Open main pack on login##p1omp", ref _settings.OpenMainPackOnLogin);
                ImGui.Checkbox("Run chat-resize command on login##p1chat", ref _settings.RunChatResizeOnLogin);
                ImGui.InputText("Chat resize command##p1chatcmd", ref _settings.ChatResizeCommand, 128);
                ImGui.Checkbox("Log out on death##p1lod", ref _settings.LogOutOnDeathEnabled);
                ImGui.InputInt("Death cooldown (sec)##p1lodcd", ref _settings.LogOutOnDeathCooldownSeconds);
                ImGui.Checkbox("Auto trade accept##p1ata", ref _settings.AutoTradeAcceptEnabled);
                ImGui.InputInt("Trade accept debounce (sec)##p1atad", ref _settings.AutoTradeAcceptDebounceSeconds);
                ImGui.InputText("Trade accept command##p1atacmd", ref _settings.AutoTradeAcceptCommand, 128);
                ImGui.InputText("Trade accept whitelist (regex; , or ;)##p1atawl", ref _settings.AutoTradeAcceptWhitelist, 512);
                ImGui.Checkbox("Auto percent confirmation##p1apc", ref _settings.AutoPercentConfirmationEnabled);
                ImGui.InputInt("Percent threshold##p1apcth", ref _settings.AutoPercentConfirmationThreshold);
                ImGui.InputInt("Percent debounce (sec)##p1apcd", ref _settings.AutoPercentConfirmationDebounceSeconds);
                ImGui.InputText("Percent confirm command##p1apccmd", ref _settings.AutoPercentConfirmationCommand, 128);
                ImGui.Checkbox("Busy-state guardrails##p1bg", ref _settings.BusyGuardrailsEnabled);
                ImGui.InputInt("Min chat command gap (ms)##p1bggap", ref _settings.BusyGuardrailsMinCommandGapMs);
                ImGui.InputInt("Busy-state threshold##p1bgth", ref _settings.BusyGuardrailsBusyStateThreshold);
                ImGui.TextDisabled($"Guard blocks: busy={_busyGuardBlockedByBusyCount}, gap={_busyGuardBlockedByGapCount}");
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "Login Queue Diagnostics");
                string loginDiagStatus = _automation?.GetLoginQueueDiagnosticsStatusLine() ?? "automation not ready.";
                ImGui.TextWrapped(loginDiagStatus);
                if (ImGui.Button("Clear login diagnostics##p1diagclr"))
                {
                    _automation?.ClearLoginQueueDiagnostics();
                }
                float diagHeight = 90f;
                if (ImGui.BeginChild("##loginDiagScroll", new Vector2(-1, diagHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
                {
                    var snapshot = _automation?.GetLoginQueueDiagnosticsSnapshot();
                    if (snapshot == null || snapshot.Count == 0)
                    {
                        ImGui.TextDisabled("No login queue attempts recorded yet.");
                    }
                    else
                    {
                        for (int i = snapshot.Count - 1; i >= 0; i--)
                        {
                            UbRythaiChatCommandAutomation.LoginQueueDiagnostic d = snapshot[i];
                            string state = d.Success ? "OK" : "FAIL";
                            ImGui.TextWrapped($"{state} {d.TimestampUtc:HH:mm:ss} L{d.SourceLine} {d.CommandPreview} ({d.Detail})");
                        }
                    }
                }
                ImGui.EndChild();

                ImGui.Spacing();
                ImGui.Checkbox("Periodic command batch##pr", ref _settings.PeriodicCommandsEnabled);
                ImGui.InputInt("Interval (sec)##p0", ref _settings.PeriodicIntervalSeconds);
                ImGui.InputInt("Gap in batch (ms)##p1", ref _settings.PeriodicCommandsGapMs);
                float h2 = 120f;
                ImGui.InputTextMultiline("##perLines", ref _settings.PeriodicCommandsText, 32_768,
                    new Vector2(-1, h2), ImGuiInputTextFlags.AllowTabInput);

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "P2 High-Traffic Parity");
                ImGui.Checkbox("Chat filter enabled##p2cf", ref _settings.ChatFilterEnabled);
                ImGui.Checkbox("Filter busy lines##p2busy", ref _settings.FilterBusyLines);
                ImGui.Checkbox("Filter spell fizzles##p2fiz", ref _settings.FilterSpellFizzleLines);
                ImGui.Checkbox("Filter vendor spam##p2ven", ref _settings.FilterVendorSpamLines);
                ImGui.Checkbox("Filter monster death lines##p2dea", ref _settings.FilterMonsterDeathLines);
                ImGui.Checkbox("Filter attack evades##p2aev", ref _settings.FilterAttackEvadesLines);
                ImGui.Checkbox("Filter defense evades##p2dev", ref _settings.FilterDefenseEvadesLines);
                ImGui.Checkbox("Filter attack resists##p2ars", ref _settings.FilterAttackResistsLines);
                ImGui.Checkbox("Filter defense resists##p2drs", ref _settings.FilterDefenseResistsLines);
                ImGui.Checkbox("Filter spell cast mine##p2scm", ref _settings.FilterSpellCastMineLines);
                ImGui.Checkbox("Filter spell cast others##p2sco", ref _settings.FilterSpellCastOthersLines);
                ImGui.Checkbox("Filter spell expires##p2sex", ref _settings.FilterSpellExpiresLines);
                ImGui.Checkbox("Filter periodic healing##p2phl", ref _settings.FilterPeriodicHealingLines);
                ImGui.Checkbox("Filter failed assess##p2fas", ref _settings.FilterFailedAssessLines);
                ImGui.Checkbox("Filter kill task complete##p2ktc", ref _settings.FilterKillTaskCompleteLines);
                if (ImGui.Button("Reset P2 counters##p2reset"))
                    ResetP2Counters();
                ImGui.TextDisabled($"P2 counters: busy={_busyFilteredCount} fizz={_fizzleFilteredCount} ven={_vendorSpamFilteredCount} death={_deathFilteredCount} atkEv={_attackEvadeFilteredCount} defEv={_defenseEvadeFilteredCount} atkRs={_attackResistFilteredCount} defRs={_defenseResistFilteredCount}");
                ImGui.TextDisabled($"P2 counters 2: castMe={_spellCastMineFilteredCount} castOther={_spellCastOthersFilteredCount} exp={_spellExpiresFilteredCount} heal={_periodicHealingFilteredCount} assess={_failedAssessFilteredCount} task={_killTaskCompleteFilteredCount}");
                ImGui.Checkbox("Client FPS policy##p2fps", ref _settings.ClientFpsPolicyEnabled);
                ImGui.InputInt("Focused FPS##p2ff", ref _settings.TargetFpsFocused);
                ImGui.InputInt("Background FPS##p2bf", ref _settings.TargetFpsBackground);
                ImGui.Checkbox("Window mover (reserved)##p2wm", ref _settings.WindowMoverEnabled);
                ImGui.Checkbox("Window mover auto-restore (reserved)##p2wma", ref _settings.WindowMoverAutoRestore);

                RenderIltAndBankUi();

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.85f, 0.9f, 1.0f, 1.0f), "P4 Deferred / Host-Dependent");
                ImGui.Checkbox("VHS hotkey bridge (deferred)##p4v", ref _settings.EnableVhsHotkeyBridge);
                ImGui.Checkbox("Decal HUD compat layer (deferred)##p4d", ref _settings.EnableDecalHudCompat);
                ImGui.Checkbox("Legacy .AutoPack import (deferred)##p4a", ref _settings.EnableLegacyAutoPackImport);

                ImGui.Spacing();
                ImGui.TextDisabled("P2 counters: /ub p2 status");

                if (ImGui.Button("Save settings##ubry"))
                {
                    UbRythaiSettingsStore.Save(_characterName, _settings);
                    Host.WriteToChat("[ub-Rythai] Settings saved.", 1);
                }

                ImGui.SameLine();
                if (ImGui.Button("Hide##ubry"))
                    _windowVisible = false;
            }

            ImGui.End();
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    private static void CopySettings(UbRythaiSettings src, UbRythaiSettings dst)
    {
        dst.LoginCommandsEnabled = src.LoginCommandsEnabled;
        dst.LoginCommandsInitialDelayMs = src.LoginCommandsInitialDelayMs;
        dst.LoginCommandsGapMs = src.LoginCommandsGapMs;
        dst.LoginCommandsText = src.LoginCommandsText ?? "";
        dst.PeriodicCommandsEnabled = src.PeriodicCommandsEnabled;
        dst.PeriodicIntervalSeconds = src.PeriodicIntervalSeconds;
        dst.PeriodicCommandsGapMs = src.PeriodicCommandsGapMs;
        dst.PeriodicCommandsText = src.PeriodicCommandsText ?? "";
        dst.OpenMainPackOnLogin = src.OpenMainPackOnLogin;
        dst.RunChatResizeOnLogin = src.RunChatResizeOnLogin;
        dst.ChatResizeCommand = src.ChatResizeCommand ?? "/ub chat max";
        dst.LogOutOnDeathEnabled = src.LogOutOnDeathEnabled;
        dst.LogOutOnDeathCooldownSeconds = src.LogOutOnDeathCooldownSeconds;
        dst.AutoTradeAcceptEnabled = src.AutoTradeAcceptEnabled;
        dst.AutoTradeAcceptWhitelist = src.AutoTradeAcceptWhitelist ?? "";
        dst.AutoTradeAcceptCommand = string.IsNullOrWhiteSpace(src.AutoTradeAcceptCommand) ? "/trade accept" : src.AutoTradeAcceptCommand;
        dst.AutoTradeAcceptDebounceSeconds = src.AutoTradeAcceptDebounceSeconds;
        dst.AutoPercentConfirmationEnabled = src.AutoPercentConfirmationEnabled;
        dst.AutoPercentConfirmationThreshold = src.AutoPercentConfirmationThreshold;
        dst.AutoPercentConfirmationCommand = string.IsNullOrWhiteSpace(src.AutoPercentConfirmationCommand) ? "/yes" : src.AutoPercentConfirmationCommand;
        dst.AutoPercentConfirmationDebounceSeconds = src.AutoPercentConfirmationDebounceSeconds;
        dst.BusyGuardrailsEnabled = src.BusyGuardrailsEnabled;
        dst.BusyGuardrailsMinCommandGapMs = src.BusyGuardrailsMinCommandGapMs;
        dst.BusyGuardrailsBusyStateThreshold = src.BusyGuardrailsBusyStateThreshold;
        dst.ChatFilterEnabled = src.ChatFilterEnabled;
        dst.FilterBusyLines = src.FilterBusyLines;
        dst.FilterSpellFizzleLines = src.FilterSpellFizzleLines;
        dst.FilterVendorSpamLines = src.FilterVendorSpamLines;
        dst.FilterMonsterDeathLines = src.FilterMonsterDeathLines;
        dst.FilterAttackEvadesLines = src.FilterAttackEvadesLines;
        dst.FilterDefenseEvadesLines = src.FilterDefenseEvadesLines;
        dst.FilterAttackResistsLines = src.FilterAttackResistsLines;
        dst.FilterDefenseResistsLines = src.FilterDefenseResistsLines;
        dst.FilterSpellCastMineLines = src.FilterSpellCastMineLines;
        dst.FilterSpellCastOthersLines = src.FilterSpellCastOthersLines;
        dst.FilterSpellExpiresLines = src.FilterSpellExpiresLines;
        dst.FilterPeriodicHealingLines = src.FilterPeriodicHealingLines;
        dst.FilterFailedAssessLines = src.FilterFailedAssessLines;
        dst.FilterKillTaskCompleteLines = src.FilterKillTaskCompleteLines;
        dst.ClientFpsPolicyEnabled = src.ClientFpsPolicyEnabled;
        dst.TargetFpsFocused = src.TargetFpsFocused;
        dst.TargetFpsBackground = src.TargetFpsBackground;
        dst.WindowMoverEnabled = src.WindowMoverEnabled;
        dst.WindowMoverAutoRestore = src.WindowMoverAutoRestore;
        dst.EnableSpellcraftQueue = src.EnableSpellcraftQueue;
        dst.EnableTempleGuardianTools = src.EnableTempleGuardianTools;
        dst.EnableFellowshipGames = src.EnableFellowshipGames;
        dst.EnableQuestEconomyTools = src.EnableQuestEconomyTools;
        dst.LeaftideFeaturesMode = NormalizeLeaftideMode(src.LeaftideFeaturesMode);
        dst.LeaftideAutoWorldNames = src.LeaftideAutoWorldNames ?? "InfiniteLeaftide,Conquest";
        dst.LeaftideManualAllowBanking = src.LeaftideManualAllowBanking;
        dst.XpMeterOverlayEnabled = src.XpMeterOverlayEnabled;
        dst.XpMeterShowTotals = src.XpMeterShowTotals;
        dst.XpMeterShowTime = src.XpMeterShowTime;
        dst.MiniRemotePortalGemsEnabled = src.MiniRemotePortalGemsEnabled;
        dst.TempleGuardianAutoChat = src.TempleGuardianAutoChat;
        dst.TempleGuardianAutoHandIn = src.TempleGuardianAutoHandIn;
        dst.BankToolsEnabled = src.BankToolsEnabled;
        dst.BankAutoRefreshOnLogin = src.BankAutoRefreshOnLogin;
        dst.BankAutoTransferEnabled = src.BankAutoTransferEnabled;
        dst.BankAutoTransferTarget = src.BankAutoTransferTarget ?? "";
        dst.BankAutoTransferPercent = src.BankAutoTransferPercent;
        dst.BankAutoTransferIntervalMinutes = src.BankAutoTransferIntervalMinutes;
        dst.BankAutoTransferPyreals = src.BankAutoTransferPyreals;
        dst.BankAutoTransferLuminance = src.BankAutoTransferLuminance;
        dst.BankAutoTransferLegendaryKeys = src.BankAutoTransferLegendaryKeys;
        dst.BankAutoTransferMythicalKeys = src.BankAutoTransferMythicalKeys;
        dst.BankAutoTransferEnlightenedCoins = src.BankAutoTransferEnlightenedCoins;
        dst.BankAutoTransferWeaklyEnlightenedCoins = src.BankAutoTransferWeaklyEnlightenedCoins;
        dst.EnableVhsHotkeyBridge = src.EnableVhsHotkeyBridge;
        dst.EnableDecalHudCompat = src.EnableDecalHudCompat;
        dst.EnableLegacyAutoPackImport = src.EnableLegacyAutoPackImport;
    }

    /// <summary>
    /// P1 bridge: run optional login commands that map UB-ILT utility macros onto
    /// RynthCore chat dispatch without requiring Decal hooks.
    /// </summary>
    private void RunLoginMacroCommands()
    {
        if (!Host.HasInvokeChatParser)
            return;

        if (_settings.OpenMainPackOnLogin)
        {
            // Uses legacy style command surface expected by players; harmless if unsupported on a shard.
            if (TryDispatchChatCommand("/open mainpack", "login macro open-main-pack"))
                LogAt(UbRythaiLogLevel.Debug, "sent login command: /open mainpack");
        }

        if (_settings.RunChatResizeOnLogin && !string.IsNullOrWhiteSpace(_settings.ChatResizeCommand))
        {
            string chatResize = _settings.ChatResizeCommand.Trim();
            if (TryDispatchChatCommand(chatResize, "login macro chat-resize"))
                LogAt(UbRythaiLogLevel.Debug, $"sent login command: {chatResize}");
        }
    }

    private void ApplyClientFpsPolicy()
    {
        if (!_settings.ClientFpsPolicyEnabled || !Host.HasSetFpsLimit)
            return;

        int focused = Math.Clamp(_settings.TargetFpsFocused, 10, 240);
        int background = Math.Clamp(_settings.TargetFpsBackground, 5, 120);
        Host.SetFpsLimit(true, focused, background);
    }

    private void CheckLogOutOnDeath()
    {
        if (!_settings.LogOutOnDeathEnabled || !Host.HasGetPlayerVitals || !Host.HasInvokeChatParser)
            return;

        if (!Host.TryGetPlayerVitals(out uint hp, out _, out _, out _, out _, out _))
            return;

        bool alive = hp > 0;
        if (_playerWasAlive && !alive)
        {
            int cooldown = Math.Clamp(_settings.LogOutOnDeathCooldownSeconds, 5, 300);
            if ((DateTime.UtcNow - _lastLogOutOnDeath).TotalSeconds >= cooldown)
            {
                Host.WriteToChat("[ub-Rythai] Death detected, running /logoff (P1 parity).", 1);
                if (TryDispatchChatCommand("/logoff", "death logout"))
                {
                    _lastLogOutOnDeath = DateTime.UtcNow;
                    LogAt(UbRythaiLogLevel.Warn, "death detected; invoked /logoff.");
                }
            }
        }

        _playerWasAlive = alive;
    }

    private void HandleAutoTradeAcceptCommand(string[] parts, string rawText)
    {
        if (parts.Length == 2 || parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            string state = _settings.AutoTradeAcceptEnabled ? "on" : "off";
            Host.WriteToChat($"[ub-Rythai] autotradeaccept={state}, debounce={Math.Clamp(_settings.AutoTradeAcceptDebounceSeconds, 1, 15)}s, cmd='{_settings.AutoTradeAcceptCommand}', whitelist='{_settings.AutoTradeAcceptWhitelist}'", 1);
            return;
        }

        if (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            _settings.AutoTradeAcceptEnabled = true;
            Host.WriteToChat("[ub-Rythai] AutoTradeAccept enabled.", 1);
            return;
        }

        if (parts[2].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _settings.AutoTradeAcceptEnabled = false;
            Host.WriteToChat("[ub-Rythai] AutoTradeAccept disabled.", 1);
            return;
        }

        if (parts[2].Equals("debounce", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4 && int.TryParse(parts[3], out int debounceSec))
        {
            _settings.AutoTradeAcceptDebounceSeconds = Math.Clamp(debounceSec, 1, 15);
            Host.WriteToChat($"[ub-Rythai] AutoTradeAccept debounce set to {_settings.AutoTradeAcceptDebounceSeconds}s.", 1);
            return;
        }

        if (parts[2].Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            int cmdStart = rawText.IndexOf("cmd", StringComparison.OrdinalIgnoreCase);
            string maybeCommand = cmdStart >= 0 ? rawText[(cmdStart + 3)..].Trim() : "";
            if (string.IsNullOrWhiteSpace(maybeCommand))
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub autotradeaccept cmd <chat command>", 1);
                return;
            }

            _settings.AutoTradeAcceptCommand = maybeCommand;
            Host.WriteToChat($"[ub-Rythai] AutoTradeAccept command set to '{_settings.AutoTradeAcceptCommand}'.", 1);
            return;
        }

        if (parts[2].Equals("whitelist", StringComparison.OrdinalIgnoreCase))
        {
            int wlStart = rawText.IndexOf("whitelist", StringComparison.OrdinalIgnoreCase);
            string whitelist = wlStart >= 0 ? rawText[(wlStart + "whitelist".Length)..].Trim() : "";
            _settings.AutoTradeAcceptWhitelist = whitelist;
            Host.WriteToChat("[ub-Rythai] AutoTradeAccept whitelist updated.", 1);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub autotradeaccept status|on|off|debounce <sec>|cmd <command>|whitelist <regex list>", 1);
    }

    private void HandleAutoPercentConfirmationCommand(string[] parts, string rawText)
    {
        if (parts.Length == 2 || parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            string state = _settings.AutoPercentConfirmationEnabled ? "on" : "off";
            int threshold = Math.Clamp(_settings.AutoPercentConfirmationThreshold, 1, 100);
            int debounce = Math.Clamp(_settings.AutoPercentConfirmationDebounceSeconds, 1, 15);
            Host.WriteToChat($"[ub-Rythai] autopercent={state}, threshold={threshold}, debounce={debounce}s, cmd='{_settings.AutoPercentConfirmationCommand}'", 1);
            return;
        }

        if (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            _settings.AutoPercentConfirmationEnabled = true;
            Host.WriteToChat("[ub-Rythai] AutoPercentConfirmation enabled.", 1);
            return;
        }

        if (parts[2].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _settings.AutoPercentConfirmationEnabled = false;
            Host.WriteToChat("[ub-Rythai] AutoPercentConfirmation disabled.", 1);
            return;
        }

        if (parts[2].Equals("threshold", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4 && int.TryParse(parts[3], out int thresholdValue))
        {
            _settings.AutoPercentConfirmationThreshold = Math.Clamp(thresholdValue, 1, 100);
            Host.WriteToChat($"[ub-Rythai] AutoPercentConfirmation threshold set to {_settings.AutoPercentConfirmationThreshold}%.", 1);
            return;
        }

        if (parts[2].Equals("debounce", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4 && int.TryParse(parts[3], out int debounceSec))
        {
            _settings.AutoPercentConfirmationDebounceSeconds = Math.Clamp(debounceSec, 1, 15);
            Host.WriteToChat($"[ub-Rythai] AutoPercentConfirmation debounce set to {_settings.AutoPercentConfirmationDebounceSeconds}s.", 1);
            return;
        }

        if (parts[2].Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            int cmdStart = rawText.IndexOf("cmd", StringComparison.OrdinalIgnoreCase);
            string maybeCommand = cmdStart >= 0 ? rawText[(cmdStart + 3)..].Trim() : "";
            if (string.IsNullOrWhiteSpace(maybeCommand))
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub autopercent cmd <chat command>", 1);
                return;
            }

            _settings.AutoPercentConfirmationCommand = maybeCommand;
            Host.WriteToChat($"[ub-Rythai] AutoPercentConfirmation command set to '{_settings.AutoPercentConfirmationCommand}'.", 1);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub autopercent status|on|off|threshold <1-100>|debounce <sec>|cmd <command>", 1);
    }

    private void TryAutoTradeAcceptFromChatLine(string line)
    {
        if (!_settings.AutoTradeAcceptEnabled || !Host.HasInvokeChatParser)
            return;

        if (DateTime.UtcNow - _lastAutoTradeAcceptUtc < TimeSpan.FromSeconds(Math.Clamp(_settings.AutoTradeAcceptDebounceSeconds, 1, 15)))
            return;

        string? partnerName = ExtractTradePartnerName(line);
        if (string.IsNullOrWhiteSpace(partnerName))
            return;

        if (!IsTradePartnerWhitelisted(partnerName))
            return;

        string cmd = string.IsNullOrWhiteSpace(_settings.AutoTradeAcceptCommand) ? "/trade accept" : _settings.AutoTradeAcceptCommand.Trim();
        if (!TryDispatchChatCommand(cmd, "autotradeaccept"))
            return;

        _lastAutoTradeAcceptUtc = DateTime.UtcNow;
        LogAt(UbRythaiLogLevel.Info, $"auto-accepted trade from '{partnerName}' via command: {cmd}");
    }

    private bool IsTradePartnerWhitelisted(string partnerName)
    {
        string whitelist = _settings.AutoTradeAcceptWhitelist ?? "";
        if (string.IsNullOrWhiteSpace(whitelist))
            return true;

        string[] tokens = whitelist.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string token in tokens)
        {
            try
            {
                if (Regex.IsMatch(partnerName, token, RegexOptions.IgnoreCase))
                    return true;
            }
            catch
            {
                // Ignore malformed regex tokens and continue evaluating remaining whitelist entries.
            }
        }

        return false;
    }

    private void TryAutoPercentConfirmationFromChatLine(string line)
    {
        if (!_settings.AutoPercentConfirmationEnabled || !Host.HasInvokeChatParser)
            return;

        if (DateTime.UtcNow - _lastAutoPercentConfirmUtc < TimeSpan.FromSeconds(Math.Clamp(_settings.AutoPercentConfirmationDebounceSeconds, 1, 15)))
            return;

        int? successPct = ExtractSuccessPercent(line);
        if (!successPct.HasValue)
            return;

        int threshold = Math.Clamp(_settings.AutoPercentConfirmationThreshold, 1, 100);
        if (successPct.Value < threshold)
            return;

        string cmd = string.IsNullOrWhiteSpace(_settings.AutoPercentConfirmationCommand) ? "/yes" : _settings.AutoPercentConfirmationCommand.Trim();
        if (!TryDispatchChatCommand(cmd, "autopercent"))
            return;

        _lastAutoPercentConfirmUtc = DateTime.UtcNow;
        LogAt(UbRythaiLogLevel.Info, $"auto-percent-confirm fired at {successPct.Value}% using command: {cmd}");
    }

    private static int? ExtractSuccessPercent(string line)
    {
        Match match = Regex.Match(
            line,
            @"^You determine that you have a (?<percent>\d+) percent chance to succeed\.$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["percent"].Value, out int pct))
            return null;

        return Math.Clamp(pct, 0, 100);
    }

    private void ResetP2Counters()
    {
        _busyFilteredCount = 0;
        _fizzleFilteredCount = 0;
        _vendorSpamFilteredCount = 0;
        _deathFilteredCount = 0;
        _attackEvadeFilteredCount = 0;
        _defenseEvadeFilteredCount = 0;
        _attackResistFilteredCount = 0;
        _defenseResistFilteredCount = 0;
        _spellCastMineFilteredCount = 0;
        _spellCastOthersFilteredCount = 0;
        _spellExpiresFilteredCount = 0;
        _periodicHealingFilteredCount = 0;
        _failedAssessFilteredCount = 0;
        _killTaskCompleteFilteredCount = 0;
    }

    private void HandleBusyGuardCommand(string[] parts)
    {
        if (parts.Length == 2 || parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            string state = _settings.BusyGuardrailsEnabled ? "on" : "off";
            int statusGapMs = Math.Clamp(_settings.BusyGuardrailsMinCommandGapMs, 0, 5000);
            int statusThreshold = Math.Clamp(_settings.BusyGuardrailsBusyStateThreshold, 0, 16);
            Host.WriteToChat($"[ub-Rythai] guard={state}, minGapMs={statusGapMs}, busyThreshold={statusThreshold}, blocked(busy={_busyGuardBlockedByBusyCount}, gap={_busyGuardBlockedByGapCount})", 1);
            return;
        }

        if (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            _settings.BusyGuardrailsEnabled = true;
            Host.WriteToChat("[ub-Rythai] Busy-state guardrails enabled.", 1);
            return;
        }

        if (parts[2].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _settings.BusyGuardrailsEnabled = false;
            Host.WriteToChat("[ub-Rythai] Busy-state guardrails disabled.", 1);
            return;
        }

        if (parts[2].Equals("gap", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4 && int.TryParse(parts[3], out int gapMs))
        {
            _settings.BusyGuardrailsMinCommandGapMs = Math.Clamp(gapMs, 0, 5000);
            Host.WriteToChat($"[ub-Rythai] Busy-state guard min gap set to {_settings.BusyGuardrailsMinCommandGapMs} ms.", 1);
            return;
        }

        if (parts[2].Equals("threshold", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4 && int.TryParse(parts[3], out int threshold))
        {
            _settings.BusyGuardrailsBusyStateThreshold = Math.Clamp(threshold, 0, 16);
            Host.WriteToChat($"[ub-Rythai] Busy-state threshold set to {_settings.BusyGuardrailsBusyStateThreshold}.", 1);
            return;
        }

        if (parts[2].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _busyGuardBlockedByBusyCount = 0;
            _busyGuardBlockedByGapCount = 0;
            Host.WriteToChat("[ub-Rythai] Busy guard block counters reset.", 1);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub guard status|on|off|gap <ms>|threshold <n>|reset", 1);
    }

    private bool TryDispatchChatCommand(string command, string source)
    {
        if (!Host.HasInvokeChatParser || string.IsNullOrWhiteSpace(command))
            return false;

        string trimmed = command.Trim();
        DateTime now = DateTime.UtcNow;

        if (_settings.BusyGuardrailsEnabled)
        {
            int minGapMs = Math.Clamp(_settings.BusyGuardrailsMinCommandGapMs, 0, 5000);
            if (minGapMs > 0 && _lastChatDispatchUtc != DateTime.MinValue &&
                (now - _lastChatDispatchUtc).TotalMilliseconds < minGapMs)
            {
                _busyGuardBlockedByGapCount++;
                return false;
            }

            if (Host.HasGetBusyState)
            {
                int busyState = Host.GetBusyState();
                int threshold = Math.Clamp(_settings.BusyGuardrailsBusyStateThreshold, 0, 16);
                if (busyState > threshold)
                {
                    _busyGuardBlockedByBusyCount++;
                    return false;
                }
            }
        }

        bool ok = Host.InvokeChatParser(trimmed);
        if (ok)
            _lastChatDispatchUtc = now;
        else
            LogAt(UbRythaiLogLevel.Warn, $"chat dispatch failed ({source}): {trimmed}");
        return ok;
    }

    private static string? ExtractTradePartnerName(string line)
    {
        // Mirrors legacy Mag parity trigger text patterns with tolerant matching for shard wording variance.
        Match m1 = Regex.Match(line, @"^(?<name>.+?)\s+has accepted the trade\.?$", RegexOptions.IgnoreCase);
        if (m1.Success)
            return m1.Groups["name"].Value.Trim();

        Match m2 = Regex.Match(line, @"^(?<name>.+?)\s+accepts the trade\.?$", RegexOptions.IgnoreCase);
        if (m2.Success)
            return m2.Groups["name"].Value.Trim();

        Match m3 = Regex.Match(line, @"^You have been offered a trade by\s+(?<name>.+?)\.?$", RegexOptions.IgnoreCase);
        if (m3.Success)
            return m3.Groups["name"].Value.Trim();

        return null;
    }

    private void OpenLogsFolder()
    {
        string logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{logsDir}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
            LogAt(UbRythaiLogLevel.Info, $"opened logs folder: {logsDir}");
        }
        catch (Exception ex)
        {
            LogAt(UbRythaiLogLevel.Error, $"failed to open logs folder '{logsDir}': {ex.Message}");
            Host.WriteToChat($"[ub-Rythai] Failed to open logs folder: {ex.Message}", 1);
        }
    }

    /// <summary>
    /// Centralized plugin logging:
    /// 1) sends to host log for immediate diagnostics
    /// 2) writes to rolling local file under install/Logs with configured minimum level
    /// </summary>
    private void LogAt(UbRythaiLogLevel level, string message)
    {
        string line = $"[{level}] {message}";
        Log(line);

        UbRythaiLogLevel minLevel = (UbRythaiLogLevel)Math.Clamp(_settings.LogLevel, 0, 4);
        _fileLogger?.Write(level, minLevel, message);
    }

    private void RenderXpMeterOverlay()
    {
        ImGui.SetNextWindowSize(new Vector2(360, 110), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("ub-Rythai XP Meter###UbXpMeter", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        double sec = _xpMeter.RunTimeSeconds();
        double xpHr = _xpMeter.XpPerHour();
        double lumHr = _xpMeter.LumPerHour();

        if (_settings.XpMeterShowTotals)
        {
            if (_xpMeter.HasLuminance)
                ImGui.TextUnformatted($"XP: {_xpMeter.AccumulatedXp:N0} | LUM: {_xpMeter.AccumulatedLum:N0}");
            else
                ImGui.TextUnformatted($"XP: {_xpMeter.AccumulatedXp:N0}");
        }

        if (_settings.XpMeterShowTime)
            ImGui.TextUnformatted($"Time: {TimeSpan.FromSeconds(sec):hh\\:mm\\:ss}");

        if (_xpMeter.HasLuminance)
            ImGui.TextUnformatted($"Rates: {xpHr:N0} XP/hr | {lumHr:N0} LUM/hr");
        else
            ImGui.TextUnformatted($"Rate: {xpHr:N0} XP/hr");

        ImGui.End();
    }

    private void RenderMiniRemotePortalGemOverlay()
    {
        ImGui.SetNextWindowSize(new Vector2(260, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("ub-Rythai Portal Gems###UbPortalGems", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        uint pid = Host.GetPlayerId();
        ImGui.TextDisabled("Click uses inventory scan + UseObject (UB MiniRemote parity).");
        foreach (string gem in UbRythaiPortalGems.CanonicalGemNames)
        {
            if (ImGui.Button($"{gem}##gem{gem.GetHashCode()}"))
                TryUsePortalGem(gem);
        }

        ImGui.Separator();
        ImGui.TextDisabled($"PlayerId: {pid}");
        ImGui.End();
    }

    private void TryUsePortalGem(string gemName)
    {
        uint pid = Host.GetPlayerId();
        if (pid == 0 || !Host.HasUseObject)
        {
            Host.WriteToChat("[ub-Rythai] Portal gem: host not ready (player id / UseObject).", 1);
            return;
        }

        if (!UbRythaiPortalGems.TryFindInventoryObjectIdByName(Host, pid, gemName, out uint gemId))
        {
            Host.WriteToChat($"[ub-Rythai] Portal gem not found: {gemName}", 1);
            return;
        }

        if (!Host.UseObject(gemId))
        {
            Host.WriteToChat($"[ub-Rythai] Portal gem use failed: {gemName} (id={gemId})", 1);
            return;
        }

        Host.WriteToChat($"[ub-Rythai] Using portal gem: {gemName}", 1);
        LogAt(UbRythaiLogLevel.Info, $"portal gem used: {gemName} (id={gemId})");
    }

    private void HandleXpMeterCommand(string[] parts)
    {
        if (parts.Length < 3)
        {
            Host.WriteToChat("[ub-Rythai] Usage: /ub xpmeter on|off|reset|status", 1);
            return;
        }

        string cmd = parts[2].ToLowerInvariant();
        if (cmd is "on" or "true")
        {
            _settings.XpMeterOverlayEnabled = true;
            Host.WriteToChat("[ub-Rythai] XP meter overlay enabled.", 1);
            return;
        }

        if (cmd is "off" or "false")
        {
            _settings.XpMeterOverlayEnabled = false;
            Host.WriteToChat("[ub-Rythai] XP meter overlay disabled.", 1);
            return;
        }

        if (cmd == "reset")
        {
            _xpMeter.Reset();
            uint pid = Host.GetPlayerId();
            if (pid != 0)
                _xpMeter.SeedFromPlayer(Host, pid);
            Host.WriteToChat("[ub-Rythai] XP meter reset.", 1);
            return;
        }

        if (cmd == "status")
        {
            double sec = _xpMeter.RunTimeSeconds();
            Host.WriteToChat(
                $"[ub-Rythai] xpmeter overlay={(_settings.XpMeterOverlayEnabled ? "on" : "off")}, xp={_xpMeter.AccumulatedXp:N0}, lum={_xpMeter.AccumulatedLum:N0}, sec={sec:0}, xp/hr={_xpMeter.XpPerHour():N0}, lum/hr={_xpMeter.LumPerHour():N0}",
                1);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub xpmeter on|off|reset|status", 1);
    }

    private void HandlePortalGemCommand(string[] parts, string rawText)
    {
        if (parts.Length < 3)
        {
            Host.WriteToChat("[ub-Rythai] Usage: /ub portal use <gem name> | /ub portal list", 1);
            return;
        }

        if (parts[2].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            Host.WriteToChat("[ub-Rythai] Portal gems: " + string.Join(" | ", UbRythaiPortalGems.CanonicalGemNames), 1);
            return;
        }

        if (parts[2].Equals("use", StringComparison.OrdinalIgnoreCase))
        {
            int idx = rawText.IndexOf("use", StringComparison.OrdinalIgnoreCase);
            string tail = idx >= 0 ? rawText[(idx + 3)..].Trim() : "";
            if (string.IsNullOrWhiteSpace(tail))
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub portal use <gem name>", 1);
                return;
            }

            TryUsePortalGem(tail);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub portal use <gem name> | /ub portal list", 1);
    }

    private void HandleGuardianCommand(string[] parts, string rawText)
    {
        if (parts.Length < 3)
        {
            Host.WriteToChat("[ub-Rythai] Usage: /ub guardian chat on|off | handin on|off | translate <text>", 1);
            return;
        }

        if (parts[2].Equals("chat", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
        {
            bool on = parts[3].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[3].Equals("true", StringComparison.OrdinalIgnoreCase);
            _settings.TempleGuardianAutoChat = on;
            Host.WriteToChat($"[ub-Rythai] Guardian auto-chat: {(on ? "ON" : "OFF")}", 1);
            return;
        }

        if (parts[2].Equals("handin", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
        {
            bool on = parts[3].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[3].Equals("true", StringComparison.OrdinalIgnoreCase);
            _settings.TempleGuardianAutoHandIn = on;
            Host.WriteToChat($"[ub-Rythai] Guardian auto hand-in: {(on ? "ON" : "OFF")} (best-effort)", 1);
            return;
        }

        if (parts[2].Equals("translate", StringComparison.OrdinalIgnoreCase))
        {
            int t = rawText.IndexOf("translate", StringComparison.OrdinalIgnoreCase);
            string blob = t >= 0 ? rawText[(t + "translate".Length)..].Trim() : "";
            if (string.IsNullOrWhiteSpace(blob))
            {
                Host.WriteToChat("[ub-Rythai] Usage: /ub guardian translate <pasted guardian text>", 1);
                return;
            }

            string ans = UbRythaiTempleGuardianPhrasebook.Translate(blob);
            string npc = UbRythaiTempleGuardianPhrasebook.HandInNpcNameFromInput(blob);
            _lastGuardianPrompt = blob;
            _lastGuardianAnswer = ans;
            _lastGuardianNpc = npc;
            Host.WriteToChat($"[Guardian] {ans}", 1);
            return;
        }

        Host.WriteToChat("[ub-Rythai] Usage: /ub guardian chat on|off | handin on|off | translate <text>", 1);
    }

    private void TryTempleGuardianFromChatLine(string line, int chatType)
    {
        if (!IsLeaftideFeaturesEnabled() || !_settings.EnableTempleGuardianTools)
            return;
        if (!_settings.TempleGuardianAutoChat)
            return;

        // UB-ILT filters to incoming tells (color 0x03). RynthCore passes the chat channel id through <paramref name="chatType"/>.
        const int incomingTell = 0x03;
        if (chatType != incomingTell && !UbRythaiTempleGuardianPhrasebook.LooksLikeIncomingGuardianTellLine(line))
            return;

        if (!UbRythaiTempleGuardianPhrasebook.LooksLikeGuardianChatLine(line))
            return;

        string ans = UbRythaiTempleGuardianPhrasebook.Translate(line);
        if (!IsActionableGuardianAnswer(ans))
            return;

        string fp = $"{line.Length}:{line.GetHashCode()}";
        if (fp == _lastGuardianFingerprint && (DateTime.UtcNow - _lastGuardianEmitUtc).TotalSeconds < 2.5)
            return;
        _lastGuardianFingerprint = fp;
        _lastGuardianEmitUtc = DateTime.UtcNow;

        _lastGuardianPrompt = line;
        _lastGuardianAnswer = ans;
        _lastGuardianNpc = UbRythaiTempleGuardianPhrasebook.TurnInNpcNameFromTell(line);

        Host.WriteToChat($"[Guardian] {ans}", 1);
        LogAt(UbRythaiLogLevel.Info, $"guardian translate: npc='{_lastGuardianNpc}', answer='{ans}'");

        if (_settings.TempleGuardianAutoHandIn)
            _templeGuardianAutomation.Start(ans, _lastGuardianNpc, cmd => TryDispatchChatCommand(cmd, "guardian automation"), msg => LogAt(UbRythaiLogLevel.Info, msg));
    }

    private static bool IsActionableGuardianAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;
        if (answer.StartsWith("No match", StringComparison.OrdinalIgnoreCase))
            return false;
        if (answer.StartsWith("Paste ", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void EnsureImGuiResolver()
    {
        if (_imguiResolverConfigured)
            return;

        NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, ResolveImGuiNative);
        _imguiResolverConfigured = true;
        Log("ub-Rythai: ImGui native resolver bound to engine cimgui.");
    }

    private static IntPtr ResolveImGuiNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, "cimgui.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        IntPtr module = GetModuleHandleA("RynthCore.cimgui.dll");
        if (module != IntPtr.Zero)
            return module;

        module = GetModuleHandleA("cimgui.dll");
        return module;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);
}
