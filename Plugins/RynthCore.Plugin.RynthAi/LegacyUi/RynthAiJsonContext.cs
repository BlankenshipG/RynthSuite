using System.Collections.Generic;
using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

[JsonSerializable(typeof(LegacyUiSettings))]
[JsonSerializable(typeof(AmmoRule))]
[JsonSerializable(typeof(List<MonsterRule>), TypeInfoPropertyName = "MonsterRuleList")]
[JsonSerializable(typeof(List<ConsumableRule>), TypeInfoPropertyName = "ConsumableRuleList")]
[JsonSerializable(typeof(Dictionary<string, CreatureProfile>), TypeInfoPropertyName = "CreatureProfileDict")]
// UB damage-insights seed (CreatureData/UbMobSeedStore.cs), written by the Monster Editor.
[JsonSerializable(typeof(RynthCore.CreatureSeed.UbMobSeedFile))]
[JsonSerializable(typeof(MonstersBridgePayload))]
[JsonSerializable(typeof(SettingsBridgePayload))]
[JsonSerializable(typeof(NavBridgePayload))]
[JsonSerializable(typeof(ItemsBridgePayload))]
[JsonSerializable(typeof(NavCommand))]
[JsonSerializable(typeof(MetaRuleDto))]
[JsonSerializable(typeof(MetaCommand))]
[JsonSerializable(typeof(List<AmmoRule>), TypeInfoPropertyName = "AmmoRuleList")]
// ILT Hub (IltHub/): per-character hub state and its bank transaction-log sidecar.
[JsonSerializable(typeof(IltHubState))]
[JsonSerializable(typeof(List<IltBankTransaction>), TypeInfoPropertyName = "IltBankTransactionList")]
// Floating HUD windows (Huds/): per-character item HUD / Mini Remote settings.
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.Huds.HudState))]
// Diagnostics (Diagnostics/RynthLog.cs): persisted debug/trace switches.
[JsonSerializable(typeof(RynthLogConfig))]
// Chat translator (Translate/TranslateSettings.cs): shared translate.json.
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.Translate.TranslateSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true,
    PropertyNameCaseInsensitive = true)]
internal partial class RynthAiJsonContext : JsonSerializerContext { }
