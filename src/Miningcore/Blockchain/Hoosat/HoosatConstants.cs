using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

// ReSharper disable InconsistentNaming

namespace Miningcore.Blockchain.Hoosat;

public static class HoosatConstants
{
    public const string WalletDaemonCategory = "wallet";

    public const int Diff1TargetNumZero = 31;
    public static readonly BigInteger Diff1b = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public static BigInteger Diff1 = BigInteger.Pow(2, 256);
    public static BigInteger Diff1Target = BigInteger.Pow(2, 255) - 1;
    public static readonly double Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
    public static BigInteger BigOne = BigInteger.One;
    public static BigInteger OneLsh256 = BigInteger.One << 256;
    public static BigInteger MinHash = BigInteger.Divide(Diff1, Diff1Target);
    public static BigInteger BigGig = BigInteger.Pow(10, 9);
    public const int ExtranoncePlaceHolderLength = 8;
    public static int NonceLength = 16;
    public const uint ShareMultiplier = 1;

    // KAS smallest unit is called SOMPI: https://github.com/kaspanet/kaspad/blob/master/util/amount.go
    public const decimal SmallestUnit = 100000000;

    // List of KAS prefixes: https://github.com/kaspanet/kaspad/blob/master/util/address.go
    public const string ChainPrefixDevnet = "htndev";
    public const string ChainPrefixSimnet = "hoosatsim";
    public const string ChainPrefixTestnet = "hoosattest";
    public const string ChainPrefixMainnet = "hoosat";

    public static readonly Regex RegexUserAgentBzMiner = new("bzminer", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RegexUserAgentIceRiverMiner = new("iceriverminer", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RegexUserAgentGodMiner = new("godminer", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string CoinbaseBlockHash = "BlockHash";
    public const string CoinbaseProofOfWorkHash = "ProofOfWorkHash";
    public const string CoinbaseHoohash = "Hoohash";

    public const string ProtobufDaemonRpcServiceName = "protowire.RPC";
    public const string ProtobufWalletRpcServiceName = "htnwalletd.htnwalletd";

    public const byte PubKeyAddrID = 0x00;
    public const byte PubKeyECDSAAddrID = 0x01;
    public const byte ScriptHashAddrID = 0x08;
    public static readonly Dictionary<byte, string> HoosatAddressType = new Dictionary<byte, string>
    {
        { PubKeyAddrID, "Public Key Address" },
        { PubKeyECDSAAddrID, "Public Key ECDSA Address" },
        { ScriptHashAddrID, "Script Hash Address" },
    };
    public const int PublicKeySize = 32;
    public const int PublicKeySizeECDSA = 33;
    public const int Blake2bSize256 = 32;
}

public enum HoosatBech32Prefix
{
    Unknown = 0,
    HoosatMain,
    HoosatDev,
    HoosatTest,
    HoosatSim
}
