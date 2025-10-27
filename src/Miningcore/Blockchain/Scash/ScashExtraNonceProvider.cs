namespace Miningcore.Blockchain.Scash;

public class ScashExtraNonceProvider : ExtraNonceProviderBase
{
    public ScashExtraNonceProvider(string poolId, byte? clusterInstanceId) : base(poolId, 4, clusterInstanceId)
    {
    }
}
