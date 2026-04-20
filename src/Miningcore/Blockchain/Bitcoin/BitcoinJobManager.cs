using Autofac;
using Miningcore.Blockchain.Bitcoin.AuxPow;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NBitcoin;
using Miningcore.Mining;
using Org.BouncyCastle.Crypto.Parameters;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJobManager : BitcoinJobManagerBase<BitcoinJob>
{
    public BitcoinJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private BitcoinTemplate coin;
    private Dictionary<string, RpcClient> auxRpcs;
    private string lastAuxWorkId;

    protected override void ConfigureDaemons()
    {
        base.ConfigureDaemons();

        // Optional AuxPoW daemons: add additional daemon entries with { "category": "aux", "coin": "dogecoin" }
        var auxDaemonEndpoints = poolConfig.Daemons
            .Where(x => string.Equals(x.Category, "aux", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (auxDaemonEndpoints.Length == 0)
            return;

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

        auxRpcs = auxDaemonEndpoints
            .Select(x =>
            {
                var coinId = x.Extra != null && x.Extra.TryGetValue("coin", out var val) ? val?.ToString() : null;
                return (coinId, endpoint: x);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.coinId))
            .GroupBy(x => x.coinId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new RpcClient(g.First().endpoint, jsonSerializerSettings, messageBus, poolConfig.Id),
                StringComparer.OrdinalIgnoreCase);

        if (auxRpcs.Count == 0)
            logger.Warn(() => "AuxPoW daemons configured but none had an extra field 'coin'. Example: { category: 'aux', coin: 'dogecoin' }");
        else
            logger.Info(() => $"AuxPoW merge-mining enabled for: {string.Join(", ", auxRpcs.Keys)}");
    }

    protected override object[] GetBlockTemplateParams()
    {
        var result = base.GetBlockTemplateParams();

        if (coin.HasMWEB)
        {
            result = new object[]
            {
                new
                {
                    rules = new[] {"segwit", "mweb"},
                }
            };
        }

        if (coin.BlockTemplateRpcExtraParams != null)
        {
            if (coin.BlockTemplateRpcExtraParams.Type == JTokenType.Array)
                result = result.Concat(coin.BlockTemplateRpcExtraParams.ToObject<object[]>() ?? Array.Empty<object>()).ToArray();
            else
                result = result.Concat(new[] { coin.BlockTemplateRpcExtraParams.ToObject<object>() }).ToArray();
        }

        return result;
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var response = await rpc.ExecuteAsync<BlockTemplate>(logger,
                BitcoinCommands.GetBlockTemplate, ct, GetBlockTemplateParams());

            var isSynched = response.Error == null;

            if (isSynched)
            {
                logger.Info(() => "All daemons synched with blockchain");
                break;
            }
            else
            {
                logger.Debug(() => $"Daemon reports error: {response.Error?.Message}");
            }

            if (!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    protected async Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        var result = await rpc.ExecuteAsync<BlockTemplate>(logger,
            BitcoinCommands.GetBlockTemplate, ct, extraPoolConfig?.GBTArgs ?? (object)GetBlockTemplateParams());

        return result;
    }

    protected RpcResponse<BlockTemplate> GetBlockTemplateFromJson(string json)
    {
        var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

        return new RpcResponse<BlockTemplate>(result!.ResultAs<BlockTemplate>());
    }

    private BitcoinJob CreateJob()
    {
        return new();
    }

    protected override void PostChainIdentifyConfigure()
    {
        base.PostChainIdentifyConfigure();

        if (poolConfig.EnableInternalStratum == true && coin.HeaderHasherValue is IHashAlgorithmInit hashInit)
        {
            if (!hashInit.DigestInit(poolConfig))
                logger.Error(() => $"{hashInit.GetType().Name} initialization failed");
        }
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if (forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            // may happen if daemon is currently not connected to peers
            if (response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            // fetch aux work (AuxPoW)
            AuxPowJobData auxPowData = null;
            var auxWorkId = (string)null;

            if (auxRpcs?.Count > 0)
            {
                auxPowData = await GetAuxPowJobDataAsync(ct);
                auxWorkId = auxPowData != null ?
                    string.Join("|", auxPowData.Chains
                        .OrderBy(x => x.CoinId, StringComparer.OrdinalIgnoreCase)
                        .Select(x => $"{x.CoinId}:{x.AuxBlockHash}")) :
                    null;

                // If aux work changes, we must rebuild the parent coinbase -> new job.
                if (auxWorkId != lastAuxWorkId)
                    forceUpdate = true;
            }

            var isNew = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                        blockTemplate.Height > job.BlockTemplate?.Height));

            if (isNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if (isNew || forceUpdate)
            {
                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue,
                    auxPowData);

                lastAuxWorkId = auxWorkId;

                lock (jobLock)
                {
                    validJobs.Insert(0, job);

                    // trim active jobs
                    while (validJobs.Count > maxActiveJobs)
                        validJobs.RemoveAt(validJobs.Count - 1);
                }

                if (isNew)
                {
                    if (via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if (via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;
            }

            return (isNew, forceUpdate);
        }

        catch (OperationCanceledException)
        {
            // ignored
        }

        catch (Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    private async Task<AuxPowJobData> GetAuxPowJobDataAsync(CancellationToken ct)
    {
        // Deterministic merkle nonce so jobs don't churn when aux hashes are unchanged.
        const uint baseNonce = 0;

        var chainWork = new List<(string CoinId, string AuxHashHex, uint ChainId, uint256 TargetValue)>();

        foreach (var (coinId, rpcClient) in auxRpcs)
        {
            var response = await rpcClient.ExecuteAsync<GetAuxBlockResponse>(logger, BitcoinCommands.GetAuxBlock, ct);

            if (response.Error != null)
                throw new PoolStartupException($"AuxPoW daemon for '{coinId}' reports: {response.Error.Message}", poolConfig.Id);

            var work = response.Response;
            if (work == null || string.IsNullOrEmpty(work.Hash))
                throw new PoolStartupException($"AuxPoW daemon for '{coinId}' returned empty getauxblock response", poolConfig.Id);

            if (!work.ChainId.HasValue)
                throw new PoolStartupException($"AuxPoW daemon for '{coinId}' did not return chainid in getauxblock response", poolConfig.Id);

            uint256 targetValue;
            if (!string.IsNullOrEmpty(work.Target))
                targetValue = new uint256(work.Target);
            else if (!string.IsNullOrEmpty(work.Bits))
                targetValue = new Target(work.Bits.HexToByteArray()).ToUInt256();
            else
                throw new PoolStartupException($"AuxPoW daemon for '{coinId}' returned neither target nor bits", poolConfig.Id);

            chainWork.Add((coinId, work.Hash, work.ChainId.Value, targetValue));
        }

        if (chainWork.Count == 0)
            return null;

        return AuxPowUtils.BuildAuxPowJobData(chainWork, baseNonce);
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        if (extraPoolConfig?.MaxActiveJobs.HasValue == true)
            maxActiveJobs = extraPoolConfig.MaxActiveJobs.Value;

        hasLegacyDaemon = extraPoolConfig?.HasLegacyDaemon == true;

        base.Configure(pc, cc);
    }

    public virtual object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            BitcoinConstants.ExtranoncePlaceHolderLength - ExtranonceBytes,
        };

        return responseData;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if (submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // extract params
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;
        var versionBits = context.VersionRollingMask.HasValue ? submitParams[5] as string : null;

        if (string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        BitcoinJob job;

        lock (jobLock)
        {
            job = validJobs.FirstOrDefault(x => x.JobId == jobId);
        }

        if (job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var (share, blockHex, auxPowSubmissions) = job.ProcessShare(worker, extraNonce2, nTime, nonce, versionBits);

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        // if block candidate, submit & check if accepted by network
        if (share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

            var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted;

            if (share.IsBlockCandidate)
            {
                logger.Info(() => $"Daemon accepted block {share.BlockHeight} block [{share.BlockHash}] submitted by {context.Miner}");

                OnBlockFound();

                // persist the coinbase transaction-hash to allow the payment processor
                // to verify later on that the pool has received the reward for the block
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;
            }
        }

        // submit aux blocks (AuxPoW merged mining)
        if (auxPowSubmissions is { Count: > 0 })
        {
            foreach (var aux in auxPowSubmissions)
            {
                if (auxRpcs != null && auxRpcs.TryGetValue(aux.CoinId, out var auxRpc))
                {
                    var accepted = await SubmitAuxBlockAsync(auxRpc, aux, ct);
                    if (accepted)
                        logger.Info(() => $"AuxPoW accepted by {aux.CoinId}: {aux.AuxBlockHash}");
                }
            }
        }

        return share;
    }

    private async Task<bool> SubmitAuxBlockAsync(RpcClient auxRpc, AuxPowSubmission submission, CancellationToken ct)
    {
        try
        {
            // Common pattern: getauxblock <hash> <auxpow>
            var payload = new object[] { submission.AuxBlockHash, submission.AuxPowHex };

            var response = await auxRpc.ExecuteAsync<JToken>(logger, BitcoinCommands.GetAuxBlock, ct, payload);

            if (response.Error != null)
            {
                // Some daemons use submitauxblock instead
                var fallback = await auxRpc.ExecuteAsync<JToken>(logger, BitcoinCommands.SubmitAuxBlock, ct, payload);

                if (fallback.Error != null)
                {
                    logger.Warn(() => $"AuxPoW submission to {submission.CoinId} failed: {response.Error.Message}");
                    logger.Warn(() => $"AuxPoW submission (fallback submitauxblock) to {submission.CoinId} failed: {fallback.Error.Message}");
                    return false;
                }

                response = fallback;
            }

            // Some daemons return boolean, others a string like "accepted".
            var token = response.Response;
            if (token == null)
                return false;

            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();

            if (token.Type == JTokenType.String)
                return token.Value<string>()?.IndexOf("accept", StringComparison.OrdinalIgnoreCase) >= 0;

            return true;
        }
        catch (Exception ex)
        {
            logger.Warn(ex, () => $"AuxPoW submission to {submission.CoinId} errored");
            return false;
        }
    }

    public double ShareMultiplier => coin.ShareMultiplier;

    #endregion // API-Surface
}
