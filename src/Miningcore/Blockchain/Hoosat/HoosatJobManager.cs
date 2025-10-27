using System;
using static System.Array;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autofac;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Hoosat.Configuration;
using NLog;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using htnWalletd = Miningcore.Blockchain.Hoosat.HtnWalletd;
using htnd = Miningcore.Blockchain.Hoosat.Htnd;
using Miningcore.Blockchain.Hoosat;
using CircularBuffer;

using System.Data;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;

using System.IO;
using pb = Google.Protobuf;

namespace Miningcore.Blockchain.Hoosat;


public class HoosatJobManager : JobManagerBase<HoosatJob>
{

    public HoosatJobManager(
        IComponentContext ctx,
        IMessageBus messageBus,
        IMasterClock clock,
        IExtraNonceProvider extraNonceProvider) : base(ctx, messageBus)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
    }

    private DaemonEndpointConfig[] daemonEndpoints;
    private DaemonEndpointConfig[] walletDaemonEndpoints;
    private HoosatCoinTemplate coin;
    private htnd.HtndRPC.HtndRPCClient rpc;
    private htnWalletd.HtnWalletdRPC.HtnWalletdRPCClient walletRpc;
    private string network;
    private readonly List<HoosatJob> validJobs = new();
    private readonly IExtraNonceProvider extraNonceProvider;
    private readonly IMasterClock clock;
    private HoosatPoolConfigExtra extraPoolConfig;
    private HoosatPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    protected int maxActiveJobs;
    protected string extraData;
   // protected IHashAlgorithm customBlockHeaderHasher;
   // protected IHashAlgorithm customCoinbaseHasher;
  //  protected IHashAlgorithm customShareHasher;

    protected IObservable<htnd.RpcBlock> HoosatSubscribeNewBlockTemplate(CancellationToken ct, object payload = null,
        JsonSerializerSettings payloadJsonSerializerSettings = null)
    {
        return Observable.Defer(() => Observable.Create<htnd.RpcBlock>(obs =>
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Task.Run(async () =>
            {
                using(cts)
                {
                    retry_subscription:
                        // we need a stream to communicate with htnd
                        var streamNotifyNewBlockTemplate = rpc.MessageStream(null, null, cts.Token);

                        // we need a request for subscribing to NotifyNewBlockTemplate
                        var requestNotifyNewBlockTemplate = new htnd.HtndMessage();
                        requestNotifyNewBlockTemplate.NotifyNewBlockTemplateRequest = new htnd.NotifyNewBlockTemplateRequestMessage();

                        // we need a request for retrieving BlockTemplate
                        var requestBlockTemplate = new htnd.HtndMessage();
                        requestBlockTemplate.GetBlockTemplateRequest = new htnd.GetBlockTemplateRequestMessage
                        {
                            PayAddress = poolConfig.Address,
                            ExtraData = extraData,
                        };

                        logger.Debug(() => $"Sending NotifyNewBlockTemplateRequest");

                        try
                        {
                            await streamNotifyNewBlockTemplate.RequestStream.WriteAsync(requestNotifyNewBlockTemplate, cts.Token);
                        }
                        catch(Exception ex)
                        {
                            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while subscribing to htnd \"NewBlockTemplate\" notifications");

                            if(!cts.IsCancellationRequested)
                            {
                                // We make sure the stream is closed in order to free resources and avoid reaching the "RPC inbound connections limitation"
                                await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                                logger.Error(() => $"Reconnecting in 10s");
                                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                                goto retry_subscription;
                            }
                            else
                                goto end_gameover;
                        }

                        while (!cts.IsCancellationRequested)
                        {
                            logger.Debug(() => $"Successful `NewBlockTemplate` subscription");

                            retry_blocktemplate:
                                logger.Debug(() => $"New job received :D");

                                try
                                {
                                    await streamNotifyNewBlockTemplate.RequestStream.WriteAsync(requestBlockTemplate, cts.Token);
                                    await foreach (var responseBlockTemplate in streamNotifyNewBlockTemplate.ResponseStream.ReadAllAsync(cts.Token))
                                    {
                                        logger.Debug(() => $"DaaScore (BlockHeight): {responseBlockTemplate.GetBlockTemplateResponse.Block.Header.DaaScore}");

                                        // publish
                                        //logger.Debug(() => $"Publishing...");
                                        obs.OnNext(responseBlockTemplate.GetBlockTemplateResponse.Block);

                                        if(!string.IsNullOrEmpty(responseBlockTemplate.GetBlockTemplateResponse.Error?.Message))
                                            logger.Warn(() => responseBlockTemplate.GetBlockTemplateResponse.Error?.Message);
                                    }
                                }
                                catch(NullReferenceException)
                                {
                                    // The following is weird but correct, when all data has been received `streamNotifyNewBlockTemplate.ResponseStream.ReadAllAsync()` will return a `NullReferenceException`
                                    logger.Debug(() => $"Waiting for data...");
                                    goto retry_blocktemplate;
                                }

                                catch(Exception ex)
                                {
                                    logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while streaming htnd \"NewBlockTemplate\" notifications");

                                    if(!cts.IsCancellationRequested)
                                    {
                                        // We make sure the stream is closed in order to free resources and avoid reaching the "RPC inbound connections limitation"
                                        await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                                        logger.Error(() => $"Reconnecting in 10s");
                                        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                                        goto retry_subscription;
                                    }
                                    else
                                        goto end_gameover;
                                }
                        }

                        end_gameover:
                            // We make sure the stream is closed in order to free resources and avoid reaching the "RPC inbound connections limitation"
                            await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                            logger.Debug(() => $"No more data received. Bye!");
                }
            }, cts.Token);

            return Disposable.Create(() => { cts.Cancel(); });
        }));
    }

    private void SetupJobUpdates(CancellationToken ct)
    {
        var blockFound = blockFoundSubject.Synchronize();
        var pollTimerRestart = blockFoundSubject.Synchronize();

        var triggers = new List<IObservable<(string Via, htnd.RpcBlock Data)>>
        {
            blockFound.Select(_ => (JobRefreshBy.BlockFound, (htnd.RpcBlock) null))
        };

        // Listen to htnd "NewBlockTemplate" notifications
        var getWorkHtnd = HoosatSubscribeNewBlockTemplate(ct)
            .Publish()
            .RefCount();

        triggers.Add(getWorkHtnd
            .Select(blockTemplate => (JobRefreshBy.BlockTemplateStream, blockTemplate))
            .Publish()
            .RefCount());

        // get initial blocktemplate
        triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(1000))
            .Select(_ => (JobRefreshBy.Initial, (htnd.RpcBlock) null))
            .TakeWhile(_ => !hasInitialBlockTemplate));

        Jobs = triggers.Merge()
            .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Via, x.Data)))
            .Concat()
            .Where(x => x)
            .Do(x =>
            {
                if(x)
                    hasInitialBlockTemplate = true;
            })
            .Select(x => GetJobParamsForStratum())
            .Publish()
            .RefCount();
    }


/*
    private Blake3? customBlockHeaderHasher;
    private Blake3? customCoinbaseHasher;
    private Blake3? customShareHasher;
*/
    protected IHashAlgorithm customBlockHeaderHasher;
    protected IHashAlgorithm customCoinbaseHasher;
    protected IHashAlgorithm customShareHasher;



public HoosatJob CreateJob(long blockHeight)
{
    if (coin == null || string.IsNullOrEmpty(coin.Symbol))
    {
        throw new InvalidOperationException("[ERROR] 'coin.Symbol' is not defined or initialized.");
    }

    string coinSymbol = coin.Symbol;


                Console.WriteLine("[DEBUG] Initializing default hashers for Hoosat.");
                customBlockHeaderHasher = InitializeHasher<Blake3>(
                    customBlockHeaderHasher,
                    () => CreateBlake3Hasher(HoosatConstants.CoinbaseBlockHash, "Block Header"),
                    "Block Header Hasher"
                );

                customCoinbaseHasher = InitializeHasher<Blake3>(
                    customCoinbaseHasher,
                    () => new Blake3(),
                    "Coinbase Hasher"
                );

                customShareHasher = InitializeHasher<Blake3>(
                    customShareHasher,
                    () => new Blake3(),
                    "Share Hasher"
                );



        Console.WriteLine("[DEBUG] Creating HoosatJob instance.");
        var job = new HoosatJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);

        Console.WriteLine("[DEBUG] HoosatJob successfully created.");
        return job;

}

// Utility method to initialize hashers
private T InitializeHasher<T>(object hasher, Func<T> initializer, string hasherName) where T : class
{
    if (hasher is not T)
    {
        Console.WriteLine($"[DEBUG] Initializing {hasherName}.");
        return initializer();
    }

    Console.WriteLine($"[DEBUG] {hasherName} already initialized.");
    return hasher as T;
}

// Utility method for creating Blake3 hashers
private Blake3 CreateBlake3Hasher(string coinbaseBlockHash, string purpose)
{
    Console.WriteLine($"[DEBUG] Initializing Blake3 for {purpose}.");
    byte[] hashBytes = Encoding.UTF8
        .GetBytes(coinbaseBlockHash.PadRight(32, '\0'))
        .Take(32)
        .ToArray();

    Console.WriteLine($"[DEBUG] HashBytes for {purpose} (hex): {BitConverter.ToString(hashBytes).Replace("-", "")}");
    return new Blake3(hashBytes);
}
    private async Task<bool> UpdateJob(CancellationToken ct, string via = null, htnd.RpcBlock blockTemplate = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        return await Task.Run(() =>
        {
            using(cts)
            {
                try
                {
                    if(blockTemplate == null)
                        return false;

                    var job = currentJob;

                    var isNew = (job == null || job.BlockTemplate?.Header.DaaScore < blockTemplate.Header.DaaScore);

                    if(isNew)
                        messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Header.DaaScore, poolConfig.Template);

                    if(isNew)
                    {
                        job = CreateJob((long) blockTemplate.Header.DaaScore);

                        job.Init(blockTemplate, NextJobId());

                        lock(jobLock)
                        {
                            validJobs.Insert(0, job);

                            // trim active jobs
                            while(validJobs.Count > maxActiveJobs)
                                validJobs.RemoveAt(validJobs.Count - 1);
                        }

                        logger.Debug(() => $"blockTargetValue: {job.blockTargetValue}");
                        logger.Debug(() => $"Difficulty: {job.Difficulty}");

                        if(via != null)
                            logger.Info(() => $"Detected new block {job.BlockTemplate.Header.DaaScore} [{via}]");
                        else
                            logger.Info(() => $"Detected new block {job.BlockTemplate.Header.DaaScore}");

                        // update stats
                        if (job.BlockTemplate.Header.DaaScore > BlockchainStats.BlockHeight)
                        {
                            // update stats
                            BlockchainStats.LastNetworkBlockTime = clock.Now;
                            BlockchainStats.BlockHeight = job.BlockTemplate.Header.DaaScore;
                            BlockchainStats.NetworkDifficulty = job.Difficulty;
                        }

                        currentJob = job;
                    }
                    else
                    {
                        if(via != null)
                            logger.Debug(() => $"Template update {job.BlockTemplate.Header.DaaScore}");
                        else
                            logger.Debug(() => $"Template update {job.BlockTemplate.Header.DaaScore}");
                    }

                    return isNew;
                }

                catch(OperationCanceledException)
                {
                    // ignored
                }

                catch(Exception ex)
                {
                    logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while updating new job");
                }

                return false;
            }
        }, cts.Token);
    }

    private async Task UpdateNetworkStatsAsync(CancellationToken ct)
    {
        try
        {
            // update stats
            // we need a stream to communicate with Htnd
            var stream = rpc.MessageStream(null, null, ct);

            var request = new htnd.HtndMessage();
            request.EstimateNetworkHashesPerSecondRequest = new htnd.EstimateNetworkHashesPerSecondRequestMessage
            {
                WindowSize = 1000,
            };
            await stream.RequestStream.WriteAsync(request);
            await foreach (var infoHashrate in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(string.IsNullOrEmpty(infoHashrate.EstimateNetworkHashesPerSecondResponse.Error?.Message))
                    BlockchainStats.NetworkHashrate = (double) infoHashrate.EstimateNetworkHashesPerSecondResponse.NetworkHashesPerSecond;

                break;
            }

            request = new htnd.HtndMessage();
            request.GetConnectedPeerInfoRequest = new htnd.GetConnectedPeerInfoRequestMessage();
            await stream.RequestStream.WriteAsync(request);
            await foreach (var info in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(string.IsNullOrEmpty(info.GetConnectedPeerInfoResponse.Error?.Message))
                    BlockchainStats.ConnectedPeers = info.GetConnectedPeerInfoResponse.Infos.Count;

                break;
            }
            await stream.RequestStream.CompleteAsync();
        }

        catch(Exception ex)
        {
            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while updating network stats");
        }
    }

    private async Task ShowDaemonSyncProgressAsync(CancellationToken ct)
    {
        // we need a stream to communicate with htnd
        var stream = rpc.MessageStream(null, null, ct);

        var request = new htnd.HtndMessage();
        request.GetInfoRequest = new htnd.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> logger.Debug(ex));
        await foreach (var info in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                logger.Debug(info.GetInfoResponse.Error?.Message);

            if(info.GetInfoResponse.IsSynced != true && info.GetInfoResponse.IsUtxoIndexed != true)
                logger.Info(() => $"Daemon is downloading headers ...");

            break;
        }
        await stream.RequestStream.CompleteAsync();
    }

private async Task<bool> SubmitBlockAsync(CancellationToken ct, htnd.RpcBlock block, string powHash, ulong nonce, object payload = null,
    JsonSerializerSettings payloadJsonSerializerSettings = null)
{
//    Console.WriteLine("elva Debug HoosatJob -----> SubmitBlockAsync --- Starting block submission...");

    block.Header.Nonce = nonce;
//    Console.WriteLine($"elva Debug HoosatJobManager -----> SubmitBlockAsync --- powHash (BigInteger): {powHash}");

    Contract.RequiresNonNull(block);
    bool isSuccessful = false;

    try
    {
 //       Console.WriteLine("elva Debug HoosatJobManager -----> SubmitBlockAsync --- Establishing message stream...");

    var powHash_x0 = "";

        // Vérifiez si le nonce commence par "0x" et le supprime si c'est le cas
        if (powHash.StartsWith("0x"))
        {
            powHash_x0 = powHash.Substring(2);
      //      Console.WriteLine("elva Debug HoosatJob -----> ProcessShare ---> Préfixe '0x' supprimé du nonce");
        }
   //     Console.WriteLine($"elva Debug HoosatJobManager -----> SubmitBlockAsync --- powHash submitted powHash {powHash} without x0 {powHash_x0}");

        // Création de la requête de soumission du bloc
        var request = new htnd.HtndMessage
        {
            SubmitBlockRequest = new htnd.SubmitBlockRequestMessage
            {
                Block = block,
                AllowNonDAABlocks = true,
                PowHash = powHash_x0
            }
        };

        // Sérialisation et affichage du message en JSON pour le log
        var requestJson = JsonConvert.SerializeObject(request, Formatting.Indented);
        Console.WriteLine("elva Debug HoosatJobManager -----> SubmitBlockAsync --- Full SubmitBlockRequestMessage: ");
        Console.WriteLine(requestJson);

        // Envoi de la requête
        using var stream = rpc.MessageStream(null, null, ct);
        await stream.RequestStream.WriteAsync(request);
 //       Console.WriteLine("elva Debug HoosatJobManager -----> SubmitBlockAsync --- Block submission request sent successfully.");

        // Lire la réponse de soumission
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (!string.IsNullOrEmpty(response.SubmitBlockResponse.Error?.Message))
            {
                string errorMsg = response.SubmitBlockResponse.Error.Message;
                string rejectReason = response.SubmitBlockResponse.RejectReason.ToString();
 //               Console.WriteLine($"elva Debug HoosatJobManager -----> SubmitBlockAsync --- Block submission failed. Error: {errorMsg}, Reason: {rejectReason}");

                logger.Warn(() => $"Block submission failed: {errorMsg} [{rejectReason}]");

                messageBus.SendMessage(new AdminNotification(
                    "Block submission failed",
                    $"Pool {poolConfig.Id}: {errorMsg} [{rejectReason}]"));
            }
            else
            {
 //               Console.WriteLine("elva Debug HoosatJobManager -----> SubmitBlockAsync --- Block submission succeeded.");
                isSuccessful = true;
            }

            break;
        }

        await stream.RequestStream.CompleteAsync();
  //      Console.WriteLine("elva Debug HoosatJobManager -----> SubmitBlockAsync --- Stream completed.");
    }
    catch (Exception ex)
    {
  //      Console.WriteLine($"elva Debug HoosatJobManager -----> SubmitBlockAsync --- Exception caught: {ex.GetType().Name} - {ex.Message}");
        logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while submitting block");

        messageBus.SendMessage(new AdminNotification(
            "Block submission failed",
            $"Pool {poolConfig.Id} failed to submit block"));
    }

 //   Console.WriteLine($"elva Debug HoosatJobManager -----> SubmitBlockAsync --- Block submission result: {(isSuccessful ? "Success" : "Failure")}");
    return isSuccessful;
}
public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission, CancellationToken ct)
{
    Contract.RequiresNonNull(worker);
    Contract.RequiresNonNull(submission);
    logger.Debug("elva Debug SubmitShareAsync ---> Start: Validation started for worker and submission.");

    if (submission is not object[] submitParams)
    {
        logger.Error("elva Error SubmitShareAsync ---> Invalid parameters: Submission is not an object array.");
        throw new StratumException(StratumError.Other, "invalid params");
    }

    var context = worker.ContextAs<HoosatWorkerContext>();
    logger.Debug($"elva Debug SubmitShareAsync ---> Worker context obtained: Miner = {context.Miner}, Worker = {context.Worker}");

    // Log complet des paramètres transmis par le mineur
    for (int i = 0; i < submitParams.Length; i++)
    {
        logger.Debug($"elva Debug SubmitShareAsync ---> Parameter {i}: {submitParams[i]?.ToString() ?? "null"}");
    }

    var jobId = submitParams[1] as string;
    var nonce = submitParams[2] as string;
    var powHashHexString = submitParams.Length > 3 ? submitParams[3] as string : null;

    logger.Debug($"elva Debug SubmitShareAsync ---> Submission parameters extracted - JobId: {jobId}, Nonce: {nonce}, powHashHexString: {powHashHexString}");

    HoosatJob job;

    lock (jobLock)
    {
        job = validJobs.FirstOrDefault(x => x.JobId == jobId);
    }

    if (job == null)
    {
        logger.Error($"elva Error SubmitShareAsync ---> Job not found for JobId: {jobId}");
        throw new StratumException(StratumError.JobNotFound, "job not found");
    }

    logger.Debug($"elva Debug SubmitShareAsync ---> Job found - Processing share for JobId: {jobId}");

    if (string.IsNullOrEmpty(powHashHexString))
    {
        throw new StratumException(StratumError.Other, "powHashHexString is missing or invalid.");
    }

    // Conversion de powHashHexString en BigInteger
    byte[] powHashBytes = ConvertHexStringToByteArray(powHashHexString);
    BigInteger powHashBigInt = new BigInteger(powHashBytes, isUnsigned: true, isBigEndian: false);
    logger.Debug($"elva Debug SubmitShareAsync ---> Converted powHashHexString to BigInteger: {powHashBigInt}");
    var share = job.ProcessShare(worker, nonce, powHashHexString);
    logger.Debug($"elva Debug SubmitShareAsync ---> ProcessShare called successfully - Share generated with BlockHeight: {share.BlockHeight}");
    share.PoolId = poolConfig.Id ?? throw new NullReferenceException("poolConfig.Id is null");
    share.IpAddress = worker.RemoteEndpoint?.Address?.ToString() ?? throw new NullReferenceException("worker.RemoteEndpoint.Address is null");
    share.Miner = context.Miner;
    share.Worker = context.Worker;
    share.UserAgent = context.UserAgent;
    share.Source = clusterConfig.ClusterName;
    share.Created = clock.Now;
    logger.Debug($"elva Debug SubmitShareAsync ---> Share enriched with PoolId: {share.PoolId}, IP Address: {share.IpAddress}, Miner: {share.Miner}, Worker: {share.Worker}, Source: {share.Source}");

    // Soumission et vérification de bloc candidat
    if (share.IsBlockCandidate)
    {
        logger.Info($"elva Info SubmitShareAsync ---> Submitting block {share.BlockHeight} [{share.BlockHash}] Pw [{powHashHexString}]");

        string nonceString = nonce.Replace("0x", "");
        if (!ulong.TryParse(nonceString, System.Globalization.NumberStyles.HexNumber, null, out ulong nonceValue))
        {
            throw new FormatException($"Invalid nonce format: {nonce}");
        }

        var acceptResponse = await SubmitBlockAsync(ct, job.BlockTemplate, powHashHexString, nonceValue);
        share.IsBlockCandidate = acceptResponse;

        if (share.IsBlockCandidate)
        {
            logger.Info($"elva Info SubmitShareAsync ---> Daemon accepted block {share.BlockHeight} block [{share.BlockHash}] submitted by {context.Miner}");
            OnBlockFound();
            share.TransactionConfirmationData = nonce;
            logger.Debug($"elva Debug SubmitShareAsync ---> Nonce persisted for block unlocking: {nonce}");
        }
        else
        {
            share.TransactionConfirmationData = null;
            logger.Debug("elva Debug SubmitShareAsync ---> Block candidate not accepted by daemon, cleared TransactionConfirmationData");
        }
    }

    logger.Debug("elva Debug SubmitShareAsync ---> Completed successfully, returning share");
    return share;
}
private byte[] ConvertHexStringToByteArray(string hexString)
{
    hexString = hexString.Replace("0x", "").Replace("-", "");
    byte[] bytes = new byte[hexString.Length / 2];
    for (int i = 0; i < hexString.Length; i += 2)
        bytes[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
    return bytes;
}
private BigInteger ConvertBitsToBigInteger(uint bits)
{
    // Extraction de l'exposant et de la mantisse
    int exponent = (int)((bits >> 24) & 0xFF);
    BigInteger mantissa = bits & 0xFFFFFF;

    // Si la mantisse dépasse 0x800000, la cible est divisée par deux
    if ((mantissa & 0x800000) != 0)
        mantissa = mantissa >> 1;

    // Calcul du target en BigInteger
    BigInteger target = mantissa * BigInteger.Pow(2, (8 * (exponent - 3)));
    return target;
}
    #region API-Surface

    public IObservable<object[]> Jobs { get; private set; }
    public BlockchainStats BlockchainStats { get; } = new();
    public string Network => network;

   // public HoosatCoinTemplate Coin => coin;

    public object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<HoosatWorkerContext>();
        var extraNonce1Size = GetExtraNonce1Size();


        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            HoosatConstants.ExtranoncePlaceHolderLength - extraNonce1Size,
        };

        return responseData;
    }

    public int GetExtraNonce1Size()
    {
//        return extraPoolConfig?.ExtraNonce1Size ?? 0;
                return extraPoolConfig?.ExtraNonce1Size ?? 2;

    }
    public bool ValidateIsLargeJob(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        if(ValidateIsBzMiner(userAgent))
            return true;

        if(ValidateIsIceRiverMiner(userAgent))
            return true;

        return false;
    }

    public bool ValidateIsBzMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        // Find matches
        MatchCollection matchesUserAgentBzMiner = HoosatConstants.RegexUserAgentBzMiner.Matches(userAgent);
        return (matchesUserAgentBzMiner.Count > 0);
    }

    public bool ValidateIsGodMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        // Find matches
        MatchCollection matchesUserAgentGodMiner = HoosatConstants.RegexUserAgentGodMiner.Matches(userAgent);
        return (matchesUserAgentGodMiner.Count > 0);
    }

    public bool ValidateIsIceRiverMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        // Find matches
        MatchCollection matchesUserAgentIceRiverMiner = HoosatConstants.RegexUserAgentIceRiverMiner.Matches(userAgent);
        return (matchesUserAgentIceRiverMiner.Count > 0);
    }

    #endregion // API-Surface

    #region Overrides

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        // validate pool address
        if(string.IsNullOrEmpty(poolConfig.Address))
            throw new PoolStartupException($"Pool address is not configured", poolConfig.Id);

        // we need a stream to communicate with htnd
        var stream = rpc.MessageStream(null, null, ct);

        var request = new htnd.HtndMessage();
        request.GetCurrentNetworkRequest = new htnd.GetCurrentNetworkRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> throw new PoolStartupException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}", poolConfig.Id));
        await foreach (var currentNetwork in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(currentNetwork.GetCurrentNetworkResponse.Error?.Message))
                throw new PoolStartupException($"Daemon reports: {currentNetwork.GetCurrentNetworkResponse.Error?.Message}", poolConfig.Id);

            network = currentNetwork.GetCurrentNetworkResponse.CurrentNetwork;
            break;
        }

        var (HoosatAddressUtility, errorHoosatAddressUtility) = HoosatUtils.ValidateAddress(poolConfig.Address, network, coin.Symbol);
        if(errorHoosatAddressUtility != null)
            throw new PoolStartupException($"Pool address: {poolConfig.Address} is invalid for network [{network}]: {errorHoosatAddressUtility}", poolConfig.Id);
        else
            logger.Info(() => $"Pool address: {poolConfig.Address} => {HoosatConstants.HoosatAddressType[HoosatAddressUtility.HoosatAddress.Version()]}");

        // update stats
        BlockchainStats.NetworkType = network;
        BlockchainStats.RewardType = "POW";

        request = new htnd.HtndMessage();
        request.GetInfoRequest = new htnd.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> throw new PoolStartupException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}", poolConfig.Id));
        await foreach (var info in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                throw new PoolStartupException($"Daemon reports: {info.GetInfoResponse.Error?.Message}", poolConfig.Id);

            if(info.GetInfoResponse.IsUtxoIndexed != true)
                throw new PoolStartupException("UTXO index is disabled", poolConfig.Id);

            extraData = (string) info.GetInfoResponse.ServerVersion + (!string.IsNullOrEmpty(extraData) ? "." + extraData : "");
            break;
        }
        await stream.RequestStream.CompleteAsync();

        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            // we need a call to communicate with htndWallet
            var call = walletRpc.ShowAddressesAsync(new htnWalletd.ShowAddressesRequest(), null, null, ct);

            // check configured address belongs to wallet
            var walletAddresses = await Guard(() => call.ResponseAsync,
                ex=> throw new PoolStartupException($"Error validating pool address '{ex.GetType().Name}' : {ex}", poolConfig.Id));
            call.Dispose();

            if(!walletAddresses.Address.Contains(poolConfig.Address))
                throw new PoolStartupException($"Pool address: {poolConfig.Address} is not controlled by pool wallet", poolConfig.Id);
        }

        await UpdateNetworkStatsAsync(ct);

        // Periodically update network stats
        Observable.Interval(TimeSpan.FromMinutes(1))
            .Select(via => Observable.FromAsync(() =>
                Guard(()=> UpdateNetworkStatsAsync(ct),
                    ex=> logger.Error(ex))))
            .Concat()
            .Subscribe();

        SetupJobUpdates(ct);
    }

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<HoosatCoinTemplate>();

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<HoosatPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<HoosatPaymentProcessingConfigExtra>();

        maxActiveJobs = extraPoolConfig?.MaxActiveJobs ?? 8;
        extraData = extraPoolConfig?.ExtraData ?? "Hoohash - pools4mining.com[\"@elvalere\"]";

        // extract standard daemon endpoints
        daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        if(cc.PaymentProcessing?.Enabled == true && pc.PaymentProcessing?.Enabled == true)
        {
            // extract wallet daemon endpoints
            walletDaemonEndpoints = pc.Daemons
                .Where(x => x.Category?.ToLower() == HoosatConstants.WalletDaemonCategory)
                .ToArray();

            if(walletDaemonEndpoints.Length == 0)
                throw new PoolStartupException("Wallet-RPC daemon is not configured (Daemon configuration for Hoosat-pools require an additional entry of category 'wallet' pointing to the wallet daemon)", pc.Id);
        }

        base.Configure(pc, cc);
    }

    protected override void ConfigureDaemons()
    {
        logger.Debug(() => $"ProtobufDaemonRpcServiceName: {extraPoolConfig?.ProtobufDaemonRpcServiceName ?? HoosatConstants.ProtobufDaemonRpcServiceName}");

        rpc = HoosatClientFactory.CreateHtndRPCClient(daemonEndpoints, extraPoolConfig?.ProtobufDaemonRpcServiceName ?? HoosatConstants.ProtobufDaemonRpcServiceName);

        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            logger.Debug(() => $"ProtobufWalletRpcServiceName: {extraPoolConfig?.ProtobufWalletRpcServiceName ?? HoosatConstants.ProtobufWalletRpcServiceName}");

            walletRpc = HoosatClientFactory.CreateHtnWalletdRPCClient(walletDaemonEndpoints, extraPoolConfig?.ProtobufWalletRpcServiceName ?? HoosatConstants.ProtobufWalletRpcServiceName);
        }
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            // we need a call to communicate with htndWallet
            var call = walletRpc.ShowAddressesAsync(new htnWalletd.ShowAddressesRequest(), null, null, ct);

            // check configured address belongs to wallet
            var walletAddresses = await Guard(() => call.ResponseAsync,
                ex=> logger.Debug(ex));
            call.Dispose();

            if(walletAddresses == null)
                return false;
        }

        // we need a stream to communicate with htnd
        var stream = rpc.MessageStream(null, null, ct);

        var request = new htnd.HtndMessage();
        request.GetInfoRequest = new htnd.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> logger.Debug(ex));
        bool areDaemonsHealthy = false;
        await foreach (var info in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
            {
                logger.Debug(info.GetInfoResponse.Error?.Message);
                return false;
            }

            if(info.GetInfoResponse.IsUtxoIndexed != true)
                throw new PoolStartupException("UTXO index is disabled", poolConfig.Id);

            // update stats
            if(info.GetInfoResponse.ServerVersion != null)
                BlockchainStats.NodeVersion = (string) info.GetInfoResponse.ServerVersion;

            areDaemonsHealthy = true;
            break;
        }
        await stream.RequestStream.CompleteAsync();

        return areDaemonsHealthy;
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            // we need a call to communicate with htndWallet
            var call = walletRpc.ShowAddressesAsync(new htnWalletd.ShowAddressesRequest(), null, null, ct);

            // check if daemon responds
            var walletAddresses = await Guard(() => call.ResponseAsync,
                ex=> logger.Debug(ex));
            call.Dispose();

            if(walletAddresses == null)
                return false;
        }

        // we need a stream to communicate with Ktnd
        var stream = rpc.MessageStream(null, null, ct);

        var request = new htnd.HtndMessage();
        request.GetConnectedPeerInfoRequest = new htnd.GetConnectedPeerInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> logger.Debug(ex));
        int totalPeers = 0;
        await foreach (var info in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(info.GetConnectedPeerInfoResponse.Error?.Message))
            {
                logger.Debug(info.GetConnectedPeerInfoResponse.Error?.Message);
                return false;
            }
            else
                totalPeers = info.GetConnectedPeerInfoResponse.Infos.Count;

            break;
        }
        await stream.RequestStream.CompleteAsync();

        return totalPeers > 0;
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var isSynched = false;

            // we need a stream to communicate with Ktnd
            var stream = rpc.MessageStream(null, null, ct);

            var request = new htnd.HtndMessage();
            request.GetInfoRequest = new htnd.GetInfoRequestMessage();
            await Guard(() => stream.RequestStream.WriteAsync(request),
                ex=> logger.Debug(ex));
            await foreach (var info in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                    logger.Debug(info.GetInfoResponse.Error?.Message);

                isSynched = (info.GetInfoResponse.IsSynced == true && info.GetInfoResponse.IsUtxoIndexed == true);
                break;
            }
            await stream.RequestStream.CompleteAsync();

            if(isSynched)
            {
                logger.Info(() => "Daemon is synced with blockchain");
                break;
            }

            if(!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while(await timer.WaitForNextTickAsync(ct));
    }

    private object[] GetJobParamsForStratum()
    {
        var job = currentJob;
        return job?.GetJobParams();
    }

    #endregion // Overrides
}
