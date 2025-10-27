using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Miningcore.Blockchain.Scash.DaemonResponses
{
    public class CoinbaseDevReward
    {
        public string ScriptPubkey { get; set; }
        public long Value { get; set; }
    }
    public class CoinbaseDevRewardTemplateExtra
    {
        [JsonProperty("coinbasedevreward")]
        public JToken CoinbaseDevReward { get; set; }
    }
}
