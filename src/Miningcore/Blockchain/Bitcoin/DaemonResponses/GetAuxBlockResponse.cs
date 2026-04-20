using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

public class GetAuxBlockResponse
{
    [JsonProperty("hash")]
    public string Hash { get; set; }

    [JsonProperty("chainid")]
    public uint? ChainId { get; set; }

    /// <summary>
    /// Target as 256-bit hex string (common in AuxPoW daemons).
    /// </summary>
    [JsonProperty("target")]
    public string Target { get; set; }

    /// <summary>
    /// Some daemons may expose nBits instead of a full target.
    /// </summary>
    [JsonProperty("bits")]
    public string Bits { get; set; }
}
