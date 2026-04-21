using System;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Linq;
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
/// FPPS (Full Pay Per Share) payout scheme implementation.
///
/// NOTE: Miningcore's payout processing is block-driven. This implementation
/// credits shares when a block gets confirmed by paying each share its expected
/// value based on the block reward (including transaction fees) and the share's
/// network difficulty at submission time.
/// </summary>
// ReSharper disable once InconsistentNaming
public class FPPSPaymentScheme : IPayoutScheme
{
  public FPPSPaymentScheme(
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
  private static readonly ILogger logger = LogManager.GetLogger("FPPS Payment");

  private const int RetryCount = 4;
  private IAsyncPolicy shareReadFaultPolicy;

  #region IPayoutScheme

  public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler,
      Block block, decimal blockReward, CancellationToken ct)
  {
    var poolConfig = pool.Config;

    if (blockReward <= 0)
    {
      logger.Info(() => $"Block reward for pool {poolConfig.Id}, block {block.BlockHeight} is {blockReward}. Skipping FPPS payout.");
      return;
    }

    // calculate rewards
    var shares = new Dictionary<string, double>();
    var rewards = new Dictionary<string, decimal>();
    var shareCutOffDate = await CalculateRewardsAsync(pool, payoutHandler, block, blockReward, shares, rewards, ct);

    // update balances
    foreach (var address in rewards.Keys)
    {
      var amount = rewards[address];

      if (amount > 0)
      {
        logger.Info(() => $"Crediting {address} with {payoutHandler.FormatAmount(amount)} for {FormatUtil.FormatQuantity(shares[address])} ({shares[address]}) shares for block {block.BlockHeight}");
        await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount,
            $"Reward for {FormatUtil.FormatQuantity(shares[address])} shares for block {block.BlockHeight}");
      }
    }

    // delete paid shares
    if (shareCutOffDate.HasValue)
    {
      // ShareRepository deletes shares using a strict "created < before" predicate.
      // For FPPS we want to delete *all* shares we just paid, including those whose
      // Created timestamp equals the newest processed share. Use an exclusive cutoff
      // (cutoff + 1 microsecond) to avoid leaving (and thus re-paying) the last batch.
      var deleteBefore = shareCutOffDate.Value.AddTicks(10); // 1µs (Postgres TIMESTAMPTZ resolution)

      var cutOffCount = await shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, deleteBefore, ct);

      if (cutOffCount > 0)
      {
        logger.Info(() => $"Deleting {cutOffCount} paid shares before {deleteBefore:O}");
        await shareRepo.DeleteSharesBeforeAsync(con, tx, poolConfig.Id, deleteBefore, ct);
      }
    }

    // diagnostics
    var totalShareCount = shares.Values.ToList().Sum(x => new decimal(x));
    var totalRewards = rewards.Values.ToList().Sum(x => x);

    if (totalRewards > 0)
    {
      logger.Info(() =>
          $"{FormatUtil.FormatQuantity((double)totalShareCount)} ({Math.Round(totalShareCount, 2)}) shares contributed to a total payout of {payoutHandler.FormatAmount(totalRewards)} " +
          $"({(blockReward > 0 ? (totalRewards / blockReward) * 100 : 0):0.00}% of block reward) to {rewards.Keys.Count} addresses");
    }
  }

  #endregion // IPayoutScheme

  private async Task<DateTime?> CalculateRewardsAsync(IMiningPool pool, IPayoutHandler payoutHandler, Block block, decimal blockReward,
      Dictionary<string, double> shares, Dictionary<string, decimal> rewards, CancellationToken ct)
  {
    var poolConfig = pool.Config;
    var done = false;
    var before = block.Created;
    var inclusive = true;
    var pageSize = 100000;
    var currentPage = 0;
    DateTime? shareCutOffDate = null;
    var accumulatedScore = 0.0m;

    while (!done && !ct.IsCancellationRequested)
    {
      logger.Info(() => $"Fetching page {currentPage} of shares for pool {poolConfig.Id}, block {block.BlockHeight}");

      var page = await shareReadFaultPolicy.ExecuteAsync(() =>
          cf.Run(c => shareRepo.ReadSharesBeforeAsync(c, poolConfig.Id, before, inclusive, pageSize, ct)));

      inclusive = false;
      currentPage++;

      for (var i = 0; i < page.Length; i++)
      {
        var share = page[i];
        var address = share.Miner;
        var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);

        // record attributed shares for diagnostic purposes
        if (!shares.ContainsKey(address))
          shares[address] = shareDiffAdjusted;
        else
          shares[address] += shareDiffAdjusted;

        // expected block fraction contributed by this share
        var score = (decimal)(shareDiffAdjusted / share.NetworkDifficulty);
        accumulatedScore += score;

        // FPPS payout: expected value per share based on this block's reward (incl. tx fees)
        var reward = score * blockReward;

        if (reward > 0)
        {
          if (!rewards.ContainsKey(address))
            rewards[address] = reward;
          else
            rewards[address] += reward;
        }

        // track newest share we included so we can delete processed shares afterwards
        if (shareCutOffDate == null || share.Created > shareCutOffDate)
          shareCutOffDate = share.Created;
      }

      if (page.Length < pageSize)
        break;

      if (page.Length <= 0)
        done = true;
      else
        before = page[^1].Created;
    }

    logger.Info(() => $"Balance-calculation for pool {poolConfig.Id}, block {block.BlockHeight} completed with accumulated score {accumulatedScore:0.####} (expected blocks)");

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
    logger.Warn(() => $"Retry {retry} due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
  }
}
