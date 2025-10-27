using System.Numerics;
using System.Text.RegularExpressions;

namespace Miningcore.Blockchain.Ethereum;

public class EthereumConstants
{
    public const ulong EpochLength = 30000;
    public static BigInteger BigMaxValue = BigInteger.Pow(2, 256);
    public static double Pow2x32 = Math.Pow(2, 32);
    public static BigInteger BigPow2x32 = new(Pow2x32);
    public const int AddressLength = 20;
    public const decimal Wei = 1000000000000000000;
    public static BigInteger WeiBig = new(1000000000000000000);
    public const string EthereumStratumVersion = "EthereumStratum/1.0.0";
    public const decimal StaticTransactionFeeReserve = 0.0025m; // in ETH
    public const string BlockTypeUncle = "uncle";
    public const string BlockTypeBlock = "block";

#if !DEBUG
    public const int MinPayoutPeerCount = 1;
#else
    public const int MinPayoutPeerCount = 1;
#endif

    public static readonly Regex ValidAddressPattern = new("^0x[0-9a-fA-F]{40}$", RegexOptions.Compiled);
    public static readonly Regex ZeroHashPattern = new("^0?x?0+$", RegexOptions.Compiled);

    public const ulong ByzantiumHardForkHeight = 4370000;
    public const ulong ConstantinopleHardForkHeight = 7280000;
    public const decimal HomesteadBlockReward = 5.0m;
    public const decimal ByzantiumBlockReward = 3.0m;
    public const decimal ConstantinopleReward = 2.0m;

    public const int MinConfimations = 16;

    public const string RpcRequestWorkerPropertyName = "worker";
}

// ETC block reward distribution - ECIP 1017
// https://ecips.ethereumclassic.org/ECIPs/ecip-1017
public class EthereumClassicConstants
{
    public const ulong HardForkBlockMainnet = 11700000;
    public const ulong HardForkBlockMordor = 2520000;
    public const ulong EpochLength = 60000;
    public const ulong EraLength = 5000001;
    public const double DisinflationRateQuotient = 4.0;
    public const double DisinflationRateDivisor = 5.0;
    public const decimal BaseRewardInitial = 5.0m;
}

// Callisto Monetary Policy
// https://github.com/EthereumCommonwealth/Roadmap/issues/56
public class CallistoConstants
{
    public const decimal BaseRewardInitial = 77.76m;
    public const decimal TreasuryPercent = 50m;
}

public class EthOneConstants
{
    public const decimal BaseRewardInitial = 2.0m;
}

// OCTA block reward distribution -
// https://docs.octa.space/cryptocurrency/monetary-policy
public class OctaSpaceConstants
{
    public const ulong TriangulumHardForkHeight = 10000000;
    public const decimal TriangulumBlockReward = 1.0m;
    public const ulong VegaHardForkHeight = 8000000;
    public const decimal VegaBlockReward = 1.1m;
    public const ulong BlackeyeHardForkHeight = 6000000;
    public const decimal BlackeyeBlockReward = 1.2m;
    public const ulong DneprHardForkHeight = 4000000;
    public const decimal DneprBlockReward = 1.85m;
    public const ulong MahasimHardForkHeight = 3000000;
    public const decimal MahasimBlockReward = 2.3m;
    public const ulong PolarisHardForkHeight = 2500000;
    public const decimal PolarisBlockReward = 2.8m;
    public const ulong SpringwaterHardForkHeight = 2000000;
    public const decimal SpringwaterBlockReward = 3.0m;
    public const ulong ZagamiHardForkHeight = 1500000;
    public const decimal ZagamiBlockReward = 3.5m;
    public const ulong OldenburgHardForkHeight = 1000000;
    public const decimal OldenburgBlockReward = 4.0m;
    public const ulong ArcturusHardForkHeight = 650000;
    public const decimal ArcturusBlockReward = 5.0m;
    public const decimal BaseRewardInitial = 6.5m;
}

public class PinkConstants
{
    public const decimal BaseRewardInitial = 1.0m;
}

// Hypra
// https://github.com/Rethereum-blockchain/open-rethereum-pool/blob/master/payouts/unlocker.go
public class HypraConstants
{
    public const ulong EpochLength = 32000;
    public const ulong LondonHeight = 15787969;
    public const decimal LondonBlockReward = 3.0m;
    public const ulong ArrowGlacierHeight = 27200177;
    public const decimal ArrowGlacierBlockReward = 2.0m;
    public const ulong GrayGlacierHeight = 40725107;
    public const decimal GrayGlacierBlockReward = 1.0m;
    public const decimal BaseRewardInitial = 4.0m;
}

// UBIQ block reward distribution -
// https://github.com/ubiq/UIPs/issues/16 - https://ubiqsmart.com/en/monetary-policy
public class UbiqConstants
{
    public const ulong YearOneHeight = 358363;
    public const decimal YearOneBlockReward = 7.0m;
    public const ulong YearTwoHeight = 716727;
    public const decimal YearTwoBlockReward = 6.0m;
    public const ulong YearThreeHeight = 1075090;
    public const decimal YearThreeBlockReward = 5.0m;
    public const ulong YearFourHeight = 1433454;
    public const decimal YearFourBlockReward = 4.0m;
    public const ulong OrionHardForkHeight = 1791793;
    public const decimal OrionBlockReward = 1.5m;
    public const decimal BaseRewardInitial = 8.0m;
}

public enum EthereumNetworkType
{
    Main = 1,
    Ropsten = 3,
    Ubiq = 8,
    Classic = 1,
    Mordor = 7,
    Callisto = 820,
    MainPow = 10001,
    EtherOne = 4949,
    Pink = 10100,
    OctaSpace = 800001,
    OctaSpaceTestnet = 800002,
    Hypra = 622277,

    Unknown = -1,
}

public enum GethChainType
{
    Main,
    Ropsten,
    Ubiq,
    Classic,
    Mordor,
    Callisto,
    MainPow = 10001,
    EtherOne = 4949,
    Pink = 10100,
    OctaSpace,
    OctaSpaceTestnet,
    Hypra,

    Unknown = -1,
}

public static class EthCommands
{
    public const string GetWork = "eth_getWork";
    public const string SubmitWork = "eth_submitWork";
    public const string Sign = "eth_sign";
    public const string GetNetVersion = "net_version";
    public const string GetClientVersion = "web3_clientVersion";
    public const string GetCoinbase = "eth_coinbase";
    public const string GetAccounts = "eth_accounts";
    public const string GetPeerCount = "net_peerCount";
    public const string GetSyncState = "eth_syncing";
    public const string GetBlockNumber = "eth_blockNumber";
    public const string GetBlockByNumber = "eth_getBlockByNumber";
    public const string GetBlockByHash = "eth_getBlockByHash";
    public const string GetUncleByBlockNumberAndIndex = "eth_getUncleByBlockNumberAndIndex";
    public const string GetTxReceipt = "eth_getTransactionReceipt";
    public const string SendTx = "eth_sendTransaction";
    public const string UnlockAccount = "personal_unlockAccount";
    public const string Subscribe = "eth_subscribe";
    public const string MaxPriorityFeePerGas = "eth_maxPriorityFeePerGas";

        // Ajout de la constante GasPrice
    public const string GasPrice = "eth_gasPrice";
}


public class ZetherConstants
{
    // Définit les récompenses en fonction des plages de blocs
    public static decimal GetBlockReward(ulong blockHeight)
    {
        if (blockHeight <= 100_000)
            return 10_000m;
        else if (blockHeight <= 200_000)
            return 9_000m;
        else if (blockHeight <= 300_000)
            return 8_000m;
        else if (blockHeight <= 400_000)
            return 7_000m;
        else if (blockHeight <= 500_000)
            return 6_000m;
        else if (blockHeight <= 600_000)
            return 5_000m;
        else if (blockHeight <= 700_000)
            return 4_000m;
        else if (blockHeight <= 800_000)
            return 3_000m;
        else if (blockHeight <= 900_000)
            return 2_000m;
        else if (blockHeight <= 1_000_000)
            return 1_000m;
        else if (blockHeight <= 1_100_000)
            return 900m;
        else if (blockHeight <= 1_200_000)
            return 800m;
        else if (blockHeight <= 1_300_000)
            return 700m;
        else if (blockHeight <= 1_400_000)
            return 600m;
        else if (blockHeight <= 1_500_000)
            return 500m;
        else if (blockHeight <= 1_600_000)
            return 400m;
        else if (blockHeight <= 1_700_000)
            return 300m;
        else if (blockHeight <= 1_800_000)
            return 200m;
        else if (blockHeight <= 1_900_000)
            return 100m;
        else if (blockHeight <= 2_000_000)
            return 90m;
        else if (blockHeight <= 2_100_000)
            return 80m;
        else if (blockHeight <= 2_200_000)
            return 70m;
        else if (blockHeight <= 2_300_000)
            return 60m;
        else if (blockHeight <= 2_400_000)
            return 50m;
        else if (blockHeight <= 2_500_000)
            return 40m;
        else if (blockHeight <= 2_600_000)
            return 30m;
        else if (blockHeight <= 2_700_000)
            return 20m;
        else
            return 10m; // Récompense après le bloc 2 700 000
    }
}
