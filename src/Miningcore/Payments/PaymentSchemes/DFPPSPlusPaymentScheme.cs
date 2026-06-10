using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Util;
using NLog;
using Polly;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments.PaymentSchemes;

/// <summary>
/// Delayed Full Pay-Per-Share Plus (DFPPS+)
///
/// - Delayed: Balances are credited only when a block is found and confirmed.
/// - Full: Pays the full net block reward (after pool fees).
/// - Pay-Per-Share: Each share receives a fixed expected value based on NetworkDifficulty.
/// - Plus: The pool absorbs variance (overpays when unlucky, underpays when lucky).
///
/// This is the classic PPS model with delayed crediting for safety.
/// </summary>
public class DFPPSPlusPaymentScheme : IPayoutScheme, IBatchPayoutScheme
{

    public DFPPSPlusPaymentScheme(
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);

        this.cf = cf;
        this.blockRepo = blockRepo;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;

        BuildFaultHandlingPolicy();
    }

    private readonly IConnectionFactory cf;
    private readonly IBlockRepository blockRepo;
    private readonly IShareRepository shareRepo;
    private readonly IBalanceRepository balanceRepo;

    private static readonly ILogger logger = LogManager.GetLogger("DFPPS+ Payment");
    private const int RetryCount = 4;
    private IAsyncPolicy shareReadFaultPolicy;

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool,
        IPayoutHandler payoutHandler, Block block, Block lastConfirmedBlock, decimal blockReward, CancellationToken ct)
    {
        await UpdateBalancesAsync(con, tx, lastConfirmedBlock, pool, payoutHandler,
            new[] { new ConfirmedBlockPayout(block, blockReward) }, ct);
    }

    public async Task<BatchPayoutResult> UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, Block lastConfirmedBlock, IMiningPool pool,
    IPayoutHandler payoutHandler, IReadOnlyList<ConfirmedBlockPayout> payouts, CancellationToken ct)
    {
        if (payouts == null || payouts.Count == 0)
            return default;

        var poolConfig = pool.Config;
        var payableBlocks = payouts
            .OrderBy(x => x.Block.Created)
            .Select(x => new PendingBlockPayout(x.Block, CalculateNetReward(x.BlockReward, poolConfig)))
            .ToArray();

        foreach (var payout in payableBlocks.Where(x => x.NetReward <= 0))
        {
            logger.Info(() => $"Payout: Net reward for pool {poolConfig.Id}, block {payout.Block.BlockHeight} is {payout.NetReward}. Skipping.");
        }

        var activeBlocks = payableBlocks
            .Where(x => x.NetReward > 0)
            .ToArray();

        if (activeBlocks.Length == 0)
            return default;

        var previousConfirmedBlock = lastConfirmedBlock;
        previousConfirmedBlock ??= await blockRepo.GetBlockBeforeAsync(con, poolConfig.Id,
            new[] { BlockStatus.Confirmed }, activeBlocks[0].Block.Created);

        foreach (var payout in activeBlocks)
        {
            logger.Info(() => $"Payout: Block {payout.Block.BlockHeight} | Net Reward for DFPPS+: {payout.NetReward}");
        }

        var shareCutOffDate = await CalculatePPSRewardsAsync(con, pool, payoutHandler, activeBlocks,
            previousConfirmedBlock?.Created, ct);

        foreach (var payout in activeBlocks)
        {
            if (payout.Rewards.Count == 0)
                HandleNoSharesFound(poolConfig, payout.Block, payoutHandler, payout.Rewards, payout.NetReward);

            decimal totalCredited = 0m;

            foreach (var address in payout.Rewards.Keys.OrderByDescending(a => payout.Rewards[a]))
            {
                var amount = payout.Rewards[address];
                var shareDiff = payout.Shares.GetValueOrDefault(address);

                if (amount > 0)
                {
                    logger.Info(() => $"Payout: Crediting {address} with {amount} " +
                                    $"for {FormatUtil.FormatQuantity(shareDiff)} share difficulty (block {payout.Block.BlockHeight})");

                    await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount,
                        $"DFPPS+ reward for {FormatUtil.FormatQuantity(shareDiff)} share difficulty - block {payout.Block.BlockHeight}");

                    totalCredited += amount;
                }
            }

            // Safer logging - avoid FormatAmount entirely here
            logger.Info(() => $"Payout: Total credited to balances for block {payout.Block.BlockHeight}: {totalCredited} HTN");

            if (totalCredited > payout.NetReward * 1.02m)
            {
                var warningMessage = $"Payout: Total credited {totalCredited} exceeds net reward {payout.NetReward} for block {payout.Block.BlockHeight}";
                logger.Warn(() => warningMessage);
                Console.WriteLine($"[DFPPS+ Payment] {warningMessage}");
            }
        }

        if (shareCutOffDate.HasValue)
        {
            var cutOffCount = await shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, shareCutOffDate.Value, ct);
            if (cutOffCount > 0)
            {
                logger.Info(() => $"Payout: Deleting all processed shares (fast path) for pool {poolConfig.Id}");

                await shareRepo.DeleteSharesBeforeAsync(con, tx, poolConfig.Id, shareCutOffDate.Value, ct);

                logger.Info(() => $"Payout: Removed processed share(s) for pool {poolConfig.Id}");
            }
        }

        return default;
    }

    #endregion

    private decimal CalculateNetReward(decimal grossReward, PoolConfig poolConfig)
    {
        if (poolConfig.RewardRecipients == null || poolConfig.RewardRecipients.Length == 0)
            return grossReward;

        decimal feePercentage = poolConfig.RewardRecipients
            .Where(r => r.Type == "op" || r.Type == "fee")
            .Sum(r => r.Percentage);

        return grossReward * (1m - feePercentage / 100m);
    }

    private async Task<DateTime?> CalculatePPSRewardsAsync(
    IDbConnection con,
    IMiningPool pool,
    IPayoutHandler payoutHandler,
    IReadOnlyList<PendingBlockPayout> payouts,
    DateTime? lowerBoundExclusive,
    CancellationToken ct)
    {
        DateTime? shareCutOffDate = null;
        var payoutIndex = 0;

        await shareReadFaultPolicy.ExecuteAsync(() =>
        {
            var upperBound = payouts[^1].Block.Created;
            var shares = lowerBoundExclusive.HasValue
                ? shareRepo.StreamSharesBetween(con, pool.Config.Id, lowerBoundExclusive.Value, upperBound, true, ascending: true)
                : shareRepo.StreamSharesBefore(con, pool.Config.Id, upperBound, true, ascending: true);

            foreach (var share in shares)
            {
                while (payoutIndex < payouts.Count && share.Created > payouts[payoutIndex].Block.Created)
                    payoutIndex++;

                if (payoutIndex >= payouts.Count)
                    break;

                var payout = payouts[payoutIndex];
                payout.TotalSharesProcessed++;

                var address = share.Miner;
                var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);

                payout.Shares[address] = payout.Shares.GetValueOrDefault(address) + shareDiffAdjusted;
                payout.TotalShareDifficulty += shareDiffAdjusted;

                var rawReward = (decimal)shareDiffAdjusted * payout.PpsRate;

                if (rawReward > 0)
                    payout.Rewards[address] = payout.Rewards.GetValueOrDefault(address) + rawReward;

                payout.ShareCutOffDate = share.Created;
                shareCutOffDate = share.Created;
            }

            return Task.CompletedTask;
        });

        foreach (var payout in payouts)
        {
            decimal totalRawReward = payout.Rewards.Values.Sum();

            if (payout.TotalShareDifficulty > 0 && totalRawReward > payout.NetReward)
            {
                var normalizationFactor = payout.NetReward / totalRawReward;

                foreach (var address in payout.Rewards.Keys.ToList())
                    payout.Rewards[address] *= normalizationFactor;

                logger.Warn(() => $"DFPPS+ overpay protection triggered for block {payout.Block.BlockHeight} | " +
                                  $"Raw total: {payoutHandler.FormatAmount(totalRawReward)} -> " +
                                  $"Capped to: {payoutHandler.FormatAmount(payout.NetReward)} | " +
                                  $"Factor: {normalizationFactor.ToString("F6")} | " +
                                  $"Total share diff: {payout.TotalShareDifficulty:F2} vs NetDiff: {payout.NetworkDifficulty:F2}");
            }
            else if (totalRawReward > 0)
            {
                logger.Info(() => $"DFPPS+ payout within limit for block {payout.Block.BlockHeight} | " +
                                  $"Total share diff: {payout.TotalShareDifficulty:F2} | " +
                                  $"Final total: {payoutHandler.FormatAmount(totalRawReward)}");
            }

            // Fixed: Use .ToString("F8") for decimal instead of :F8 in interpolation
            logger.Info(() => $"DFPPS+ processed {payout.TotalSharesProcessed:N0} shares | " +
                              $"PPS Rate: {payout.PpsRate.ToString("F8")} per difficulty unit | " +
                              $"Block {payout.Block.BlockHeight}");
        }

        return shareCutOffDate;
    }

    private void HandleNoSharesFound(PoolConfig poolConfig, Block block, IPayoutHandler payoutHandler,
        Dictionary<string, decimal> rewardsDict, decimal rewardAmount)
    {
        if (!string.IsNullOrWhiteSpace(block.Miner))
        {
            rewardsDict[block.Miner] = rewardAmount;
            logger.Warn(() => $"Payout: No shares found for block {block.BlockHeight}. Crediting {payoutHandler.FormatAmount(rewardAmount)} to block finder {block.Miner}");
        }
        else
        {
            logger.Warn(() => $"Payout: No shares found for block {block.BlockHeight} and no block finder specified.");
        }
    }

    private void BuildFaultHandlingPolicy()
    {
        var retry = Policy
            .Handle<DbException>()
            .Or<SocketException>()
            .Or<TimeoutException>()
            .RetryAsync(RetryCount, OnPolicyRetry);

        shareReadFaultPolicy = retry;
    }

    private static void OnPolicyRetry(Exception ex, int retry, object context)
    {
        logger.Warn(() => $"Payout: Retry {retry} due to {ex.GetType().Name}: {ex.Message}");
    }

    private sealed class PendingBlockPayout
    {
        public PendingBlockPayout(Block block, decimal netReward)
        {
            Block = block;
            NetReward = netReward;
            NetworkDifficulty = block.NetworkDifficulty > 0 ? block.NetworkDifficulty : 1.0;
            PpsRate = NetworkDifficulty > 0 ? netReward / (decimal)NetworkDifficulty : 0m;
        }

        public Block Block { get; }
        public decimal NetReward { get; }
        public double NetworkDifficulty { get; }
        public decimal PpsRate { get; }
        public Dictionary<string, double> Shares { get; } = new();
        public Dictionary<string, decimal> Rewards { get; } = new();
        public DateTime? ShareCutOffDate { get; set; }
        public long TotalSharesProcessed { get; set; }
        public double TotalShareDifficulty { get; set; }
    }
}