using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using Autofac;
using AutoMapper;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Hoosat.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using htnWalletd = Miningcore.Blockchain.Hoosat.HtnWalletd;
using htnd = Miningcore.Blockchain.Hoosat.Htnd;
using Miningcore.Persistence.Postgres.Repositories;

namespace Miningcore.Blockchain.Hoosat;

[CoinFamily(CoinFamily.Hoosat)]
public class HoosatPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public HoosatPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
        this.connectionFactory = cf;
    }

    protected readonly IComponentContext ctx;
    protected readonly IConnectionFactory connectionFactory;
    protected htnd.HtndRPC.HtndRPCClient rpc;
    protected htnWalletd.HtnWalletdRPC.HtnWalletdRPCClient walletRpc;
    private string network;
    private HoosatPoolConfigExtra extraPoolConfig;
    private HoosatPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    private const int BlockFetchMaxAttempts = 5;
    private static readonly TimeSpan BlockFetchRetryDelay = TimeSpan.FromSeconds(2);

    protected override string LogCategory => "Hoosat Payout Handler";

    private class BlockFetchResult
    {
        public htnd.GetBlockResponseMessage BlockInfo { get; init; }
        public bool IsRetryable { get; init; }
    }

    #region IPayoutHandler

    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<HoosatPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<HoosatPaymentProcessingConfigExtra>();

        logger = LogUtil.GetPoolScopedLogger(typeof(HoosatPayoutHandler), pc);

        // extract standard daemon endpoints
        var daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        // extract wallet daemon endpoints
        var walletDaemonEndpoints = pc.Daemons
            .Where(x => x.Category?.ToLower() == HoosatConstants.WalletDaemonCategory)
            .ToArray();

        if (walletDaemonEndpoints.Length == 0)
            throw new PaymentException("Wallet-RPC daemon is not configured (Daemon configuration for Hoosat-pools require an additional entry of category 'wallet' pointing to the wallet daemon)");

        rpc = HoosatClientFactory.CreateHtndRPCClient(daemonEndpoints, extraPoolConfig?.ProtobufDaemonRpcServiceName ?? HoosatConstants.ProtobufDaemonRpcServiceName);
        walletRpc = HoosatClientFactory.CreateHtnWalletdRPCClient(walletDaemonEndpoints, extraPoolConfig?.ProtobufWalletRpcServiceName ?? HoosatConstants.ProtobufWalletRpcServiceName);

        // we need a stream to communicate with Hoosatd
        var stream = rpc.MessageStream(null, null, ct);

        var request = new htnd.HtndMessage();
        request.GetCurrentNetworkRequest = new htnd.GetCurrentNetworkRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex => throw new PaymentException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}"));
        await foreach (var currentNetwork in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (!string.IsNullOrEmpty(currentNetwork.GetCurrentNetworkResponse.Error?.Message))
                throw new PaymentException($"Daemon reports: {currentNetwork.GetCurrentNetworkResponse.Error?.Message}");

            network = currentNetwork.GetCurrentNetworkResponse.CurrentNetwork;
            break;
        }
        await stream.RequestStream.CompleteAsync();
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if (blocks.Length == 0)
            return blocks;

        var coin = poolConfig.Template.As<HoosatCoinTemplate>();
        var pageSize = 100;
        var pageCount = (int)Math.Ceiling(blocks.Length / (double)pageSize);
        var result = new List<Block>();
        var blockInfoCache = new ConcurrentDictionary<string, Task<BlockFetchResult>>();

        int minConfirmations = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? (network == "mainnet" ? 120 : 110);
        for (var i = 0; i < pageCount; i++)
        {
            logger.Debug(() => $"[{LogCategory}] Processing page {i + 1}/{pageCount}");
            var page = blocks.Skip(i * pageSize).Take(pageSize).ToArray();

            foreach (var block in page)
            {
                logger.Debug(() => $"[{LogCategory}] Checking block {block.BlockHeight}");

                var blockInfoResult = await GetBlockInfoCachedAsync(block.Hash, ct, blockInfoCache);

                if (blockInfoResult.IsRetryable)
                {
                    logger.Warn(() => $"[{LogCategory}] Deferring block {block.BlockHeight} because block data is temporarily unavailable");
                    result.Add(block);
                    continue;
                }

                if (blockInfoResult.BlockInfo == null)
                {
                    await MarkBlockAsOrphaned(block, poolConfig.Id, coin, blockRepo);
                    continue;
                }

                var blockInfo = blockInfoResult.BlockInfo;

                block.ConfirmationProgress = await GetBlockConfirmations(block.Hash, minConfirmations, ct);

                result.Add(block);
                messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                logger.Debug(() => $"block.ConfirmationProgress height : {block.BlockHeight} / confirmation progess : {block.ConfirmationProgress}");

                if (block.ConfirmationProgress < 1)
                {
                    logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} NOT confirmed yet ({block.ConfirmationProgress * 100}%)");
                    continue;
                }

                var blockReward = await ProcessChildrenAndRedBlocks(block, blockInfo, poolConfig, ct, blockInfoCache);

                if (!blockReward.HasValue)
                {
                    logger.Warn(() => $"[{LogCategory}] Deferring block {block.BlockHeight} because reward data is temporarily unavailable");
                    continue;
                }

                if (blockReward.Value > 0)
                {
                    await FinalizeBlockProcessing(block, poolConfig.Id, coin, blockRepo, blockReward.Value);
                }
                else
                {
                    await MarkBlockAsOrphaned(block, poolConfig.Id, coin, blockRepo);
                }
            }
        }

        return result.ToArray();
    }

    private async Task<decimal?> ProcessChildrenAndRedBlocks(Block block, htnd.GetBlockResponseMessage blockInfo,
        PoolConfig poolConfig, CancellationToken ct, ConcurrentDictionary<string, Task<BlockFetchResult>> blockInfoCache)
    {
        logger.Debug(() => $"[{LogCategory}] Calculating reward for block {block.BlockHeight}");

        var totalReward = 0m;

        var childInfos = await Task.WhenAll(blockInfo.Block.VerboseData.ChildrenHashes.Select(async childHash =>
        {
            logger.Debug(() => $"[{LogCategory}] Checking child {childHash}");

            var childInfoResult = await GetBlockInfoCachedAsync(childHash, ct, blockInfoCache);

            return new
            {
                ChildHash = childHash,
                Result = childInfoResult
            };
        }));

        foreach (var childInfoEntry in childInfos)
        {
            var childHash = childInfoEntry.ChildHash;
            var childInfoResult = childInfoEntry.Result;

            if (childInfoResult.IsRetryable || childInfoResult.BlockInfo == null)
            {
                logger.Warn(() => $"[{LogCategory}] Deferring reward evaluation for block {block.BlockHeight} because child {childHash} data is unavailable");
                return null;
            }

            var childInfo = childInfoResult.BlockInfo;

            if (!childInfo.Block.VerboseData.IsChainBlock)
            {
                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - child {childInfo.Block.Header.DaaScore} [{childHash}] provides {FormatAmount(0.0m)}");
                continue;
            }

            if (childInfo.Block.VerboseData.MergeSetRedsHashes.Contains(block.Hash))
            {
                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - child {childInfo.Block.Header.DaaScore} [{childHash}] provides {FormatAmount(0.0m)} because block is in merge-set reds");
                continue;
            }

            var mergeSetBlueIndex = childInfo.Block.VerboseData.MergeSetBluesHashes.IndexOf(block.Hash);

            if (mergeSetBlueIndex < 0)
            {
                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - child {childInfo.Block.Header.DaaScore} [{childHash}] provides {FormatAmount(0.0m)}");
                continue;
            }

            var childrenPosition = mergeSetBlueIndex * 2;

            var coinbaseTransaction = childInfo.Block.Transactions
                .Where(tx => tx.Inputs.Count == 0)
                .OrderByDescending(tx => tx.Outputs.Count)
                .FirstOrDefault(tx => tx.Outputs.Count > childrenPosition);

            if (coinbaseTransaction == null)
            {
                logger.Warn(() => $"[{LogCategory}] No reward coinbase transaction found in child {childHash}");
                continue;
            }

            if (childrenPosition >= coinbaseTransaction.Outputs.Count)
            {
                logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} - child {childInfo.Block.Header.DaaScore} [{childHash}] has no reward output at merge-set index {childrenPosition}");
                continue;
            }

            var rewardOutput = coinbaseTransaction.Outputs[childrenPosition];

            if (rewardOutput.VerboseData.ScriptPublicKeyAddress == poolConfig.Address)
            {

                var childReward = (decimal)(rewardOutput.Amount / HoosatConstants.SmallestUnit);
                totalReward += childReward;

                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - child {childInfo.Block.Header.DaaScore} [{childHash}] provides {FormatAmount(childReward)} => {poolConfig.Template.As<HoosatCoinTemplate>().Symbol} address: {rewardOutput.VerboseData.ScriptPublicKeyAddress} [{poolConfig.Address}]");
            }
            else
            {
                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - child {childInfo.Block.Header.DaaScore} [{childHash}] provides {FormatAmount(0.0m)}");
            }
        }

        var directRedReward = GetPoolChainBlockReward(blockInfo, poolConfig.Address);

        if (directRedReward > 0)
        {
            totalReward += directRedReward;
            logger.Info(() => $"[{LogCategory}] Chain block {block.BlockHeight} rewards itself with {FormatAmount(directRedReward)}");
        }

        if (totalReward > 0)
            return totalReward;

        logger.Warn(() => $"[{LogCategory}] No rewards found for block {block.BlockHeight}");

        return 0.0m;
    }


    private async Task<BlockFetchResult> GetBlockInfo(string blockHash, CancellationToken ct)
    {
        logger.Debug(() => $"[{LogCategory}] Fetching block info for {blockHash}");

        for (var attempt = 1; attempt <= BlockFetchMaxAttempts; attempt++)
        {
            try
            {
                using var stream = rpc.MessageStream(null, null, ct);
                var request = new htnd.GetBlockRequestMessage { Hash = blockHash, IncludeTransactions = true };

                await Guard(() => stream.RequestStream.WriteAsync(new htnd.HtndMessage { GetBlockRequest = request }), ex => throw ex);

                while (await stream.ResponseStream.MoveNext(ct))
                {
                    var response = stream.ResponseStream.Current;

                    if (response?.GetBlockResponse == null)
                        continue;

                    if (!string.IsNullOrEmpty(response.GetBlockResponse.Error?.Message))
                    {
                        await Guard(() => stream.RequestStream.CompleteAsync(), ex => throw ex);
                        logger.Warn(() => $"[{LogCategory}] Daemon returned an error for block {blockHash}: {response.GetBlockResponse.Error.Message}");
                        return new BlockFetchResult();
                    }

                    if (response.GetBlockResponse.Block == null)
                    {
                        await Guard(() => stream.RequestStream.CompleteAsync(), ex => throw ex);
                        logger.Warn(() => $"[{LogCategory}] Block {blockHash} not found or invalid response.");
                        return new BlockFetchResult();
                    }

                    await Guard(() => stream.RequestStream.CompleteAsync(), ex => throw ex);
                    return new BlockFetchResult { BlockInfo = response.GetBlockResponse };
                }

                await Guard(() => stream.RequestStream.CompleteAsync(), ex => throw ex);

                if (attempt < BlockFetchMaxAttempts)
                {
                    logger.Warn(() => $"[{LogCategory}] No response received for block {blockHash} (attempt {attempt}/{BlockFetchMaxAttempts}), retrying in {BlockFetchRetryDelay.TotalSeconds:0}s");
                    await Task.Delay(BlockFetchRetryDelay, ct);
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt < BlockFetchMaxAttempts)
                {
                    logger.Warn(() => $"[{LogCategory}] Error fetching block {blockHash} (attempt {attempt}/{BlockFetchMaxAttempts}): {ex.Message}. Retrying in {BlockFetchRetryDelay.TotalSeconds:0}s");
                    await Task.Delay(BlockFetchRetryDelay, ct);
                    continue;
                }

                logger.Warn(() => $"[{LogCategory}] Error fetching block {blockHash} after {BlockFetchMaxAttempts} attempts: {ex.Message}");
            }
        }

        logger.Warn(() => $"[{LogCategory}] Block {blockHash} is temporarily unavailable after {BlockFetchMaxAttempts} attempts");

        return new BlockFetchResult { IsRetryable = true };
    }

    private async Task<BlockFetchResult> GetBlockInfoCachedAsync(string blockHash, CancellationToken ct,
        ConcurrentDictionary<string, Task<BlockFetchResult>> blockInfoCache)
    {
        var blockInfoTask = blockInfoCache.GetOrAdd(blockHash, _ => GetBlockInfo(blockHash, ct));
        var blockInfoResult = await blockInfoTask;

        if (blockInfoResult.IsRetryable)
            blockInfoCache.TryRemove(blockHash, out _);

        return blockInfoResult;
    }

    private async Task<double> GetBlockConfirmations(string blockHash, int minConfirmations, CancellationToken ct)
    {
        logger.Debug(() => $"[{LogCategory}] Checking confirmations for block {blockHash}");

        using var stream = rpc.MessageStream(null, null, ct);
        var request = new htnd.GetBlocksRequestMessage { LowHash = blockHash, IncludeBlocks = false, IncludeTransactions = false };
        await Guard(() => stream.RequestStream.WriteAsync(new htnd.HtndMessage { GetBlocksRequest = request }), ex => logger.Debug(ex));

        while (await stream.ResponseStream.MoveNext(ct))
        {
            var response = stream.ResponseStream.Current;

            if (response?.GetBlocksResponse == null)
                continue;

            if (!string.IsNullOrEmpty(response.GetBlocksResponse.Error?.Message))
            {
                await Guard(() => stream.RequestStream.CompleteAsync(), ex => logger.Debug(ex));
                logger.Warn(() => $"[{LogCategory}] Daemon returned a confirmation error for block {blockHash}: {response.GetBlocksResponse.Error.Message}");
                return 0.0d;
            }

            await Guard(() => stream.RequestStream.CompleteAsync(), ex => logger.Debug(ex));
            return Math.Min(1.0d, (double)response.GetBlocksResponse.BlockHashes.Count / minConfirmations);
        }

        await Guard(() => stream.RequestStream.CompleteAsync(), ex => logger.Debug(ex));

        return 0.0d;
    }

    private async Task MarkBlockAsOrphaned(Block block, string poolId, CoinTemplate coin, IBlockRepository blockRepo)
    {
        logger.Warn(() => $"[{LogCategory}] Marking block {block.BlockHeight} as orphaned");

        block.Status = BlockStatus.Orphaned;
        block.Reward = 0;
        block.ConfirmationProgress = 1;
        using var con = await connectionFactory.OpenConnectionAsync();
        await blockRepo.UpdateBlockStatusAndRewardAsync(con, poolId, (long)block.BlockHeight, block.Hash, BlockStatus.Orphaned, 0);
        messageBus.NotifyBlockUnlocked(poolId, block, coin);
    }

    private decimal GetPoolChainBlockReward(htnd.GetBlockResponseMessage blockInfo, string poolAddress)
    {
        if (!blockInfo.Block.VerboseData.IsChainBlock)
            return 0.0m;

        if (blockInfo.Block.Transactions == null || blockInfo.Block.Transactions.Count == 0)
            return 0.0m;

        var coinbaseTransaction = blockInfo.Block.Transactions
            .Where(tx => tx.Inputs.Count == 0)
            .OrderByDescending(tx => tx.Outputs.Count)
            .FirstOrDefault(tx => tx.Outputs.Count > (blockInfo.Block.VerboseData.MergeSetBluesHashes.Count * 2));

        if (coinbaseTransaction == null)
        {
            logger.Warn(() => $"[{LogCategory}] No self-reward coinbase transaction found in block {blockInfo.Block.VerboseData.Hash}");
            return 0.0m;
        }

        var blueRewardOutputs = blockInfo.Block.VerboseData.MergeSetBluesHashes.Count * 2;

        if (coinbaseTransaction.Outputs.Count <= blueRewardOutputs)
            return 0.0m;

        var rewardOutput = coinbaseTransaction.Outputs[blueRewardOutputs];

        if (rewardOutput.VerboseData.ScriptPublicKeyAddress != poolAddress)
            return 0.0m;

        decimal reward = (decimal)(rewardOutput.Amount / HoosatConstants.SmallestUnit);

        logger.Info(() => $"[{LogCategory}] Chain block reward found in coinbase of block {blockInfo.Block.VerboseData.Hash}: {FormatAmount(reward)} to pool {poolAddress}");

        return reward;
    }

    private async Task FinalizeBlockProcessing(Block block, string poolId, CoinTemplate coin, IBlockRepository blockRepo, decimal blockReward)
    {
        logger.Info(() => $"[{LogCategory}] Finalizing block {block.BlockHeight} with reward {FormatAmount(blockReward)}");

        block.Reward = blockReward;
        block.Status = BlockStatus.Confirmed;
        block.ConfirmationProgress = 1;

        using var con = await connectionFactory.OpenConnectionAsync();
        await blockRepo.UpdateBlockStatusAndRewardAsync(con, poolId, (long)block.BlockHeight, block.Hash, BlockStatus.Confirmed, blockReward);

        messageBus.NotifyBlockUnlocked(poolId, block, coin);
    }


    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        logger.Info(() => $"Base ---- PayoutAsync - PayoutAsync starting...");

        Contract.RequiresNonNull(balances);

        // build args
        var balanceMap = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => x);

        if (balanceMap.Count == 0)
            return;

        var balancesTotal = balanceMap.Values.Sum(x => x.Amount);

        logger.Info(() => $"Base --- PayoutAsync - Initiating payout of {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        logger.Info(() => $"Base - Validating addresses...");
        var coin = poolConfig.Template.As<HoosatCoinTemplate>();
        foreach (var pair in balanceMap)
        {
            logger.Debug(() => $"Base --- PayoutAsync - Address {pair.Key} with amount [{FormatAmount(pair.Value.Amount)}]");
            var (hoosatAddressUtility, errorHoosatAddressUtility) = HoosatUtils.ValidateAddress(pair.Key, network, coin.Symbol);

            if (errorHoosatAddressUtility != null)
                logger.Warn(() => $"Base --- PayoutAsync - Address {pair.Key} is not valid : {errorHoosatAddressUtility}");
        }

        var callGetBalance = walletRpc.GetBalanceAsync(new htnWalletd.GetBalanceRequest());
        var walletBalances = await Guard(() => callGetBalance.ResponseAsync,
            ex => logger.Debug(ex));
        callGetBalance.Dispose();

        var walletBalancePending = (decimal)(walletBalances?.Pending == null ? 0 : walletBalances?.Pending) / HoosatConstants.SmallestUnit;
        var walletBalanceAvailable = (decimal)(walletBalances?.Available == null ? 0 : walletBalances?.Available) / HoosatConstants.SmallestUnit;

        logger.Info(() => $"Base --- PayoutAsync - Current wallet balance - Total: [{FormatAmount(walletBalancePending + walletBalanceAvailable)}] - Pending: [{FormatAmount(walletBalancePending)}] - Available: [{FormatAmount(walletBalanceAvailable)}]");

        if (walletBalanceAvailable < balancesTotal)
        {
            logger.Warn(() => $"Base --- PayoutAsync - Wallet balance currently short of {FormatAmount(balancesTotal - walletBalanceAvailable)}. Will try again");
            return;
        }

        var txFailures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
        var successBalances = new Dictionary<Balance, string>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = extraPoolPaymentProcessingConfig?.MaxDegreeOfParallelPayouts ?? 2,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(balanceMap.Values, parallelOptions, async (balance, _ct) =>
        {
            var address = balance.Address;
            var amount = balance.Amount;
            int maxAttempts = 5;
            int attempt = 0;
            bool success = false;
            Exception lastException = null;
            string txId = null;

            while (attempt < maxAttempts && !success)
            {
                attempt++;
                var transferId = CorrelationIdGenerator.GetNextId();
                try
                {
                    logger.Info(() => $"Base --- PayoutAsync - [{transferId}] Sending {FormatAmount(amount)} to {address} (attempt {attempt})");

                    var callSend = walletRpc.SendAsync(new htnWalletd.SendRequest
                    {
                        ToAddress = address.ToLower(),
                        Amount = (ulong)(amount * HoosatConstants.SmallestUnit),
                        Password = extraPoolPaymentProcessingConfig?.WalletPassword ?? null,
                        UseExistingChangeAddress = true,
                        IsSendAll = false,
                    });
                    var sendTransaction = await Guard(() => callSend.ResponseAsync,
                        ex => throw new PaymentException($"[{transferId}] htnWalletd returned error: {ex}"));
                    callSend.Dispose();

                    txId = sendTransaction.TxIDs.First();
                    if (string.IsNullOrEmpty(txId))
                        throw new Exception($"[{transferId}] htnWalletd did not return a transaction id!");

                    logger.Info(() => $"Base --- PayoutAsync - [{transferId}] Payment transaction id: {txId}");

                    lock (successBalances)
                    {
                        successBalances.Add(balance, txId);
                    }
                    success = true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    logger.Warn(() => $"Base --- PayoutAsync - Failed to send {FormatAmount(amount)} to {address} (attempt {attempt}): {ex.Message}");
                    if (attempt < maxAttempts)
                    {
                        logger.Info(() => $"Base --- PayoutAsync - Waiting 10 seconds before retrying payment to {address}...");
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                }
            }

            if (!success)
            {
                lock (txFailures)
                {
                    txFailures.Add(Tuple.Create(new KeyValuePair<string, decimal>(address, amount), lastException));
                }
            }
        });

        if (successBalances.Any())
        {
            await PersistPaymentsAsync(successBalances);

            NotifyPayoutSuccess(poolConfig.Id, successBalances.Keys.ToArray(), successBalances.Values.ToArray(), null);
        }

        if (txFailures.Any())
        {
            var failureBalances = txFailures.Select(x => new Balance { Amount = x.Item1.Value }).ToArray();
            var error = string.Join(", ", txFailures.Select(x => $"{x.Item1.Key} {FormatAmount(x.Item1.Value)}: {x.Item2.Message}"));

            logger.Error(() => $"Base --- PayoutAsync - Failed to transfer the following balances: {error}");

            NotifyPayoutFailure(poolConfig.Id, failureBalances, error, null);
        }
    }


    public override double AdjustShareDifficulty(double difficulty)
    {
        return difficulty * HoosatConstants.Pow2xDiff1TargetNumZero * (double)HoosatConstants.MinHash;
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort * HoosatConstants.Pow2xDiff1TargetNumZero * (double)HoosatConstants.MinHash;
    }

    #endregion

    private class PaymentException : Exception
    {
        public PaymentException(string msg) : base(msg)
        {
        }
    }
}
