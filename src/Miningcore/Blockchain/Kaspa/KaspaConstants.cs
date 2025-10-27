using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Miningcore.Configuration;
using NBitcoin;

// ReSharper disable InconsistentNaming

namespace Miningcore.Blockchain.Kaspa;


public static class BitmemeConstants
{
    // List of HTN prefixes: https://github.com/bitmeme-taxi/bitmemed/blob/main/util/address.go
    public const string ChainPrefixDevnet = "btmdev";
    public const string ChainPrefixSimnet = "btmsim";
    public const string ChainPrefixTestnet = "btmtest";
    public const string ChainPrefixMainnet = "btm";
}

public static class KobradagConstants
{
    // List of Kobra prefixes: https://github.com/kobradag/kobrad/blob/master/util/address.go
    public const string ChainPrefixDevnet = "kobradev";
    public const string ChainPrefixSimnet = "kobrasim";
    public const string ChainPrefixTestnet = "kobratest";
    public const string ChainPrefixMainnet = "kobra";
}

public static class KaspaConstants
{
    public const string WalletDaemonCategory = "wallet";

    public const int Diff1TargetNumZero = 31;
   // public static readonly BigInteger Diff1b = BigInteger.Parse("00ffff0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public static readonly BigInteger Diff1b = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    public static BigInteger Diff1 = BigInteger.Pow(2, 256);
    public static BigInteger Diff1Target = BigInteger.Pow(2, 255) - 1;
    public static readonly double Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
  //  public static BigInteger BigOne = BigInteger.One;
  //  public static BigInteger OneLsh256 = BigInteger.One << 256;
    public static BigInteger MinHash = BigInteger.Divide(Diff1, Diff1Target);
  //  public static BigInteger BigGig = BigInteger.Pow(10, 9);
    public const int ExtranoncePlaceHolderLength = 8;
    public static int NonceLength = 16;


    public const uint ShareMultiplier = 1;



    // KAS smallest unit is called SOMPI: https://github.com/kaspanet/kaspad/blob/master/util/amount.go
    public const decimal SmallestUnit = 100000000;

    // List of KAS prefixes: https://github.com/kaspanet/kaspad/blob/master/util/address.go

    public static readonly Regex RegexUserAgentBzMiner = new("bzminer", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RegexUserAgentIceRiverMiner = new("iceriverminer", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RegexUserAgentGodMiner = new("godminer", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RegexUserAgentGoldShell = new("(goldshell|bzminer)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RegexUserAgentTNNMiner = new("tnn-miner", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string CoinbaseBlockHash = "BlockHash";
    public const string CoinbaseProofOfWorkHash = "ProofOfWorkHash";
    public const string CoinbaseHeavyHash = "HeavyHash";

    public const string ProtobufDaemonRpcServiceName = "protowire.RPC";
    public const string ProtobufWalletRpcServiceName = "kaspawalletd.kaspawalletd";

    public const byte PubKeyAddrID = 0x00;
    public const byte PubKeyECDSAAddrID = 0x01;
    public const byte ScriptHashAddrID = 0x08;
    public static readonly Dictionary<byte, string> KaspaAddressType = new Dictionary<byte, string>
    {
        { PubKeyAddrID, "Public Key Address" },
        { PubKeyECDSAAddrID, "Public Key ECDSA Address" },
        { ScriptHashAddrID, "Script Hash Address" },
    };
    public const int PublicKeySize = 32;
    public const int PublicKeySizeECDSA = 33;
    public const int Blake2bSize256 = 32;
}

public static class NautilusConstants
{
    // List of NTL prefixes: https://github.com/Nautilus-Network/nautiliad/blob/master/util/address.go
    public const string ChainPrefixDevnet = "nautiliadev";
    public const string ChainPrefixSimnet = "nautilussim";
    public const string ChainPrefixTestnet = "nautilustest";
    public const string ChainPrefixMainnet = "nautilus";
}

public static class ConsensusNetworkConstants
{
    // List of CSS prefixes: https://github.com/consensus-network/consensusd/blob/main/util/address.go
    public const string ChainPrefixDevnet = "consensusdev";
    public const string ChainPrefixSimnet = "consensussim";
    public const string ChainPrefixTestnet = "consensustest";
    public const string ChainPrefixMainnet = "consensus";

}

public static class NexelliaConstants
{
    // List of NXL prefixes: https://github.com/Nexellia-Network/nexelliad/blob/master/util/address.go
    public const string ChainPrefixDevnet = "nexelliadev";
    public const string ChainPrefixSimnet = "nexelliasim";
    public const string ChainPrefixTestnet = "nexelliatest";
    public const string ChainPrefixMainnet = "nexellia";
}

public static class PyrinConstants
{
    public const ulong Blake3ForkHeight = 1484741;
}

public static class SedraCoinConstants
{
    // List of HTN prefixes: https://github.com/sedracoin/sedrad/blob/main/util/address.go
    public const string ChainPrefixDevnet = "sedradev";
    public const string ChainPrefixSimnet = "sedrasim";
    public const string ChainPrefixTestnet = "sedratest";
    public const string ChainPrefixMainnet = "sedra";
}

public static class BricsCoinConstants
{
    // List of HTN prefixes: https://github.com/sedracoin/sedrad/blob/main/util/address.go
    public const string ChainPrefixDevnet = "bricsdev";
    public const string ChainPrefixSimnet = "bricssim";
    public const string ChainPrefixTestnet = "bricstest";
    public const string ChainPrefixMainnet = "brics";
}

public static class KaspaV2CoinConstants
{
    // List of HTN prefixes: https://github.com/sedracoin/sedrad/blob/main/util/address.go
    public const string ChainPrefixDevnet = "kasv2dev";
    public const string ChainPrefixSimnet = "kasv2sim";
    public const string ChainPrefixTestnet = "kasv2test";
    public const string ChainPrefixMainnet = "kasv2";
}

public static class AnumaCoinConstants
{
    public const string ChainPrefixDevnet = "anumadev";
    public const string ChainPrefixSimnet = "anumasim";
    public const string ChainPrefixTestnet = "anumatest";
    public const string ChainPrefixMainnet = "anuma";
}

public static class KarlsencoinConstants
{
    public const ulong FishHashForkHeightTestnet = 0;
    public const ulong FishHashPlusForkHeightTestnet = 43200;
    public const ulong FishHashPlusForkHeightMainnet = 26962009;
}

public static class SpectreConstants
{
    public const int Diff1TargetNumZero = 7;
    public static readonly BigInteger Diff1b = BigInteger.Parse("00ffff0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public static readonly double Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
    public static BigInteger MinHash = BigInteger.One;

    public const uint ShareMultiplier = 256;
}

public static class WaglaylaConstants
{
    public const int Diff1TargetNumZero = 7;
     public static readonly BigInteger Diff1b = BigInteger.Parse("0000000000ffff00000000000000000000000000000000000000000000000000", NumberStyles.HexNumber); //Mainnet ok 30 nov
    public static readonly double Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
    public static BigInteger MinHash = BigInteger.One;
    public const uint ShareMultiplier = 1; //Maintnet ok 30Nov
}

public static class KobraConstants
{
    public const int Diff1TargetNumZero = 7;
    //public static readonly BigInteger Diff1b = BigInteger.Parse("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
    public static readonly BigInteger Diff1b = BigInteger.Parse("00ffff0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public static readonly double Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
    public static BigInteger MinHash = BigInteger.One;
}

public enum KaspaBech32Prefix
{
    Unknown = 0,
    KaspaMain,
    KaspaDev,
    KaspaTest,
    KaspaSim
}

