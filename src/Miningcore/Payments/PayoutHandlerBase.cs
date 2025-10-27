using System.Data;
using System.Data.Common;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using Polly;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

public abstract class PayoutHandlerBase
{
    protected PayoutHandlerBase(
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.cf = cf;
        this.mapper = mapper;
        this.clock = clock;
        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;
        this.balanceRepo = balanceRepo;
        this.paymentRepo = paymentRepo;
        this.messageBus = messageBus;

        BuildFaultHandlingPolicy();
    }

    protected readonly IBalanceRepository balanceRepo;
    protected readonly IBlockRepository blockRepo;
    protected readonly IConnectionFactory cf;
    protected readonly IMapper mapper;
    protected readonly IPaymentRepository paymentRepo;
    protected readonly IShareRepository shareRepo;
    protected readonly IMasterClock clock;
    protected readonly IMessageBus messageBus;
    protected ClusterConfig clusterConfig;
    private IAsyncPolicy faultPolicy;

    protected ILogger logger;
    protected PoolConfig poolConfig;
    private const int RetryCount = 8;

    protected abstract string LogCategory { get; }

    protected void BuildFaultHandlingPolicy()
    {
        var retry = Policy
            .Handle<DbException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(RetryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), OnRetry);

        faultPolicy = retry;
    }

    protected virtual void OnRetry(Exception ex, TimeSpan timeSpan, int retry, object context)
    {
        logger.Warn(() => $"[{LogCategory}] Retry {1} in {timeSpan} due to: {ex}");
    }






    public virtual async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct)
    {

        //elva Traitement pour le cas de luckybits - Recupéra du mutiplicateur -- Debut
        var blockHeight = block.BlockHeight;

        var coin_Temp = poolConfig.Coin;
        //Cas Luckybits (BIT)
        if (coin_Temp == "luckybits")
        {
        //var Newcoin = extraPoolConfig?.coin;
        //logger.Info(() => $"elva 1 --->  Block {block.BlockHeight} block {blockHeight0}");
        // Exemple d'un numéro de bloc que vous souhaitez passer
        ulong blockNumber = block.BlockHeight;
        // Appel de la méthode statique CallRewardProcessorAsync dans la classe Program
        await Program.CallRewardProcessorAsync(blockNumber);

        // Initialiser ApiFetcher et RewardProcessor
        ApiFetcherLuckybits apiFetcher = new ApiFetcherLuckybits();
        RewardProcessor processor = new RewardProcessor(apiFetcher);

        // Appeler la méthode pour traiter les valeurs
        await processor.ProcessRewardValuesAsync(blockNumber);

        // Récupérer les valeurs Multiplier et Base depuis les propriétés
        double multiplier = processor.Multiplier;
        double baseValue = processor.BaseValue;

        // Utiliser les valeurs récupérées
        // Console.WriteLine($"Multiplier récupéré dans un autre objet : {multiplier}");
        // Console.WriteLine($"Base récupérée dans un autre objet : {baseValue}");
     //   var coin_Temp = "luckybits";

        // elva Ajustement du reward
        if (coin_Temp == "luckybits")
        block.Reward = (decimal)Math.Abs(baseValue * multiplier);
        }


        var blockRewardRemaining = block.Reward;

        // Distribute funds to configured reward recipients
        foreach(var recipient in poolConfig.RewardRecipients.Where(x => x.Percentage > 0))
        {
            var amount = block.Reward * (recipient.Percentage / 100.0m);
            var address = recipient.Address;

            blockRewardRemaining -= amount;

            // skip transfers from pool wallet to pool wallet
            if(address != poolConfig.Address)
            {
                logger.Info(() => $"elva Base - Crediting {address} with {FormatAmount(amount)}");


                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for block {block.BlockHeight}");

                logger.Info(() => $"elva Base - Crediting {address} with {FormatAmount(amount)} Poolid {poolConfig.Id} tx {tx} for block {block.BlockHeight}");
            }
        }

        logger.Info(() => $"elva Base - blockRewardRemaining with {FormatAmount(blockRewardRemaining)}");

        return blockRewardRemaining;
    }

/*
public virtual async Task ProcessBlockRewardsAsync(IDbConnection con, IMiningPool pool, List<Block> blocks, CancellationToken ct)
{
    // Afficher la liste des blocs à traiter
    logger.Info(() => $"elva Debug -----> Début du traitement des blocs. Nombre total de blocs à traiter : {blocks.Count}");
    foreach (var block in blocks)
    {
        logger.Info(() => $"elva Debug -----> Bloc à traiter : {block.BlockHeight} avec une récompense estimée de {block.Reward}");
    }

    // Boucle pour traiter chaque bloc
    foreach (var block in blocks)
    {
        try
        {
            logger.Info(() => $"elva Debug -----> Début du traitement pour le bloc {block.BlockHeight}");

            // Nouvelle transaction pour chaque bloc
            using (var tx = con.BeginTransaction())
            {
                // Appel de la fonction UpdateBlockRewardBalancesAsync pour traiter le bloc
                var blockRewardRemaining = await UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);

                // Vérification et validation de la transaction
                if (tx.Connection != null && con.State == ConnectionState.Open)
                {
                    tx.Commit();
                    logger.Info(() => $"elva Debug -----> Transaction validée pour le bloc {block.BlockHeight}");
                }
                else
                {
                    logger.Warn(() => $"elva Debug -----> Impossible de valider la transaction pour le bloc {block.BlockHeight} car la connexion est fermée.");
                }
            }
        }
        catch (Exception ex)
        {
            // Log de l'erreur et continuer avec le bloc suivant
            logger.Error(() => $"Erreur lors du traitement du bloc {block.BlockHeight}: {ex.Message}");
            continue; // Continuer la boucle sans interruption
        }
    }
}

public virtual async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct)
{
    // Traitement spécifique pour "luckybits"
    if (poolConfig.Coin == "luckybits")
    {
        // Appel du traitement spécifique à "luckybits"
        await Program.CallRewardProcessorAsync((ulong)block.BlockHeight);
        ApiFetcherLuckybits apiFetcher = new ApiFetcherLuckybits();
        RewardProcessor processor = new RewardProcessor(apiFetcher);
        await processor.ProcessRewardValuesAsync((ulong)block.BlockHeight);

        // Ajustement de la récompense pour "luckybits"
        block.Reward = (decimal)Math.Abs(processor.BaseValue * processor.Multiplier);
    }

    var blockRewardRemaining = block.Reward;

    // Répartition des fonds aux destinataires
    foreach (var recipient in poolConfig.RewardRecipients.Where(x => x.Percentage > 0))
    {
        var amount = block.Reward * (recipient.Percentage / 100.0m);
        var address = recipient.Address;
        blockRewardRemaining -= amount;

logger.Info(() => $"elva Debug -----> amount {amount} avec le montant {FormatAmount(amount)} / address {address} / pour le bloc {block.BlockHeight}.");


        // Vérification de l'adresse du pool pour éviter les transferts internes
        if (address != poolConfig.Address)
        {
            try
            {
                // Log de pré-insertion dans la base de données
                logger.Info(() => $"elva Debug -----> Tentative d'ajout de montant pour l'adresse {address} avec le montant {FormatAmount(amount)} pour le bloc {block.BlockHeight}.");

                // Exécution de l'ajout dans la base de données
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for block {block.BlockHeight}");

                // Confirmation de l'ajout réussi
                logger.Info(() => $"elva Debug -----> Montant ajouté avec succès pour l'adresse {address} avec le montant {FormatAmount(amount)} pour le bloc {block.BlockHeight}.");
            }
            catch (Exception ex)
            {
                // Log en cas d'erreur mais continuer avec les autres destinataires
                logger.Error(() => $"Erreur lors de l'ajout de montant pour l'adresse {address} : {ex.Message}");
            }
        }
    }

    return blockRewardRemaining;
}
*/























public class Program
{
    // Méthode principale asynchrone avec Task
/*
    static async Task Main(string[] args)
    {
    //   int blockNumber = 100; // Vous pouvez passer une valeur ici ou demander à l'utilisateur
        await CallRewardProcessorAsync(blockNumber);
    }
    */

    // Méthode pour appeler RewardProcessor de manière asynchrone avec un paramètre blockNumber
  public static async Task CallRewardProcessorAsync(ulong blockNumber)
    {
      //  await CallRewardProcessorAsync(blockNumber);
      //  ulong blockNumber = 1000;

        // Créer une instance de ApiFetcher et RewardProcessor
        ApiFetcherLuckybits apiFetcher = new ApiFetcherLuckybits();
        RewardProcessor processor = new RewardProcessor(apiFetcher);

        // Appel de la fonction pour traiter les valeurs
        await processor.ProcessRewardValuesAsync(blockNumber);
    }
}

public class RewardProcessor
{
    private readonly ApiFetcherLuckybits apiFetcher;

    // Propriétés publiques pour exposer multiplier et baseValue
    public double Multiplier { get; private set; }
    public double BaseValue { get; private set; }

    public RewardProcessor(ApiFetcherLuckybits apiFetcher)
    {
        this.apiFetcher = apiFetcher;
    }

    // Fonction pour traiter les valeurs récupérées de l'API
    public async Task ProcessRewardValuesAsync(ulong blockNumber)
    {
        // Appel de la fonction pour récupérer les valeurs multiplier et base
        var (multiplier, baseValue) = await apiFetcher.GetRewardValuesAsync(blockNumber);

        // Stocker les valeurs dans les propriétés
        Multiplier = multiplier;
        BaseValue = baseValue;

        // Affichage des valeurs récupérées
        Console.WriteLine($"Pour le bloc {blockNumber} :");
        Console.WriteLine($"Multiplier : {Multiplier}");
        Console.WriteLine($"Base : {BaseValue}");


        // Vous pouvez maintenant utiliser ces valeurs dans votre programme
    }
}




    protected async Task PersistPaymentsAsync(Balance[] balances, string transactionConfirmation)
    {
        Contract.RequiresNonNull(balances);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(transactionConfirmation));

        var coin = poolConfig.Template.As<CoinTemplate>();

        try
        {
            await faultPolicy.ExecuteAsync(async () =>
            {
                await cf.RunTx(async (con, tx) =>
                {
                    foreach(var balance in balances)
                    {
                        if(!string.IsNullOrEmpty(transactionConfirmation) && poolConfig.RewardRecipients.All(x => x.Address != balance.Address))
                        {
                            // record payment
                            var payment = new Payment
                            {
                                PoolId = poolConfig.Id,
                                Coin = coin.Symbol,
                                Address = balance.Address,
                                Amount = balance.Amount,
                                Created = clock.Now,
                                TransactionConfirmationData = transactionConfirmation
                            };

                            await paymentRepo.InsertAsync(con, tx, payment);
                        }

                        // reset balance
                        logger.Info(() => $"[{LogCategory}] Resetting balance of {balance.Address}");
                        await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after payment");
                    }
                });
            });
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Failed to persist the following payments: " +
                $"{JsonConvert.SerializeObject(balances.Where(x => x.Amount > 0).ToDictionary(x => x.Address, x => x.Amount))}");
            throw;
        }
    }

    protected async Task PersistPaymentsAsync(Dictionary<Balance, string> balances)
    {
        Contract.RequiresNonNull(balances);
        Contract.Requires<ArgumentException>(balances.Count > 0);

        var coin = poolConfig.Template.As<CoinTemplate>();

        try
        {
            await faultPolicy.ExecuteAsync(async () =>
            {
                await cf.RunTx(async (con, tx) =>
                {
                    foreach(var kvp in balances)
                    {
                        var (balance, transactionConfirmation) = kvp;

                        if(!string.IsNullOrEmpty(transactionConfirmation) && poolConfig.RewardRecipients.All(x => x.Address != balance.Address))
                        {
                            // record payment
                            var payment = new Payment
                            {
                                PoolId = poolConfig.Id,
                                Coin = coin.Symbol,
                                Address = balance.Address,
                                Amount = balance.Amount,
                                Created = clock.Now,
                                TransactionConfirmationData = transactionConfirmation
                            };

                            await paymentRepo.InsertAsync(con, tx, payment);
                        }

                        // reset balance
                        logger.Info(() => $"[{LogCategory}] Resetting balance of {balance.Address}");
                        await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after payment");
                    }
                });
            });
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Failed to persist the following payments: " +
                $"{JsonConvert.SerializeObject(balances.Where(x => x.Key.Amount > 0).ToDictionary(x => x.Key.Address, x => x.Key.Amount))}");
            throw;
        }
    }

    public virtual double AdjustShareDifficulty(double difficulty)
    {
        return difficulty;
    }

    public string FormatAmount(decimal amount)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();
        return $"{amount:0.#####} {coin.Symbol}";
    }

    protected virtual void NotifyPayoutSuccess(string poolId, Balance[] balances, string[] txHashes, decimal? txFee)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();

        // admin notifications
        var explorerLinks = !string.IsNullOrEmpty(coin.ExplorerTxLink) ?
            txHashes.Select(x => string.Format(coin.ExplorerTxLink, x)).ToArray() :
            Array.Empty<string>();

        messageBus.SendMessage(new PaymentNotification(poolId, null, balances.Sum(x => x.Amount), coin.Symbol, balances.Length, txHashes, explorerLinks, txFee));
    }

    protected virtual void NotifyPayoutFailure(string poolId, Balance[] balances, string error, Exception ex)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();

        messageBus.SendMessage(new PaymentNotification(poolId, error ?? ex?.Message, balances.Sum(x => x.Amount), coin.Symbol));
    }
}
