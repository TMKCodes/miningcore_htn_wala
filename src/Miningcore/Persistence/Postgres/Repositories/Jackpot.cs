namespace Miningcore.Persistence.Model
{
public class Jackpot
{
    public string JackpotId { get; set; }
    public string PoolId { get; set; }
    public decimal Amount { get; set; }
    public DateTime RewardedAt { get; set; }
    public string BlockHash { get; set; }
    public string WalletAddress { get; set; }
    public string DrawNumber { get; set; }
}
}
