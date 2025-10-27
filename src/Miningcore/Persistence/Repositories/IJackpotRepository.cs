using System.Threading.Tasks;
using Miningcore.Persistence.Model;
using System.Numerics;

namespace Miningcore.Persistence.Repositories
{
    public interface IJackpotRepository
    {
        Task<int> GetOrCreateMinerIdAsync(string minerWallet);
        Task InsertJackpotAsync(Jackpot jackpot);
        Task InsertJackpotEntryAsync(JackpotEntry jackpotEntry);
        Task UpdateJackpotStringAsync(int minerId);
        Task<bool> CheckJackpotWinAsync(string blockHash, int minerId);
        Task RegisterJackpotWinAsync(string poolId, int minerId, string blockHash);
        Task SavePoolRewardAsync(string poolId, string jackpotId, int minerId);
        Task UpdateJackpotIfBlockExistsAsync(string blockHash, decimal blockReward, string poolId, ulong blockHeight);
        Task<List<Balance>> GetPendingPaymentsAsync();
        Task MarkPaymentsAsPaidAsync(List<Balance> balances);
        Task<IEnumerable<Jackpot>> GetJackpotsAsync();
    }
}
