namespace Miningcore.Persistence.Model;

public class Block
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public ulong BlockHeight { get; set; }
    public double NetworkDifficulty { get; set; }
    public BlockStatus Status { get; set; }
    public string Type { get; set; }
    public double ConfirmationProgress { get; set; }
    public double? Effort { get; set; }
    public double? MinerEffort { get; set; }
    public string TransactionConfirmationData { get; set; }
    public string Miner { get; set; }
    public decimal Reward { get; set; }
    public string Source { get; set; }
    public string Hash { get; set; }
    public DateTime Created { get; set; }

    // Autres propriétés existantes
    // Nouvelle propriété pour indiquer si un bloc est éligible aux récompenses
    public bool RewardEligible { get; set; }

    // Ajoute un constructeur ou une initialisation par défaut si nécessaire
    public Block()
    {
        RewardEligible = false;  // Par défaut, un bloc n'est pas éligible tant qu'il n'est pas marqué comme tel
    }

    }
