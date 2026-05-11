using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
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
/// DFPPS+ payout scheme implementation.
///
/// Miningcore's payout processing is block-driven.
/// Each confirmed block triggers crediting of all unpaid shares with their
/// proportional expected value: (shareDiff / block.NetworkDifficulty) * netBlockReward.
/// The pool absorbs all variance.
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

  public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler,
    Block block, decimal blockReward, CancellationToken ct)
  {
    var poolConfig = pool.Config;

    if (blockReward <= 0)
    {
      logger.Info(() => $"Payout: Block reward for pool {poolConfig.Id}, block {block.BlockHeight} is {blockReward}. Skipping.");
      return;
    }

    logger.Info(() => $"Payout: Block {block.BlockHeight} | Reward for DFPPS+ distribution: {payoutHandler.FormatAmount(blockReward)}");

    if (blockReward <= 0 || block.NetworkDifficulty <= 0)
    {
      logger.Warn(() => $"Payout: Invalid reward or difficulty for block {block.BlockHeight}. Skipping.");
      return;
    }

    var shares = new Dictionary<string, double>();
    var rewards = new Dictionary<string, decimal>();

    var shareCutOffDate = await CalculateRewardsAsync(pool, payoutHandler, block, blockReward, shares, rewards, ct);

    if (shares.Count == 0)
    {
      if (!string.IsNullOrWhiteSpace(block.Miner))
      {
        rewards[block.Miner] = blockReward;

        logger.Warn(() => $"Payout: No DFPPS+ shares found for block {block.BlockHeight}. Crediting full reward {payoutHandler.FormatAmount(blockReward)} to block finder {block.Miner}");
      }
      else
      {
        logger.Warn(() => $"Payout: No DFPPS+ shares found for block {block.BlockHeight} and block finder is empty. Reward cannot be credited automatically");
      }
    }

    // === CREDIT BALANCES ===
    var totalCredited = 0m;
    foreach (var address in rewards.Keys.OrderByDescending(a => rewards[a]))
    {
      var amount = rewards[address];
      var shareCount = shares.GetValueOrDefault(address);

      if (amount > 0)
      {
        logger.Info(() => $"Payout: Crediting {address} with {payoutHandler.FormatAmount(amount)} " +
                          $"for {FormatUtil.FormatQuantity(shareCount)} shares (block {block.BlockHeight})");

        await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount,
          $"DFPPS+ reward for {FormatUtil.FormatQuantity(shareCount)} shares - block {block.BlockHeight}");

        totalCredited += amount;
      }
    }

    logger.Info(() => $"Payout: Total credited to balances for block {block.BlockHeight}: {payoutHandler.FormatAmount(totalCredited)}");

    if (totalCredited > blockReward)
    {
      var warningMessage = $"Payout: Total credited {payoutHandler.FormatAmount(totalCredited)} exceeds block reward {payoutHandler.FormatAmount(blockReward)} for block {block.BlockHeight}";
      logger.Warn(() => warningMessage);
      Console.WriteLine($"[DFPPS+ Payment] {warningMessage}");
    }

    // === DELETE SHARES ONLY IF WE ACTUALLY PROCESSED SOME ===
    if (shareCutOffDate.HasValue && totalCredited > 0)
    {
      var deleteCutOffDate = shareCutOffDate.Value.AddTicks(PostgresTimestampPrecisionTicks);
      var cutOffCount = await shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, deleteCutOffDate, ct);

      if (cutOffCount > 0)
      {
        logger.Info(() => $"Payout: Deleting {cutOffCount} processed shares through {shareCutOffDate.Value:O}");
        await shareRepo.DeleteSharesBeforeAsync(con, tx, poolConfig.Id, deleteCutOffDate, ct);
      }
    }
    else if (shareCutOffDate.HasValue)
    {
      logger.Warn(() => $"Payout: No rewards calculated for block {block.BlockHeight} — shares will NOT be deleted yet.");
    }

    // Diagnostics
    var totalShareCount = shares.Values.Sum(x => (decimal)x);
    var totalRewards = rewards.Values.Sum();

    if (totalRewards > 0)
    {
      var percentageOfGross = (totalRewards / blockReward) * 100;
      logger.Info(() =>
          $"{FormatUtil.FormatQuantity((double)totalShareCount)} shares contributed to " +
          $"{payoutHandler.FormatAmount(totalRewards)} ({percentageOfGross:0.00}% of gross) " +
          $"across {rewards.Count} addresses");
    }
  }
  #endregion

  private async Task<DateTime?> CalculateRewardsAsync(IMiningPool pool, IPayoutHandler payoutHandler, Block block,
    decimal netBlockReward, Dictionary<string, double> sharesDict, Dictionary<string, decimal> rewards, CancellationToken ct)
  {
    var poolConfig = pool.Config;
    var before = block.Created;
    var inclusive = true;
    const int pageSize = 100000;
    var currentPage = 0;
    DateTime? shareCutOffDate = null;
    long totalSharesProcessed = 0;
    double totalShareSum = 0.0;
    decimal totalExpectedScore = 0m;

    var blockNetworkDifficulty = block.NetworkDifficulty > 0 ? block.NetworkDifficulty : 1.0;

    logger.Info(() => $"Starting DFPPS+ calculation for block {block.BlockHeight} | " +
                      $"Net reward: {payoutHandler.FormatAmount(netBlockReward)} | " +
                      $"NetworkDifficulty: {blockNetworkDifficulty:0.##}");

    while (!ct.IsCancellationRequested)
    {
      var page = await shareReadFaultPolicy.ExecuteAsync(() =>
          cf.Run(c => shareRepo.ReadSharesBeforeAsync(c, poolConfig.Id, before, inclusive, pageSize, ct)));

      inclusive = false;
      currentPage++;

      if (page.Length == 0)
        break;

      foreach (var share in page)
      {
        totalSharesProcessed++;
        var address = share.Miner;
        var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);
        var score = (decimal)(shareDiffAdjusted / blockNetworkDifficulty);
        var reward = score * netBlockReward;

        sharesDict[address] = sharesDict.GetValueOrDefault(address) + shareDiffAdjusted;
        totalShareSum += shareDiffAdjusted;
        totalExpectedScore += score;

        if (reward > 0)
          rewards[address] = rewards.GetValueOrDefault(address) + reward;

        if (shareCutOffDate == null || share.Created > shareCutOffDate.Value)
          shareCutOffDate = share.Created;
      }

      if (page.Length < pageSize)
        break;

      before = page[^1].Created;
    }

    var totalPayout = rewards.Values.Sum();

    logger.Info(() => $"DFPPS+ calc for block {block.BlockHeight} | " +
                      $"Processed shares: {totalSharesProcessed} | " +
                      $"Total share sum: {totalShareSum:F2} | " +
                      $"Expected score: {totalExpectedScore:F8} | " +
                      $"Calculated total payout: {payoutHandler.FormatAmount(totalPayout)}");

    return shareCutOffDate;
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
