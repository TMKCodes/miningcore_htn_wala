using System.Data;
using System.Data.Common;
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
/// PPLNS70 payout scheme implementation
/// </summary>
// ReSharper disable once InconsistentNaming
public class PPLNS70PaymentScheme : IPayoutScheme
{
    public PPLNS70PaymentScheme(
        IConnectionFactory cf,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);

        this.cf = cf;
        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;
        this.balanceRepo = balanceRepo;

        BuildFaultHandlingPolicy();
    }

    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly IShareRepository shareRepo;
    private static readonly ILogger logger = LogManager.GetLogger("PPLNS70 Payment");

    private const int RetryCount = 4;
    private IAsyncPolicy shareReadFaultPolicy;

    private class Config
    {
        public decimal Factor { get; set; }
    }

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler,
        Block block, decimal blockReward, CancellationToken ct)
    {

        logger.Info(() => $"elva PPLNS70 - CalculateRewardsAsync Strating");

        var poolConfig = pool.Config;
        var payoutConfig = poolConfig.PaymentProcessing.PayoutSchemeConfig;


        logger.Info(() => $"elva PPLNS70 - CalculateRewardsAsync Strating - poolConfig {poolConfig} - payoutConfig {payoutConfig}");


        // PPLNS70 window (see https://bitcointalk.org/index.php?topic=39832)
        var window = payoutConfig?.ToObject<Config>()?.Factor ?? 2.0m;

        // calculate rewards
        var shares = new Dictionary<string, double>();
        var rewards = new Dictionary<string, decimal>();
        var shareCutOffDate = await CalculateRewardsAsync(pool, payoutHandler, window, block, blockReward, shares, rewards, ct);

        // update balances
        foreach (var address in rewards.Keys)
        {
            var amount = rewards[address];

            if (amount > 0)
            {
                if (shares.ContainsKey(address))
                {
                    logger.Info(() => $"elva PPLNS70 - Crediting {address} with {payoutHandler.FormatAmount(amount)} for {FormatUtil.FormatQuantity(shares[address])} ({shares[address]}) shares for block {block.BlockHeight}");
                }
                else
                {
                    logger.Warn(() => $"elva PPLNS70 - Address {address} not found in shares dictionary for block {block.BlockHeight}. Skipping crediting process for this entry.");
                }
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for {FormatUtil.FormatQuantity(shares[address])} shares for block {block.BlockHeight}");
            }
        }

        // delete discarded shares
        if (shareCutOffDate.HasValue)
        {
            var cutOffCount = await shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, shareCutOffDate.Value, ct);

            if (cutOffCount > 0)
            {
                await LogDiscardedSharesAsync(ct, poolConfig, block, shareCutOffDate.Value);

                logger.Info(() => $"Deleting {cutOffCount} discarded shares before {shareCutOffDate.Value:O}");
                await shareRepo.DeleteSharesBeforeAsync(con, tx, poolConfig.Id, shareCutOffDate.Value, ct);
            }
        }

        // diagnostics
        var totalShareCount = shares.Values.ToList().Sum(x => new decimal(x));
        var totalRewards = rewards.Values.ToList().Sum(x => x);

        if (totalRewards > 0)
            logger.Info(() => $"{FormatUtil.FormatQuantity((double)totalShareCount)} ({Math.Round(totalShareCount, 2)}) shares contributed to a total payout of {payoutHandler.FormatAmount(totalRewards)} ({totalRewards / blockReward * 100:0.00}% of block reward) to {rewards.Keys.Count} addresses");
    }

    private async Task LogDiscardedSharesAsync(CancellationToken ct, PoolConfig poolConfig, Block block, DateTime value)
    {
        var before = value;
        var pageSize = 50000;
        var currentPage = 0;
        var shares = new Dictionary<string, double>();

        while (true)
        {
            logger.Info(() => $"Fetching page {currentPage} of discarded shares for pool {poolConfig.Id}, block {block.BlockHeight}");

            var page = await shareReadFaultPolicy.ExecuteAsync(() =>
                cf.Run(con => shareRepo.ReadSharesBeforeAsync(con, poolConfig.Id, before, false, pageSize, ct)));

            currentPage++;

            for (var i = 0; i < page.Length; i++)
            {
                var share = page[i];
                var address = share.Miner;

                // record attributed shares for diagnostic purposes
                if (!shares.ContainsKey(address))
                    shares[address] = share.Difficulty;
                else
                    shares[address] += share.Difficulty;
            }

            if (page.Length < pageSize)
                break;

            before = page[^1].Created;
        }

        if (shares.Keys.Count > 0)
        {
            // sort addresses by shares
            var addressesByShares = shares.Keys.OrderByDescending(x => shares[x]);

            logger.Info(() => $"{FormatUtil.FormatQuantity(shares.Values.Sum())} ({shares.Values.Sum()}) total discarded shares, block {block.BlockHeight}");

            foreach (var address in addressesByShares)
                logger.Info(() => $"{address} = {FormatUtil.FormatQuantity(shares[address])} ({shares[address]}) discarded shares, block {block.BlockHeight}");
        }
    }

    #endregion // IPayoutScheme

    private async Task<DateTime?> CalculateRewardsAsync(IMiningPool pool, IPayoutHandler payoutHandler, decimal window, Block block, decimal blockReward,
        Dictionary<string, double> shares, Dictionary<string, decimal> rewards, CancellationToken ct)
    {
        logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> CalculateRewardsAsync Starting for block {block.BlockHeight}");

        var poolConfig = pool.Config;
        var done = false;
        var before = block.Created;
        var inclusive = true;
        var pageSize = 50000;
        var currentPage = 0;
        var accumulatedScore = 0.0m;

        // Calcul de la récompense initiale pour le mineur ayant trouvé le bloc
        var finderReward = blockReward * 0.70m;
        var remainingReward = blockReward - finderReward; // 30 % restants pour distribution proportionnelle

        var blockFinder = block.Miner; // Le mineur du bloc

        // Allouer les 70 % initiaux au mineur du bloc
        if (blockFinder != null)
        {
            if (!rewards.ContainsKey(blockFinder))
            {
                rewards[blockFinder] = finderReward;
                logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Allocated {payoutHandler.FormatAmount(finderReward)} to block finder {blockFinder} for block {block.BlockHeight}");
            }
            else
            {
                rewards[blockFinder] += finderReward;
                logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Updated reward for block finder {blockFinder}: total {rewards[blockFinder]} for block {block.BlockHeight}");
            }

            // Ajouter une part symbolique dans `shares` pour inclure le block finder dans la distribution des 30 % restants
            if (!shares.ContainsKey(blockFinder))
            {
                shares[blockFinder] = 0.7; // Part symbolique minimale
                logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Added symbolic share for block finder {blockFinder} for block {block.BlockHeight}");
            }

            // Vérifier si le block finder est le seul mineur actif
            if (shares.Count == 1 && shares.ContainsKey(blockFinder) && shares[blockFinder] > 0)
            {
                // Allouer les 30 % restants au block finder
                rewards[blockFinder] += remainingReward;
                logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Allocated remaining {payoutHandler.FormatAmount(remainingReward)} to block finder {blockFinder} as the only active miner for block {block.BlockHeight}");
                return null; // Pas besoin de continuer, car tous les rewards ont été distribués
            }
        }
        else
        {
            logger.Warn(() => $"elva Debug PPLNS70PayementScheme -----> Block finder for block {block.BlockHeight} is null, skipping block finder reward allocation.");
        }

        // Si le block finder n'est pas le seul actif, distribuer les 30 % restants de manière proportionnelle
        DateTime? shareCutOffDate = null;

        while (!done && !ct.IsCancellationRequested)
        {
            logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Fetching page {currentPage} of shares for pool {poolConfig.Id}, block {block.BlockHeight}");

            var page = await shareReadFaultPolicy.ExecuteAsync(() =>
                cf.Run(con => shareRepo.ReadSharesBeforeAsync(con, poolConfig.Id, before, inclusive, pageSize, ct)));

            inclusive = false;
            currentPage++;

            for (var i = 0; !done && i < page.Length; i++)
            {
                var share = page[i];
                var address = share.Miner;

                var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);

                // Ajouter ou mettre à jour les parts du mineur
                if (!shares.ContainsKey(address))
                {
                    shares[address] = shareDiffAdjusted;
                    logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Added new share for miner {address} at adjusted difficulty {shareDiffAdjusted} for block {block.BlockHeight}");
                }
                else
                {
                    shares[address] += shareDiffAdjusted;
                    logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Updated share for miner {address}: total difficulty {shares[address]} for block {block.BlockHeight}");
                }

                var score = (decimal)(shareDiffAdjusted / share.NetworkDifficulty);
                accumulatedScore += score;

                // Limiter le score accumulé
                if (accumulatedScore >= window)
                {
                    score = window - accumulatedScore;
                    shareCutOffDate = share.Created;
                    done = true;
                }

                // Calcul de la récompense pour chaque mineur, basé sur 30% du blockReward
                var reward = score * remainingReward / window;
                remainingReward -= reward;

                logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Reward calculated for miner {address}: {reward} for block {block.BlockHeight}");

                if (reward > 0)
                {
                    if (!rewards.ContainsKey(address))
                    {
                        rewards[address] = reward;
                        logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Added new reward entry for miner {address}: {reward} for block {block.BlockHeight}");
                    }
                    else
                    {
                        rewards[address] += reward;
                        logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Updated reward for miner {address}: total {rewards[address]} for block {block.BlockHeight}");
                    }
                }

                // Vérification de l'épuisement de la récompense restante
                if (remainingReward <= 0 && !done)
                {
                    logger.Warn(() => $"elva Debug PPLNS70PayementScheme -----> Remaining reward exhausted for block {block.BlockHeight}");
                    done = true;
                }
            }

            if (page.Length < pageSize)
                break;

            before = page[^1].Created;
        }

        logger.Info(() => $"elva Debug PPLNS70PayementScheme -----> Balance-calculation completed for block {block.BlockHeight} with accumulated score {accumulatedScore:0.####} ({(accumulatedScore / window) * 100:0.#}%)");

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
