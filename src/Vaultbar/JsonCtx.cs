using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vaultbar;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(Dictionary<string, UsageEntry>))]
internal partial class JsonCtx : JsonSerializerContext;
