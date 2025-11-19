using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.RegularExpressions;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Hoosat.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Hoosat;

[CoinFamily(CoinFamily.Hoosat)]
public class HoosatPool : PoolBase
{
    public HoosatPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    protected object[] currentJobParams;
    protected HoosatJobManager manager;
    private HoosatPoolConfigExtra extraPoolConfig;
    private HoosatCoinTemplate coin;

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<HoosatWorkerContext>();

        if (request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        // setup worker context
        var requestParams = request.ParamsAs<string[]>();
        context.UserAgent = requestParams.FirstOrDefault()?.Trim();
        context.IsLargeJob = manager.ValidateIsLargeJob(context.UserAgent);

        //Ban if using an iceriver with algo other then kHeavyHash
        string userAgentBan = requestParams.FirstOrDefault()?.Trim();
        string banPattern = ".*iceriver*.";
        string coinAlgoName = coin.GetAlgorithmName();
        TimeSpan IceRiverBanTimeout = TimeSpan.FromSeconds(600);
        //if (Regex.IsMatch(userAgentBan, banPattern) && !string.Equals(coinAlgoName, "kHeavyHash", StringComparison.OrdinalIgnoreCase))       {
        if (Regex.IsMatch(userAgentBan, banPattern, RegexOptions.IgnoreCase) && !string.Equals(coinAlgoName, "Hoohash", StringComparison.OrdinalIgnoreCase))
        {
            // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
            logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized useragent {userAgentBan} for {IceRiverBanTimeout.TotalSeconds} sec");

            banManager.Ban(connection.RemoteEndpoint.Address, IceRiverBanTimeout);

            Disconnect(connection);
            return; // Exit from the method if iceriver
        }

        if (manager.ValidateIsGodMiner(context.UserAgent))
        {
            var data = new object[]
            {
                null,
            }
            .Concat(manager.GetSubscriberData(connection))
            .ToArray();

            await connection.RespondAsync(data, request.Id);
        }

        else
        {
            var data = new object[]
            {
                true,
                "HoosatStratum/1.0.0",
            };

            await connection.RespondAsync(data, request.Id);
            await connection.NotifyAsync(HoosatStratumMethods.SetExtraNonce, manager.GetSubscriberData(connection));
        }

        context.IsSubscribed = true;
    }

    protected virtual async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        if (request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<HoosatWorkerContext>();

        if (!context.IsSubscribed)
            throw new StratumException(StratumError.NotSubscribed, "subscribe first please, we aren't savages");

        var requestParams = request.ParamsAs<string[]>();

        // setup worker context
        context.IsSubscribed = true;

        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var passParts = password?.Split(PasswordControlVarsSeparator);

        // extract worker/miner
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // assumes that minerName is an address
        var (HoosatAddressUtility, errorHoosatAddressUtility) = HoosatUtils.ValidateAddress(minerName, manager.Network, coin.Symbol);
        if (errorHoosatAddressUtility != null)
            logger.Warn(() => $"[{connection.ConnectionId}] Unauthorized worker: {errorHoosatAddressUtility}");
        else
        {
            context.IsAuthorized = true;
            logger.Info(() => $"[{connection.ConnectionId}] worker: {minerName} => {HoosatConstants.HoosatAddressType[HoosatAddressUtility.HoosatAddress.Version()]}");
        }

        context.Miner = minerName;
        context.Worker = workerName;

        if (context.IsAuthorized)
        {
            // respond
            await connection.RespondAsync(context.IsAuthorized, request.Id);

            // log association
            logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {workerValue}");

            // extract control vars from password
            var staticDiff = GetStaticDiffFromPassparts(passParts);

            // Nicehash support
            var nicehashDiff = await GetNicehashStaticMinDiff(context, coin.Name, coin.GetAlgorithmName());

            if (nicehashDiff.HasValue)
            {
                if (!staticDiff.HasValue || nicehashDiff > staticDiff)
                {
                    logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

                    staticDiff = nicehashDiff;
                }

                else
                    logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using miner supplied difficulty of {staticDiff.Value}");
            }

            // Static diff
            if (staticDiff.HasValue &&
               (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
            {
                context.VarDiff = null; // disable vardiff
                context.SetDifficulty(staticDiff.Value);

                logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");
            }

            // send intial job
            await SendJob(connection, context, currentJobParams);
        }

        else
        {
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

            if (clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
                logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);

                Disconnect(connection);
            }
        }
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<HoosatWorkerContext>();

        try
        {
            if (request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            // check age of submission (aged submissions are usually caused by high server load)
            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if (requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            // check worker state
            context.LastActivity = clock.Now;

            if (!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "Unauthorized worker");
            else if (!context.IsSubscribed)
                throw new StratumException(StratumError.NotSubscribed, "Not subscribed");

            var requestParams = request.ParamsAs<string[]>();

            // submit
            var share = await manager.SubmitShareAsync(connection, requestParams, ct);
            await connection.RespondAsync(true, request.Id);

            // publish
            messageBus.SendMessage(share);

            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty * HoosatConstants.ShareMultiplier, 3)}");

            // update pool stats
            if (share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            // update client stats
            context.Stats.ValidShares++;

            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch (StratumException ex)
        {
            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // update client stats
            context.Stats.InvalidShares++;

            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

            // banning
            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    protected virtual async Task OnNewJobAsync(object[] jobParams)
    {
        currentJobParams = jobParams;

        logger.Info(() => $"Broadcasting job {jobParams[0]}");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<HoosatWorkerContext>();

            await SendJob(connection, context, currentJobParams);
        }));
    }

    private async Task SendJob(StratumConnection connection, HoosatWorkerContext context, object[] jobParams)
    {
        object[] jobParamsActual;
        if (context.IsLargeJob)
        {
            jobParamsActual = new object[] {
                jobParams[0],
                jobParams[1],
            };
        }
        else
        {
            jobParamsActual = new object[] {
                jobParams[0],
                jobParams[2],
                jobParams[3],
            };
        }

        // send difficulty
        await connection.NotifyAsync(HoosatStratumMethods.SetDifficulty, new object[] { context.Difficulty });

        // send job
        await connection.NotifyAsync(HoosatStratumMethods.MiningNotify, jobParamsActual);
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        var multiplier = HoosatConstants.Pow2xDiff1TargetNumZero * (double)HoosatConstants.MinHash;
        var result = shares * multiplier / interval;

        return result;
    }

    public override double ShareMultiplier => HoosatConstants.ShareMultiplier;

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<HoosatCoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<HoosatPoolConfigExtra>();

        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        var extraNonce1Size = extraPoolConfig?.ExtraNonce1Size ?? 2;

        manager = ctx.Resolve<HoosatJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new HoosatExtraNonceProvider(poolConfig.Id, extraNonce1Size, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if (poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(() => OnNewJobAsync(job),
                        ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            await manager.Jobs.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new HoosatWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        try
        {
            switch (request.Method)
            {
                case HoosatStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest, ct);
                    break;

                case HoosatStratumMethods.ExtraNonceSubscribe:
                    var context = connection.ContextAs<HoosatWorkerContext>();

                    var data = new object[]
                    {
                        context.ExtraNonce1,
                        HoosatConstants.ExtranoncePlaceHolderLength - manager.GetExtraNonce1Size(),
                    };

                    await connection.NotifyAsync(HoosatStratumMethods.SetExtraNonce, data);
                    break;

                case HoosatStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case HoosatStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch (StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    protected override async Task<double?> GetNicehashStaticMinDiff(WorkerContextBase context, string coinName, string algoName)
    {
        var result = await base.GetNicehashStaticMinDiff(context, coinName, algoName);

        if (result.HasValue)
            result = result.Value / uint.MaxValue;

        return result;
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        var context = connection.ContextAs<HoosatWorkerContext>();

        if (context.ApplyPendingDifficulty())
        {
            await SendJob(connection, context, currentJobParams);
        }
    }

    #endregion
}
