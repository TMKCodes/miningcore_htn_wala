using System;
using System.Net.Http;
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

    protected override string LogCategory => "Hoosat Payout Handler";

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

        int minConfirmations = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? (network == "mainnet" ? 120 : 110);
        var stream = rpc.MessageStream(null, null, ct);

        for (var i = 0; i < pageCount; i++)
        {
            logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Processing page {i + 1}/{pageCount}");
            var page = blocks.Skip(i * pageSize).Take(pageSize).ToArray();

            foreach (var block in page)
            {
                logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Checking block {block.BlockHeight}");

                uint totalDuplicateBlockBefore = await cf.Run(con =>
                    blockRepo.GetPoolDuplicateBlockBeforeCountByPoolHeightNoTypeAndStatusAsync(con, poolConfig.Id,
                        Convert.ToInt64(block.BlockHeight), new[]
                        {
                            BlockStatus.Confirmed,
                            BlockStatus.Orphaned,
                            BlockStatus.Pending
                        }, block.Created));

                var blockInfo = await GetBlockInfo(block.Hash, stream);
                if (blockInfo == null || totalDuplicateBlockBefore > 0)
                {
                    await MarkBlockAsOrphaned(block, poolConfig.Id, coin, blockRepo);
                    continue;
                }

                block.ConfirmationProgress = await GetBlockConfirmations(block.Hash, minConfirmations, stream);

                result.Add(block);
                messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler block.ConfirmationProgress height : {block.BlockHeight} / confirmation progess : {block.ConfirmationProgress}");

                if (block.ConfirmationProgress < 1)
                {
                    logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Block {block.BlockHeight} NOT confirmed yet ({block.ConfirmationProgress * 100}%)");
                    continue;
                }

                decimal blockReward = await ProcessChildrenAndRedBlocks(block, blockInfo, stream, poolConfig);

                if (blockReward > 0)
                {
                    await FinalizeBlockProcessing(block, poolConfig.Id, coin, blockRepo, blockInfo);
                }
                else
                {
                    await MarkBlockAsOrphaned(block, poolConfig.Id, coin, blockRepo);
                }
            }
        }

        await stream.RequestStream.CompleteAsync();
        return result.ToArray();
    }

    private async Task<decimal> ProcessChildrenAndRedBlocks(Block block, htnd.GetBlockResponseMessage blockInfo,
        AsyncDuplexStreamingCall<htnd.HtndMessage, htnd.HtndMessage> stream, PoolConfig poolConfig)
    {
        logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Searching for rewards in children of block {block.BlockHeight}");

        foreach (var childHash in blockInfo.Block.VerboseData.ChildrenHashes)
        {
            logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Checking child {childHash}");

            var childInfo = await GetBlockInfo(childHash, stream);
            if (childInfo == null)
            {
                logger.Warn(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Child {childHash} not found, skipping.");
                continue;
            }

            var coinbaseTransactions = childInfo.Block.Transactions
                .Where(tx => tx.Inputs.Count == 0)
                .ToList();

            if (coinbaseTransactions.Count == 0)
            {
                logger.Warn(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] No coinbase transactions found in child {childHash}");
                continue;
            }

            foreach (var transaction in coinbaseTransactions)
            {
                logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Coinbase transaction {transaction.VerboseData.TransactionId} found in child {childHash}");

                foreach (var output in transaction.Outputs)
                {
                    string outputAddress = output.VerboseData.ScriptPublicKeyAddress;
                    decimal reward = (decimal)(output.Amount / HoosatConstants.SmallestUnit);

                    logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Checking output: Tx={transaction.VerboseData.TransactionId}, Address={outputAddress}, Amount={FormatAmount(reward)}");

                    if (outputAddress == poolConfig.Address)
                    {
                        logger.Info(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Reward found! Child {childHash} provides {FormatAmount(reward)} to pool {poolConfig.Address}");
                        return reward;
                    }
                }
            }
        }

        logger.Warn(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] No rewards found in children of block {block.BlockHeight}");
        return 0.0m;
    }


    private async Task<htnd.GetBlockResponseMessage> GetBlockInfo(string blockHash, AsyncDuplexStreamingCall<htnd.HtndMessage, htnd.HtndMessage> stream)
    {
        logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Fetching block info for {blockHash}");

        var request = new htnd.GetBlockRequestMessage { Hash = blockHash, IncludeTransactions = true };
        await Guard(() => stream.RequestStream.WriteAsync(new htnd.HtndMessage { GetBlockRequest = request }), ex => logger.Debug(ex));

        await foreach (var response in stream.ResponseStream.ReadAllAsync())
        {
            if (response == null || response.GetBlockResponse == null || response.GetBlockResponse.Block == null)
            {
                logger.Warn(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Block {blockHash} not found or invalid response.");
                return null;
            }

            return response.GetBlockResponse;
        }

        logger.Warn(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] No response received for block {blockHash}");
        return null;
    }


    private async Task<double> GetBlockConfirmations(string blockHash, int minConfirmations, AsyncDuplexStreamingCall<htnd.HtndMessage, htnd.HtndMessage> stream)
    {
        logger.Debug(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Checking confirmations for block {blockHash}");

        var request = new htnd.GetBlocksRequestMessage { LowHash = blockHash, IncludeBlocks = false, IncludeTransactions = false };
        await Guard(() => stream.RequestStream.WriteAsync(new htnd.HtndMessage { GetBlocksRequest = request }), ex => logger.Debug(ex));

        await foreach (var response in stream.ResponseStream.ReadAllAsync())
        {
            return Math.Min(1.0d, (double)response.GetBlocksResponse.BlockHashes.Count / minConfirmations);
        }
        return 0.0d;
    }

    private async Task MarkBlockAsOrphaned(Block block, string poolId, CoinTemplate coin, IBlockRepository blockRepo)
    {
        logger.Warn(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Marking block {block.BlockHeight} as orphaned");

        block.Status = BlockStatus.Orphaned;
        block.Reward = 0;
        block.ConfirmationProgress = 1;
        using var con = await connectionFactory.OpenConnectionAsync();
        await blockRepo.UpdateBlockStatusAndRewardAsync(con, poolId, (long)block.BlockHeight, block.Hash, BlockStatus.Orphaned, 0);
        messageBus.NotifyBlockUnlocked(poolId, block, coin);
    }

    private decimal GetFirstCoinbaseReward(htnd.GetBlockResponseMessage blockInfo)
    {
        if (blockInfo.Block.Transactions == null || blockInfo.Block.Transactions.Count == 0)
            return 0.0m;

        var coinbaseTransaction = blockInfo.Block.Transactions
            .FirstOrDefault(tx => tx.Inputs.Count == 0);

        if (coinbaseTransaction == null)
        {
            logger.Warn(() => $"Debug HoosatJob -----> No coinbase transaction found in block {blockInfo.Block.VerboseData.Hash}");
            return 0.0m;
        }

        var firstOutput = coinbaseTransaction.Outputs.FirstOrDefault();

        if (firstOutput == null)
        {
            logger.Warn(() => $"Debug HoosatJob -----> No outputs found in coinbase transaction of block {blockInfo.Block.VerboseData.Hash}");
            return 0.0m;
        }

        decimal reward = (decimal)(firstOutput.Amount / HoosatConstants.SmallestUnit);

        logger.Info(() => $"Debug HoosatJob -----> Reward found! Block {blockInfo.Block.VerboseData.Hash} provides {FormatAmount(reward)} as block reward");

        return reward;
    }

    private async Task FinalizeBlockProcessing(Block block, string poolId, CoinTemplate coin, IBlockRepository blockRepo, htnd.GetBlockResponseMessage blockInfo)
    {
        decimal blockReward = GetFirstCoinbaseReward(blockInfo);

        logger.Info(() => $"Debug HoosatJob -----> HoosatPayoutHandler [{LogCategory}] Finalizing block {block.BlockHeight} with reward {FormatAmount(blockReward)}");

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
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => x.Amount);


        if (amounts.Count == 0)
            return;

        var balancesTotal = amounts.Sum(x => x.Value);

        logger.Info(() => $"Base --- PayoutAsync - Initiating payout of {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        logger.Info(() => $"Base - Validating addresses...");
        var coin = poolConfig.Template.As<HoosatCoinTemplate>();
        foreach (var pair in amounts)
        {
            logger.Debug(() => $"Base --- PayoutAsync - Address {pair.Key} with amount [{FormatAmount(pair.Value)}]");
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

        await Parallel.ForEachAsync(amounts, parallelOptions, async (x, _ct) =>
        {
            var (address, amount) = x;

            await Guard(async () =>
            {
                // use a common id for all log entries related to this transfer
                var transferId = CorrelationIdGenerator.GetNextId();

                logger.Info(() => $"Base --- PayoutAsync - [{transferId}] Sending {FormatAmount(amount)} to {address}");

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

                // check result
                var txId = sendTransaction.TxIDs.First();

                if (string.IsNullOrEmpty(txId))
                    throw new Exception($"[{transferId}] htnWalletd did not return a transaction id!");
                else
                    logger.Info(() => $"Base --- PayoutAsync - [{transferId}] Payment transaction id: {txId}");

                successBalances.Add(new Balance
                {
                    PoolId = poolConfig.Id,
                    Address = address,
                    Amount = amount,
                }, txId);
            }, ex =>
            {
                txFailures.Add(Tuple.Create(x, ex));
                logger.Warn(() => $"Base --- PayoutAsync - Failed to send {FormatAmount(amount)} to {address}: {ex.Message}");
            });
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
