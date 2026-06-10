using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

/// <summary>
/// Coin agnostic payment processor
/// </summary>
public class PayoutManager : BackgroundService
{
    public PayoutManager(IComponentContext ctx,
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(messageBus);

        this.ctx = ctx;
        this.cf = cf;
        this.blockRepo = blockRepo;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;

        interval = TimeSpan.FromSeconds(clusterConfig.PaymentProcessing.Interval > 0 ?
            clusterConfig.PaymentProcessing.Interval : 600);
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly IComponentContext ctx;
    private readonly IShareRepository shareRepo;
    private readonly IMessageBus messageBus;
    private readonly TimeSpan interval;
    private readonly ConcurrentDictionary<string, IMiningPool> pools = new();
    private readonly ClusterConfig clusterConfig;
    private readonly CompositeDisposable disposables = new();

#if !DEBUG
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromSeconds(15);
#endif

    private void AttachPool(IMiningPool pool)
    {
        pools.TryAdd(pool.Config.Id, pool);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if (notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private async Task ProcessPoolsAsync(CancellationToken ct)
    {
        foreach (var pool in pools.Values.ToArray().Where(x => x.Config.Enabled && x.Config.PaymentProcessing.Enabled))
        {
            var poolConfig = pool.Config;

            logger.Info(() => $"Processing payments for pool {poolConfig.Id}");

            try
            {
                var family = HandleFamilyOverride(poolConfig.Template.Family, poolConfig);

                // resolve payout handler
                var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>>>()
                    .First(x => x.Value.Metadata.SupportedFamilies.Contains(family)).Value;

                var handler = handlerImpl.Value;
                await handler.ConfigureAsync(clusterConfig, poolConfig, ct);

                // resolve payout scheme
                var scheme = ctx.ResolveKeyed<IPayoutScheme>(poolConfig.PaymentProcessing.PayoutScheme);

                await UpdatePoolBalancesAsync(pool, poolConfig, handler, scheme, ct);
                await PayoutPoolBalancesAsync(pool, poolConfig, handler, ct);
            }

            catch (InvalidOperationException ex)
            {
                logger.Error(ex.InnerException ?? ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }

            catch (AggregateException ex)
            {
                switch (ex.InnerException)
                {
                    case HttpRequestException httpEx:
                        logger.Error(() => $"[{poolConfig.Id}] Payment processing failed: {httpEx.Message}");
                        break;

                    default:
                        logger.Error(ex.InnerException, () => $"[{poolConfig.Id}] Payment processing failed");
                        break;
                }
            }

            catch (Exception ex)
            {
                logger.Error(ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }
        }
    }

    private static CoinFamily HandleFamilyOverride(CoinFamily family, PoolConfig pool)
    {
        switch (family)
        {
            case CoinFamily.Equihash:
                var equihashTemplate = pool.Template.As<EquihashCoinTemplate>();

                if (equihashTemplate.UseBitcoinPayoutHandler)
                    return CoinFamily.Bitcoin;

                break;

            case CoinFamily.Progpow:
                return CoinFamily.Bitcoin;
        }

        return family;
    }

    private async Task UpdatePoolBalancesAsync(
        IMiningPool pool,
        PoolConfig poolConfig,
        IPayoutHandler handler,
        IPayoutScheme scheme,
        CancellationToken ct)
    {

        Console.WriteLine($"[Timing] UpdatePoolBalancesAsync started for pool {poolConfig.Id}");

        // 1. Get pending blocks
        var swPending = Stopwatch.StartNew();
        var pendingBlocks = await cf.Run(con => blockRepo.GetPendingBlocksForPoolAsync(con, poolConfig.Id));
        swPending.Stop();
        Console.WriteLine($"[Timing] GetPendingBlocksForPoolAsync took {swPending.ElapsedMilliseconds} ms for pool {poolConfig.Id} ({pendingBlocks.Count()} blocks)");
        // 2. Classify blocks
        var swClassify = Stopwatch.StartNew();
        var updatedBlocks = await handler.ClassifyBlocksAsync(pool, pendingBlocks, ct);
        swClassify.Stop();
        Console.WriteLine($"[Timing] ClassifyBlocksAsync took {swClassify.ElapsedMilliseconds} ms for pool {poolConfig.Id} ({updatedBlocks.Count()} blocks to update)");

        if (updatedBlocks.Any())
        {
            Block lastBlock = null;
            foreach (var block in updatedBlocks.OrderBy(x => x.Created))
            {
                await cf.RunTx(async (con, tx) =>
                {
                    // fill block effort if empty
                    if (!block.Effort.HasValue)
                    {
                        var blockEffort = Stopwatch.StartNew();
                        Console.WriteLine($"[Timing] Calculating block effort for block {block.BlockHeight}");
                        await CalculateBlockEffortAsync(pool, poolConfig, block, lastBlock, handler, ct);
                        blockEffort.Stop();
                        Console.WriteLine($"[Timing] CalculateBlockEffortAsync took {blockEffort.ElapsedMilliseconds} ms → Effort = {block.Effort}");
                    }

                    switch (block.Status)
                    {
                        case BlockStatus.Confirmed:
                            var swReward = Stopwatch.StartNew();
                            var blockReward = await handler.UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);
                            swReward.Stop();
                            Console.WriteLine($"[Timing] UpdateBlockRewardBalancesAsync took {swReward.ElapsedMilliseconds} ms");

                            var swUpdateBlock = Stopwatch.StartNew();
                            await blockRepo.UpdateBlockAsync(con, tx, block);
                            swUpdateBlock.Stop();
                            Console.WriteLine($"[Timing] UpdateBlockAsync (Confirmed) took {swUpdateBlock.ElapsedMilliseconds} ms");

                            var swBalances = Stopwatch.StartNew();
                            await scheme.UpdateBalancesAsync(con, tx, pool, handler, block, blockReward, ct);
                            swBalances.Stop();
                            Console.WriteLine($"[Timing] UpdateBalancesAsync took {swBalances.ElapsedMilliseconds} ms");
                            break;

                        case BlockStatus.Orphaned:
                        case BlockStatus.Pending:
                            var swStatusUpdate = Stopwatch.StartNew();
                            await blockRepo.UpdateBlockAsync(con, tx, block);
                            swStatusUpdate.Stop();
                            Console.WriteLine($"[Timing] UpdateBlockAsync ({block.Status}) took {swStatusUpdate.ElapsedMilliseconds} ms");
                            break;
                    }
                    if (block.Status == BlockStatus.Confirmed ||
                        block.Status == BlockStatus.Orphaned ||
                        block.Status == BlockStatus.Pending)
                    {
                        lastBlock = block;
                    }
                });
            }
        }
        else
        {
            Console.WriteLine($"[Timing] No updated blocks to process for pool {poolConfig.Id}");
        }
    }

    private async Task PayoutPoolBalancesAsync(IMiningPool pool, PoolConfig config, IPayoutHandler handler, CancellationToken ct)
    {
        var poolBalancesOverMinimum = await cf.Run(con =>
            balanceRepo.GetPoolBalancesOverThresholdAsync(con, config.Id, config.PaymentProcessing.MinimumPayment));

        if (poolBalancesOverMinimum.Length > 0)
        {
            try
            {
                await handler.PayoutAsync(pool, poolBalancesOverMinimum, ct);
            }

            catch (Exception ex)
            {
                await NotifyPayoutFailureAsync(poolBalancesOverMinimum, config, ex);
                throw;
            }
        }

        else
            logger.Info(() => $"No balances over configured minimum payout for pool {config.Id}");
    }

    private Task NotifyPayoutFailureAsync(Balance[] balances, PoolConfig pool, Exception ex)
    {
        messageBus.SendMessage(new PaymentNotification(pool.Id, ex.Message, balances.Sum(x => x.Amount), pool.Template.Symbol));

        return Task.CompletedTask;
    }

    private async Task GetLastBlockBeforeAsync()
    {

    }

    private async Task CalculateBlockEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, Block lastBlock, IPayoutHandler handler, CancellationToken ct)
    {
        // get share date-range
        var from = DateTime.MinValue;
        var to = block.Created;

        // get previous block
        lastBlock ??= await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
                BlockStatus.Confirmed,
                BlockStatus.Orphaned,
                BlockStatus.Pending,
            }, block.Created));

        if (lastBlock != null)
            from = lastBlock.Created;

        block.Effort = await cf.Run(con =>
            shareRepo.GetEffectiveAccumulatedShareDifficultyBetweenAsync(con, pool.Config.Id, from, to, ct));

        if (!block.Effort.HasValue)
            Console.WriteLine($"[PayoutAsync] GetEffectiveAccumulatedShareDifficultyBetweenAsync failed");

        if (block.Effort.HasValue)
        {
            block.Effort = handler.AdjustBlockEffort(block.Effort.Value);
            if (!block.Effort.HasValue)
                Console.WriteLine($"[PayoutAsync] AdjustBlockEffort failed");
        }

    }

    private async Task CalculateMinerEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        // get share date-range
        var from = DateTime.MinValue;
        var to = block.Created;
        var miner = block.Miner;

        // get last block for pool
        var lastBlock = await cf.Run(con => blockRepo.GetMinerBlockBeforeAsync(con, poolConfig.Id, miner, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
        }, block.Created, ct));

        if (lastBlock != null)
            from = lastBlock.Created;
        block.MinerEffort = await cf.Run(con => shareRepo.GetMinerShareDifficultyBetweenAsync(con, pool.Config.Id, miner, from, to, ct));

        if (block.MinerEffort.HasValue)
            block.MinerEffort = handler.AdjustBlockEffort(block.MinerEffort.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // monitor pool lifetime
            disposables.Add(messageBus.Listen<PoolStatusNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnPoolStatusNotification));

            logger.Info(() => "Online");

            // Allow all pools to actually come up before the first payment processing run
            await Task.Delay(initialRunDelay, ct);

            using var timer = new PeriodicTimer(interval);

            do
            {
                try
                {
                    await ProcessPoolsAsync(ct);
                }

                catch (OperationCanceledException)
                {
                    // ignored
                }

                catch (Exception ex)
                {
                    logger.Error(ex);
                }
            } while (await timer.WaitForNextTickAsync(ct));

            logger.Info(() => "Offline");
        }

        finally
        {
            disposables.Dispose();
        }
    }
}
