namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// Persisted settings for the ub-Rythai plugin only (not RynthAi, not Decal UB-ILT).
/// Parity targets: Utility Belt login chat queues and periodic command batches.
/// </summary>
public sealed class UbRythaiSettings
{
    /// <summary>
    /// Minimum log level written to the local file logger. 0=Trace, 1=Debug, 2=Info, 3=Warn, 4=Error.
    /// </summary>
    public int LogLevel = (int)UbRythaiLogLevel.Info;

    /// <summary>Login-time chat lines (UtilityBelt CharacterLoginCommands-style).</summary>
    public bool LoginCommandsEnabled;
    public int LoginCommandsInitialDelayMs = 2000;
    public int LoginCommandsGapMs = 400;
    public string LoginCommandsText = "";

    /// <summary>Repeating chat batches (PeriodicCommandsTool-style).</summary>
    public bool PeriodicCommandsEnabled;
    public int PeriodicIntervalSeconds = 300;
    public int PeriodicCommandsGapMs = 400;
    public string PeriodicCommandsText = "";

    // P1: macro parity items that map cleanly onto chat-command dispatch.
    public bool OpenMainPackOnLogin;
    public bool RunChatResizeOnLogin;
    public string ChatResizeCommand = "/ub chat max";
    public bool LogOutOnDeathEnabled;
    public int LogOutOnDeathCooldownSeconds = 30;
    public bool AutoTradeAcceptEnabled;
    public string AutoTradeAcceptWhitelist = "";
    public string AutoTradeAcceptCommand = "/trade accept";
    public int AutoTradeAcceptDebounceSeconds = 2;
    public bool AutoPercentConfirmationEnabled;
    public int AutoPercentConfirmationThreshold = 100;
    public string AutoPercentConfirmationCommand = "/yes";
    public int AutoPercentConfirmationDebounceSeconds = 2;
    public bool BusyGuardrailsEnabled = true;
    public int BusyGuardrailsMinCommandGapMs = 250;
    public int BusyGuardrailsBusyStateThreshold = 0;

    // P2: selective chat filtering and client FPS policy.
    public bool ChatFilterEnabled;
    public bool FilterBusyLines;
    public bool FilterSpellFizzleLines;
    public bool FilterVendorSpamLines;
    public bool FilterMonsterDeathLines;
    public bool FilterAttackEvadesLines;
    public bool FilterDefenseEvadesLines;
    public bool FilterAttackResistsLines;
    public bool FilterDefenseResistsLines;
    public bool FilterSpellCastMineLines;
    public bool FilterSpellCastOthersLines;
    public bool FilterSpellExpiresLines;
    public bool FilterPeriodicHealingLines;
    public bool FilterFailedAssessLines;
    public bool FilterKillTaskCompleteLines;
    public bool ClientFpsPolicyEnabled = true;
    public int TargetFpsFocused = 60;
    public int TargetFpsBackground = 30;
    public bool WindowMoverEnabled;
    public bool WindowMoverAutoRestore;

    // P3: ILT gameplay systems (feature gates for incremental implementation).
    public bool EnableSpellcraftQueue;
    public bool EnableTempleGuardianTools;
    public bool EnableFellowshipGames;
    public bool EnableQuestEconomyTools;

    /// <summary>
    /// ILT feature gate mode: Auto | On | Off.
    /// Auto enables ILT-only features when world name matches <see cref="LeaftideAutoWorldNames"/>.
    /// On forces ILT-only features on any shard; Off disables ILT-only features.
    /// </summary>
    public string LeaftideFeaturesMode = "Auto";

    /// <summary>
    /// Comma/semicolon/newline-separated world names used by Auto mode (case-insensitive exact match).
    /// </summary>
    public string LeaftideAutoWorldNames = "InfiniteLeaftide,Conquest";

    /// <summary>
    /// Extra safety gate for banking when mode is manually forced On. Auto mode allows banking automatically on matched worlds.
    /// </summary>
    public bool LeaftideManualAllowBanking;

    /// <summary>UB-ILT XPMeter parity: ImGui overlay tracking XP/LUM rates via player PropertyInt64 reads.</summary>
    public bool XpMeterOverlayEnabled;
    public bool XpMeterShowTotals = true;
    public bool XpMeterShowTime = true;

    /// <summary>UB-ILT MiniRemote parity: quick portal gem buttons (inventory scan + UseObject).</summary>
    public bool MiniRemotePortalGemsEnabled;

    /// <summary>UB-ILT TempleGuardianHelper parity: auto-translate incoming guardian tells using the phrasebook.</summary>
    public bool TempleGuardianAutoChat;
    /// <summary>UB-ILT TempleGuardianHelper parity: optional vendor/fillcomps/buyall pump (best-effort; requires UB-style chat commands).</summary>
    public bool TempleGuardianAutoHandIn;

    /// <summary>Enable /bank refresh + transfer helpers in ub-Rythai when ILT banking gate allows it.</summary>
    public bool BankToolsEnabled = true;

    /// <summary>Issue /bank once shortly after login if banking tools are active.</summary>
    public bool BankAutoRefreshOnLogin = true;

    /// <summary>Enable periodic auto-transfer from parsed bank balances.</summary>
    public bool BankAutoTransferEnabled;
    public string BankAutoTransferTarget = "";
    public int BankAutoTransferPercent = 10;
    public int BankAutoTransferIntervalMinutes = 15;
    public bool BankAutoTransferPyreals;
    public bool BankAutoTransferLuminance;
    public bool BankAutoTransferLegendaryKeys;
    public bool BankAutoTransferMythicalKeys;
    public bool BankAutoTransferEnlightenedCoins;
    public bool BankAutoTransferWeaklyEnlightenedCoins;

    // P4: deferred or host-dependent surfaces.
    public bool EnableVhsHotkeyBridge;
    public bool EnableDecalHudCompat;
    public bool EnableLegacyAutoPackImport;
}
