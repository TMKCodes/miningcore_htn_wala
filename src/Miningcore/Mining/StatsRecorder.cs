using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Hosting;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using NLog;
using Polly;

namespace Miningcore.Mining;

public class StatsRecorder : BackgroundService
{
    public StatsRecorder(IComponentContext ctx,
        IMasterClock clock,
        IConnectionFactory cf,
        IMessageBus messageBus,
        IMapper mapper,
        ClusterConfig clusterConfig,
        IShareRepository shareRepo,
        IStatsRepository statsRepo)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(statsRepo);

        this.clock = clock;
        this.cf = cf;
        this.mapper = mapper;
        this.messageBus = messageBus;
        this.shareRepo = shareRepo;
        this.statsRepo = statsRepo;
        this.clusterConfig = clusterConfig;

        updateInterval = TimeSpan.FromSeconds(clusterConfig.Statistics?.UpdateInterval ?? 180);
        gcInterval = TimeSpan.FromHours(clusterConfig.Statistics?.GcInterval ?? 4);
        hashrateCalculationWindow = TimeSpan.FromMinutes(clusterConfig.Statistics?.HashrateCalculationWindow ?? 30);
        cleanupDays = TimeSpan.FromDays(clusterConfig.Statistics?.CleanupDays ?? 180);

        BuildFaultHandlingPolicy();
    }

    private readonly IMasterClock clock;
    private readonly IStatsRepository statsRepo;
    private readonly IConnectionFactory cf;
    private readonly IMapper mapper;
    private readonly IMessageBus messageBus;
    private readonly IShareRepository shareRepo;
    private readonly ClusterConfig clusterConfig;
    private readonly CompositeDisposable disposables = new();
    private readonly ConcurrentDictionary<string, IMiningPool> pools = new();
    private readonly TimeSpan updateInterval;
    private readonly TimeSpan cleanupDays;
    private readonly TimeSpan gcInterval;
    private readonly TimeSpan hashrateCalculationWindow;
    private const int RetryCount = 4;
    private IAsyncPolicy readFaultPolicy;

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    private void AttachPool(IMiningPool pool)
    {
        pools.TryAdd(pool.Config.Id, pool);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if (notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private async Task UpdatePoolHashratesAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var timeFrom = now.Add(-hashrateCalculationWindow);

        var stats = new MinerWorkerPerformanceStats
        {
            Created = now
        };

        foreach (var poolId in pools.Keys)
        {
            if (ct.IsCancellationRequested)
                return;

            stats.PoolId = poolId;

            logger.Info(() => $"[{poolId}] Updating Statistics for pool");

            var pool = pools[poolId];

            // Fetch accumulated shares for the window
            var result = await readFaultPolicy.ExecuteAsync(() =>
                cf.Run(con => shareRepo.GetHashAccumulationBetweenAsync(con, poolId, timeFrom, now, ct)));

            var byMiner = result.GroupBy(x => x.Miner).ToArray();

            if (result.Length > 0)
            {
                // Pool miners count
                pool.PoolStats.ConnectedMiners = byMiner.Length;

                // ====================== POOL HASHRATE ======================
                var poolHashTimeFrame = hashrateCalculationWindow.TotalSeconds; // Fixed full window = most stable
                var poolHashesAccumulated = result.Sum(x => x.Sum);

                var poolHashrate = pool.HashrateFromShares(poolHashesAccumulated, poolHashTimeFrame);
                poolHashrate = Math.Floor(poolHashrate);

                pool.PoolStats.PoolHashrate = (ulong)poolHashrate;

                // Pool shares per second
                var poolHashesCountAccumulated = result.Sum(x => x.Count);
                pool.PoolStats.SharesPerSecond = (int)(poolHashesCountAccumulated / poolHashTimeFrame);

                messageBus.NotifyHashrateUpdated(pool.Config.Id, poolHashrate);

                logger.Info(() => $"[{poolId}] Pool hashrate: {FormatUtil.FormatHashrate(poolHashrate)} " +
                                  $"({pool.PoolStats.ConnectedMiners} miners)");
            }
            else
            {
                // No shares in window → reset
                pool.PoolStats.ConnectedMiners = 0;
                pool.PoolStats.PoolHashrate = 0;
                pool.PoolStats.SharesPerSecond = 0;

                messageBus.NotifyHashrateUpdated(pool.Config.Id, 0);

                logger.Info(() => $"[{poolId}] Reset performance stats for pool (no shares)");
            }

            // Persist pool stats
            await cf.RunTx(async (con, tx) =>
            {
                var mapped = new Persistence.Model.PoolStats { PoolId = poolId, Created = now };
                mapper.Map(pool.PoolStats, mapped);
                mapper.Map(pool.NetworkStats, mapped);
                await statsRepo.InsertPoolStatsAsync(con, tx, mapped, ct);
            });

            // ====================== MINER / WORKER HASHRATES ======================
            var previousMinerWorkerHashrates = await cf.Run(con =>
                statsRepo.GetPoolMinerWorkerHashratesAsync(con, poolId, ct));

            const char keySeparator = '.';

            string BuildKey(string miner, string worker = null) =>
                !string.IsNullOrEmpty(worker) ? $"{miner}{keySeparator}{worker}" : miner;

            var previousNonZero = new HashSet<string>(
                previousMinerWorkerHashrates.Select(x => BuildKey(x.Miner, x.Worker)));

            var currentNonZero = new HashSet<string>();

            foreach (var minerGroup in byMiner)
            {
                if (ct.IsCancellationRequested)
                    return;

                double minerTotalHashrate = 0;
                var miner = minerGroup.Key;

                await cf.RunTx(async (con, tx) =>
                {
                    stats.Miner = miner;

                    foreach (var item in minerGroup) // per worker
                    {
                        stats.Worker = item.Worker;
                        currentNonZero.Add(BuildKey(miner, item.Worker));

                        // === Better effective time window calculation ===
                        var firstShare = minerGroup.Min(x => x.FirstShare);
                        var lastShare = minerGroup.Max(x => x.LastShare);
                        var activeSeconds = (lastShare - firstShare).TotalSeconds;

                        // Clamp to reasonable range
                        var effectiveSeconds = Math.Clamp(activeSeconds, 30.0, hashrateCalculationWindow.TotalSeconds);

                        // Fallback for edge cases
                        if (effectiveSeconds < 1)
                            effectiveSeconds = 1;

                        // Calculate hashrate
                        var workerHashrate = pool.HashrateFromShares(item.Sum, effectiveSeconds);
                        workerHashrate = Math.Floor(workerHashrate);

                        minerTotalHashrate += workerHashrate;

                        stats.Hashrate = workerHashrate;
                        stats.SharesPerSecond = Math.Round(item.Count / effectiveSeconds, 3);

                        // Persist + broadcast
                        await statsRepo.InsertMinerWorkerPerformanceStatsAsync(con, tx, stats, ct);
                        messageBus.NotifyHashrateUpdated(pool.Config.Id, workerHashrate, miner, item.Worker);

                        logger.Info(() => $"[{poolId}] Worker {miner}{(!string.IsNullOrEmpty(item.Worker) ? $".{item.Worker}" : "")}: " +
                                          $"{FormatUtil.FormatHashrate(workerHashrate)}  ({stats.SharesPerSecond} shares/sec)");
                    }
                });

                // Miner total (without worker)
                messageBus.NotifyHashrateUpdated(pool.Config.Id, minerTotalHashrate, miner, null);
                logger.Info(() => $"[{poolId}] Miner {miner}: {FormatUtil.FormatHashrate(minerTotalHashrate)}");
            }

            // Reset orphaned (disconnected) miners/workers
            var orphaned = previousNonZero.Except(currentNonZero).ToArray();
            if (orphaned.Any())
            {
                await cf.RunTx(async (con, tx) =>
                {
                    stats.Hashrate = 0;
                    stats.SharesPerSecond = 0;

                    foreach (var key in orphaned)
                    {
                        var parts = key.Split(keySeparator);
                        stats.Miner = parts[0];
                        stats.Worker = parts.Length > 1 ? parts[1] : null;

                        await statsRepo.InsertMinerWorkerPerformanceStatsAsync(con, tx, stats, ct);
                        messageBus.NotifyHashrateUpdated(pool.Config.Id, 0, stats.Miner, stats.Worker);

                        var logName = string.IsNullOrEmpty(stats.Worker) ? stats.Miner : $"{stats.Miner}.{stats.Worker}";
                        logger.Info(() => $"[{poolId}] Reset performance stats for {logName}");
                    }
                });
            }
        }
    }

    private async Task StatsGcAsync(CancellationToken ct)
    {
        logger.Info(() => "Performing Stats GC");

        await cf.Run(async con =>
        {
            var cutOff = clock.Now.Add(-cleanupDays);

            var rowCount = await statsRepo.DeletePoolStatsBeforeAsync(con, cutOff, ct);
            if (rowCount > 0)
                logger.Info(() => $"Deleted {rowCount} old poolstats records");

            rowCount = await statsRepo.DeleteMinerStatsBeforeAsync(con, cutOff, ct);
            if (rowCount > 0)
                logger.Info(() => $"Deleted {rowCount} old minerstats records");
        });

        logger.Info(() => "Stats GC complete");
    }

    private async Task UpdateAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(updateInterval);

        do
        {
            try
            {
                await UpdatePoolHashratesAsync(ct);
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
    }

    private async Task GcAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(gcInterval);

        do
        {
            try
            {
                await StatsGcAsync(ct);
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
    }

    private void BuildFaultHandlingPolicy()
    {
        var retry = Policy
            .Handle<DbException>()
            .Or<SocketException>()
            .Or<TimeoutException>()
            .RetryAsync(RetryCount, OnPolicyRetry);

        readFaultPolicy = retry;
    }

    private static void OnPolicyRetry(Exception ex, int retry, object context)
    {
        logger.Warn(() => $"Retry {retry} due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
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

            // warm-up delay
            await Task.Delay(TimeSpan.FromSeconds(15), ct);

            await Task.WhenAll(
                UpdateAsync(ct),
                GcAsync(ct));

            logger.Info(() => "Offline");
        }

        finally
        {
            disposables.Dispose();
        }
    }
}
