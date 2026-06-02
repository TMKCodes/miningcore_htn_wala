using System.Collections.Generic;
using Newtonsoft.Json;

namespace Miningcore.Stratum;

/// <summary>
/// Stratum V1 response envelope - tuned for BOSminer compatibility
/// BOSminer expects:
/// - No extra fields
/// - "result": value or false/null
/// - "error": null OR array [code, message, data]
/// - No "jsonrpc" field
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class StratumResponse
{
    [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Include)]
    public object Result { get; set; }

    [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Include)]
    public object Error { get; set; }

    [JsonProperty(PropertyName = "id", NullValueHandling = NullValueHandling.Include)]
    public object Id { get; set; }

    // Remove any extra data that could confuse the parser
    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; } = null;
}