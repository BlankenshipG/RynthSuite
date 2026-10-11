using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthILT.Bounty;

namespace RynthCore.Plugin.RynthILT;

// Source-generated System.Text.Json only: never reflection-serialize plugin types (the
// reflection cache keeps the old plugin generation alive across a hot reload).
[JsonSerializable(typeof(BountyState))]
[JsonSerializable(typeof(BountyBridgePayload))]
[JsonSourceGenerationOptions(
    IncludeFields = true,
    PropertyNameCaseInsensitive = true)]
internal partial class RynthILTJsonContext : JsonSerializerContext { }
