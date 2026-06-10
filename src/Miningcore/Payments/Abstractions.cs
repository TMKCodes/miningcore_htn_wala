using System.Data;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence.Model;

namespace Miningcore.Payments;

public interface IPayoutHandler
{
    Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct);

    Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct);
    Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct);
    Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct);
    double AdjustShareDifficulty(double difficulty);
    double AdjustBlockEffort(double effort);

    string FormatAmount(decimal amount);
}

public interface IPayoutScheme
{
    Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler, Block block, Block lastConfirmedBlock, decimal blockReward, CancellationToken ct);
}

public readonly record struct ConfirmedBlockPayout(Block Block, decimal BlockReward);

public readonly record struct BatchPayoutResult(DateTime? ProcessedShareCutoff);

public interface IBatchPayoutScheme
{
    Task<BatchPayoutResult> UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, Block lastConfirmedBlock, IMiningPool pool, IPayoutHandler payoutHandler,
        IReadOnlyList<ConfirmedBlockPayout> payouts, CancellationToken ct);
}
