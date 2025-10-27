namespace Miningcore.Blockchain.Hoosat
{
    public class HoosatExtraNonceProvider : ExtraNonceProviderBase
    {
        /// <summary>
        /// Constructeur de HoosatExtraNonceProvider
        /// </summary>
        /// <param name="poolId">Identifiant unique du pool</param>
        /// <param name="size">Taille de l'extra nonce en octets</param>
        /// <param name="clusterInstanceId">Identifiant de l'instance du cluster (peut être null)</param>
        public HoosatExtraNonceProvider(string poolId, int size, byte? clusterInstanceId)
            : base(poolId, size, clusterInstanceId)
        {
        }
    }
}
