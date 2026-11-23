using System.Text.Json.Serialization;

namespace RynthCore.Plugin.UbRythai;

/// <summary>Source-generated JSON serializer context for NativeAOT-safe settings persistence.</summary>
[JsonSerializable(typeof(UbRythaiSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class UbRythaiJsonContext : JsonSerializerContext;
