using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using System.Numerics;

public class JackpotsBlock
{
    public int Index { get; set; }  // Position du bloc dans la chaîne
    public DateTime Timestamp { get; set; }
    public string PreviousHash { get; set; }
    public string Hash { get; set; }
    public Jackpot JackpotData { get; set; }  // Données de jackpot stockées dans le bloc

    public JackpotsBlock(int index, string previousHash, Jackpot jackpotData)
    {
        Index = index;
        Timestamp = DateTime.UtcNow;
        PreviousHash = previousHash;
        JackpotData = jackpotData;
        Hash = jackpotData.BlockHash; // Utilise directement le hash du jackpot pour le bloc
    }
}
