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
public class DFPPSPlusPaymentScheme : IPayoutScheme
{
    public DFPPSPlusPaymentScheme(
        IConnectionFactory cf,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);

        this.cf = cf;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;

        BuildFaultHandlingPolicy();
    }

    private readonly IConnectionFactory cf;
    private readonly IShareRepository shareRepo;
    private readonly IBalanceRepository balanceRepo;

    private static readonly ILogger logger = LogManager.GetLogger("DFPPS+ Payment");
    private const int RetryCount = 4;
    private const long PostgresTimestampPrecisionTicks = 10;
    private IAsyncPolicy shareReadFaultPolicy;

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool,
        IPayoutHandler payoutHandler, Block block, decimal blockReward, CancellationToken ct)
    {
        var poolConfig = pool.Config;
        var netReward = CalculateNetReward(blockReward, poolConfig);

        if (netReward <= 0)
        {
            logger.Info(() => $"Payout: Net reward for pool {poolConfig.Id}, block {block.BlockHeight} is {netReward}. Skipping.");
            return;
        }

        logger.Info(() => $"Payout: Block {block.BlockHeight} | Net Reward for DFPPS+: {payoutHandler.FormatAmount(netReward)}");

        var sharesDict = new Dictionary<string, double>();      // miner -> total adjusted difficulty
        var rewardsDict = new Dictionary<string, decimal>();    // miner -> reward

        var shareCutOffDate = await CalculatePPSRewardsAsync(con, pool, payoutHandler, block, netReward,
            sharesDict, rewardsDict, ct);

        if (rewardsDict.Count == 0)
        {
            HandleNoSharesFound(poolConfig, block, payoutHandler, rewardsDict, netReward);
        }

        // === CREDIT BALANCES ===
        decimal totalCredited = 0m;

        foreach (var address in rewardsDict.Keys.OrderByDescending(a => rewardsDict[a]))
        {
            var amount = rewardsDict[address];
            var shareDiff = sharesDict.GetValueOrDefault(address);

            if (amount > 0)
            {
                logger.Info(() => $"Payout: Crediting {address} with {payoutHandler.FormatAmount(amount)} " +
                                  $"for {FormatUtil.FormatQuantity(shareDiff)} share difficulty (block {block.BlockHeight})");

                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount,
                    $"DFPPS+ reward for {FormatUtil.FormatQuantity(shareDiff)} share difficulty - block {block.BlockHeight}");

                totalCredited += amount;
            }
        }

        logger.Info(() => $"Payout: Total credited to balances for block {block.BlockHeight}: {payoutHandler.FormatAmount(totalCredited)}");

        if (totalCredited > netReward * 1.02m) // Allow small tolerance for rounding
        {
            var warningMessage = $"Payout: Total credited {payoutHandler.FormatAmount(totalCredited)} exceeds net reward {payoutHandler.FormatAmount(netReward)} for block {block.BlockHeight}";
            logger.Warn(() => warningMessage);
            Console.WriteLine($"[DFPPS+ Payment] {warningMessage}");
        }

        // === DELETE PROCESSED SHARES ===
        if (shareCutOffDate.HasValue && totalCredited > 0)
        {
            var deleteCutOffDate = shareCutOffDate.Value.AddTicks(PostgresTimestampPrecisionTicks);
            logger.Info(() => $"Payout: Deleting processed shares through {shareCutOffDate.Value:O}");
            await shareRepo.DeleteSharesBeforeAsync(con, tx, poolConfig.Id, deleteCutOffDate, ct);
        }
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

    private async Task<DateTime?> CalculatePPSRewardsAsync(IDbConnection con, IMiningPool pool,
        IPayoutHandler payoutHandler, Block block, decimal netReward,
        Dictionary<string, double> sharesDict, Dictionary<string, decimal> rewardsDict, CancellationToken ct)
    {
        var nd = block.NetworkDifficulty > 0 ? block.NetworkDifficulty : 1.0;
        var ppsRate = netReward / (decimal)nd;   // Fixed PPS rate per unit of difficulty

        DateTime? shareCutOffDate = null;
        long totalSharesProcessed = 0;
        double totalShareDifficulty = 0.0;

        await shareReadFaultPolicy.ExecuteAsync(async () =>
        {
            foreach (var share in shareRepo.StreamSharesBefore(con, pool.Config.Id, block.Created, true))
            {
                totalSharesProcessed++;

                var address = share.Miner;
                var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);

                // Accumulate share difficulty
                sharesDict[address] = sharesDict.GetValueOrDefault(address) + shareDiffAdjusted;
                totalShareDifficulty += shareDiffAdjusted;

                // Calculate fixed PPS reward
                var reward = (decimal)shareDiffAdjusted * ppsRate;

                if (reward > 0)
                {
                    rewardsDict[address] = rewardsDict.GetValueOrDefault(address) + reward;
                }

                if (shareCutOffDate == null || share.Created > shareCutOffDate.Value)
                    shareCutOffDate = share.Created;
            }
        });

        logger.Info(() => $"DFPPS+ calculation for block {block.BlockHeight} | " +
                          $"Processed shares: {totalSharesProcessed} | " +
                          $"Total share difficulty: {totalShareDifficulty:F2} | " +
                          $"PPS Rate: {ppsRate} per difficulty unit | " +
                          $"Total payout: {payoutHandler.FormatAmount(rewardsDict.Values.Sum())}");

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
}