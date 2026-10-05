using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.MonsterEditor;

[JsonSerializable(typeof(List<MonsterRule>))]
[JsonSerializable(typeof(EditorSettings))]
// UB damage-insights seed written for the RynthAi plugin (see UbMobSeedImport).
[JsonSerializable(typeof(RynthCore.CreatureSeed.UbMobSeedFile))]
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
internal partial class EditorJsonContext : JsonSerializerContext { }
