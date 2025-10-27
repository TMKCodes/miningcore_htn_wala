using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Native;
using CircularBuffer;
using System;
using NBitcoin;
using NLog;
using htnd = Miningcore.Blockchain.Hoosat.Htnd;

namespace Miningcore.Blockchain.Hoosat
{
    public class HoosatXoShiRo256PlusPlus
    {
        private ulong[] s = new ulong[4];

        public HoosatXoShiRo256PlusPlus(Span<byte> prePowHash)
        {
            Contract.Requires<ArgumentException>(prePowHash.Length >= 32);

            for (int i = 0; i < 4; i++)
            {
                s[i] = BitConverter.ToUInt64(prePowHash.Slice(i * 8, 8));
            }
        }

        public ulong Uint64()
        {
            ulong result = RotateLeft64(this.s[0] + this.s[3], 23) + this.s[0];
            ulong t = this.s[1] << 17;
            this.s[2] ^= this.s[0];
            this.s[3] ^= this.s[1];
            this.s[1] ^= this.s[2];
            this.s[0] ^= this.s[3];
            this.s[2] ^= t;
            this.s[3] = RotateLeft64(this.s[3], 45);
            return result;
        }

        private static ulong RotateLeft64(ulong value, int offset)
        {
            return (value << offset) | (value >> (64 - offset));
        }
    }


    public class State
{
    public ushort[][] Matrix { get; set; } = new ushort[64][];
    public long Timestamp { get; set; }
    public ulong Nonce { get; set; }
    public byte[] Target { get; set; } = new byte[32];
    public byte[] PrePowHash { get; set; } = new byte[32];

    public State()
    {
        // Initialisation des lignes de la matrice
        for (int i = 0; i < 64; i++)
        {
            Matrix[i] = new ushort[64];
        }
    }
}
    public class HoosatJob
    {
        protected IMasterClock clock;
        public htnd.RpcBlock BlockTemplate { get; private set; }
        public double Difficulty { get; private set; }
        public string JobId { get; protected set; }
        public uint256 blockTargetValue { get; protected set; }
        private object[] jobParams;
        private readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);

        private const double EPS = 1e-9;

    private readonly IHashAlgorithm blockHeaderHasher;
    private readonly IHashAlgorithm coinbaseHasher;
    private readonly IHashAlgorithm shareHasher;


        public HoosatJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
        {
            Contract.RequiresNonNull(customBlockHeaderHasher);

            Contract.RequiresNonNull(customCoinbaseHasher);

            Contract.RequiresNonNull(customShareHasher);

            this.blockHeaderHasher = customBlockHeaderHasher;
            this.coinbaseHasher = customCoinbaseHasher;
            this.shareHasher = customShareHasher;
        }

    protected virtual bool RegisterSubmit(string nonce)
    {
    var key = new StringBuilder().Append(nonce).ToString();

    bool result = submissions.TryAdd(key, true);

    return result;
    }


//New GenerateHoohashMatrix
public static double[][] GenerateHoohashMatrix(Span<byte> hash)
{
    var state = new HoosatXoShiRo256PlusPlus(hash);
    const double normalize = 1_000_000.0;
    double[][] mat = new double[64][];

    for (int i = 0; i < 64; i++)
    {
        mat[i] = new double[64];
        for (int j = 0; j < 64; j++)
        {
            ulong val = state.Uint64();
            uint lower4bytes = (uint)(val & 0xFFFFFFFF);
            mat[i][j] = (double)lower4bytes / uint.MaxValue * normalize;
        }
    }

    return mat;
}

public static double[,] ConvertTo2DArray(double[][] source)
{
    int rows = source.Length;
    int cols = source[0].Length;
    var result = new double[rows, cols];

    for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
            result[i, j] = source[i][j];

    return result;
}


// News
public static double MediumComplexNonLinear(double x)
{
    return Math.Exp(Math.Sin(x) + Math.Cos(x));
}

public static double IntermediateComplexNonLinear(double x)
{
    const double PI = Math.PI;

    if (x == PI / 2 || x == 3 * PI / 2)
        return 0; // Avoid singularity

    return Math.Sin(x) * Math.Cos(x) * Math.Tan(x);
}

public static double HighComplexNonLinear(double x)
{
    return 1.0 / Math.Sqrt(Math.Abs(x) + 1);
}

// New ComplexNonLinear

public static double ComplexNonLinear(double x)
{
    const double COMPLEX_TRANSFORM_MULTIPLIER = 0.000001;

    double transformFactorOne = (x * COMPLEX_TRANSFORM_MULTIPLIER) % 8 / 8;
    double transformFactorTwo = (x * COMPLEX_TRANSFORM_MULTIPLIER) % 4 / 4;

    if (transformFactorOne < 0.33)
    {
        if (transformFactorTwo < 0.25)
            return MediumComplexNonLinear(x + (1 + transformFactorTwo));
        else if (transformFactorTwo < 0.5)
            return MediumComplexNonLinear(x - (1 + transformFactorTwo));
        else if (transformFactorTwo < 0.75)
            return MediumComplexNonLinear(x * (1 + transformFactorTwo));
        else
            return MediumComplexNonLinear(x / (1 + transformFactorTwo));
    }
    else if (transformFactorOne < 0.66)
    {
        if (transformFactorTwo < 0.25)
            return IntermediateComplexNonLinear(x + (1 + transformFactorTwo));
        else if (transformFactorTwo < 0.5)
            return IntermediateComplexNonLinear(x - (1 + transformFactorTwo));
        else if (transformFactorTwo < 0.75)
            return IntermediateComplexNonLinear(x * (1 + transformFactorTwo));
        else
            return IntermediateComplexNonLinear(x / (1 + transformFactorTwo));
    }
    else
    {
        if (transformFactorTwo < 0.25)
            return HighComplexNonLinear(x + (1 + transformFactorTwo));
        else if (transformFactorTwo < 0.5)
            return HighComplexNonLinear(x - (1 + transformFactorTwo));
        else if (transformFactorTwo < 0.75)
            return HighComplexNonLinear(x * (1 + transformFactorTwo));
        else
            return HighComplexNonLinear(x / (1 + transformFactorTwo));
    }
}


public virtual void Init(htnd.RpcBlock blockTemplate, string jobId)
{
    Contract.RequiresNonNull(blockTemplate);
    Contract.RequiresNonNull(jobId);

    JobId = jobId;

    var target = new Target(HoosatUtils.CompactToBig(blockTemplate.Header.Bits));

    Difficulty = HoosatUtils.TargetToDifficulty(target.ToBigInteger()) * (double)HoosatConstants.MinHash / HoosatConstants.ShareMultiplier;

    blockTargetValue = target.ToUInt256();

    BlockTemplate = blockTemplate;

    var (largeJob, regularJob) = SerializeJobParamsData(SerializeHeader(blockTemplate.Header));

    jobParams = new object[]
    {
        JobId,
        largeJob + BitConverter.GetBytes(blockTemplate.Header.Timestamp).ToHexString().PadLeft(16, '0'),
        regularJob,
        blockTemplate.Header.Timestamp,
    };
}

protected virtual (string, ulong[]) SerializeJobParamsData(Span<byte> prePowHash, bool isLittleEndian = false)
{
    ulong[] preHashU64s = new ulong[4];
    string preHashStrings = "";

    for (int i = 0; i < 4; i++)
    {
        var slice = prePowHash.Slice(i * 8, 8).ToArray();

        var finalSlice = isLittleEndian ? slice.Reverse().ToArray() : slice;
        string hexString = BitConverter.ToString(finalSlice).Replace("-", "").PadLeft(16, '0');
        preHashStrings += hexString;

        ulong value = BitConverter.ToUInt64(finalSlice, 0);
        preHashU64s[i] = value;

    }
    return (preHashStrings, preHashU64s);
}

    protected virtual Span<byte> SerializeHeader(htnd.RpcBlockHeader header, bool isPrePow = true, bool isLittleEndian = false)
{
    ulong nonce = isPrePow ? 0 : header.Nonce;
    long timestamp = isPrePow ? 0 : header.Timestamp;
    Span<byte> hashBytes = stackalloc byte[32];

    using(var stream = new MemoryStream())
    {
        var versionBytes = (isLittleEndian) ? BitConverter.GetBytes((ushort) header.Version).ReverseInPlace() : BitConverter.GetBytes((ushort) header.Version);
        stream.Write(versionBytes);

        var parentsBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong) header.Parents.Count).ReverseInPlace() : BitConverter.GetBytes((ulong) header.Parents.Count);
        stream.Write(parentsBytes);

        foreach (var parent in header.Parents)
        {
            var parentHashesBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong) parent.ParentHashes.Count).ReverseInPlace() : BitConverter.GetBytes((ulong) parent.ParentHashes.Count);
            stream.Write(parentHashesBytes);

            foreach (var parentHash in parent.ParentHashes)
            {

            var parentHashBytes = parentHash.HexToByteArray();
            stream.Write(parentHashBytes);
            }
        }

    // Merkle roots and other fields
    stream.Write(header.HashMerkleRoot.HexToByteArray());

    stream.Write(header.AcceptedIdMerkleRoot.HexToByteArray());

    stream.Write(header.UtxoCommitment.HexToByteArray());

    // Timestamp
    var timestampBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong)timestamp).ReverseInPlace() : BitConverter.GetBytes((ulong)timestamp);
    stream.Write(timestampBytes);

    // Bits
    var bitsBytes = (isLittleEndian) ? BitConverter.GetBytes(header.Bits).ReverseInPlace() : BitConverter.GetBytes(header.Bits);
    stream.Write(bitsBytes);

    // Nonce
    var nonceBytes = (isLittleEndian) ? BitConverter.GetBytes(nonce).ReverseInPlace() : BitConverter.GetBytes(nonce);
    stream.Write(nonceBytes);

    // DaaScore
    var daaScoreBytes = (isLittleEndian) ? BitConverter.GetBytes(header.DaaScore).ReverseInPlace() : BitConverter.GetBytes(header.DaaScore);
    stream.Write(daaScoreBytes);

    // BlueScore
    var blueScoreBytes = (isLittleEndian) ? BitConverter.GetBytes(header.BlueScore).ReverseInPlace() : BitConverter.GetBytes(header.BlueScore);
    stream.Write(blueScoreBytes);

    // BlueWork
    var blueWork = header.BlueWork.PadLeft(header.BlueWork.Length + (header.BlueWork.Length % 2), '0');
    var blueWorkBytes = blueWork.HexToByteArray();

    var blueWorkLengthBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong)blueWorkBytes.Length).ReverseInPlace() : BitConverter.GetBytes((ulong)blueWorkBytes.Length);
    stream.Write(blueWorkLengthBytes);
    stream.Write(blueWorkBytes);

    // PruningPoint
    stream.Write(header.PruningPoint.HexToByteArray());

    // Calculating the hash
    byte[] streamBytes = stream.ToArray();
    blockHeaderHasher.Digest(streamBytes, hashBytes);

    return hashBytes.ToArray();
        }
    }

protected virtual Share ProcessShareInternal(StratumConnection worker, string nonce, string powHashStr)
{
    var context = worker.ContextAs<HoosatWorkerContext>();

    if (!ulong.TryParse(nonce, System.Globalization.NumberStyles.HexNumber, null, out ulong parsedNonce))
        throw new ArgumentException("Invalid nonce format");

    string nonceHex = parsedNonce.ToString("X").PadLeft(16, '0');

    byte[] nonceBytes = Enumerable.Range(0, nonceHex.Length / 2)
                                  .Select(x => Convert.ToByte(nonceHex.Substring(x * 2, 2), 16))
                                  .ToArray();
    Array.Reverse(nonceBytes);
    BigInteger littleEndianNonce = new BigInteger(nonceBytes, isUnsigned: true, isBigEndian: false);

    if (littleEndianNonce > ulong.MaxValue)
        throw new OverflowException("Nonce value exceeds the maximum value for ulong.");

    ulong finalNonce = (ulong)littleEndianNonce;

    var timestamp = BlockTemplate.Header.Timestamp;

    var prePowHash = SerializeHeader(BlockTemplate.Header).ToArray();

    var matrix = GenerateHoohashMatrix(prePowHash);

    var flatMatrix = ConvertTo2DArray(matrix);

    var proofOfWorkValue = new byte[32];
    HoohashNative.CalculateProofOfWork(prePowHash, (long)timestamp, finalNonce, flatMatrix, proofOfWorkValue);

    var powHashBytes = ConvertHexStringToByteArray(powHashStr);

    BigInteger submittedPowNum = new BigInteger(powHashBytes, isUnsigned: true, isBigEndian: false);
    BigInteger recalculatedPowNum = new BigInteger(proofOfWorkValue, isUnsigned: true, isBigEndian: false);

    if (submittedPowNum != recalculatedPowNum)
    {
        throw new StratumException(StratumError.Other, "Proof of Work hash is invalid");
    }

    var targetHashBigInteger = new BigInteger(proofOfWorkValue.Reverse().ToArray(), true, true);
    var blockTargetBigInteger = ConvertBitsToBigInteger(BlockTemplate.Header.Bits);

    var shareDiff = (double)new BigRational(HoosatConstants.Diff1b, targetHashBigInteger)
                    * HoosatConstants.Pow2xDiff1TargetNumZero
                    * (double)HoosatConstants.MinHash
                    / HoosatConstants.ShareMultiplier;

    var stratumDifficulty = context.Difficulty;
    var ratio = shareDiff / stratumDifficulty;

    var isBlockCandidate = targetHashBigInteger <= blockTargetBigInteger;

    if (!isBlockCandidate && ratio < 0.99)
    {
        if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
        {
            ratio = shareDiff / context.PreviousDifficulty.Value;

            if (ratio < 0.99)
            {
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            stratumDifficulty = context.PreviousDifficulty.Value;
        }
        else
        {
            throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }
    }

    BlockTemplate.Header.Nonce = finalNonce;

    var result = new Share
    {
        BlockHeight = (long)BlockTemplate.Header.DaaScore,
        NetworkDifficulty = Difficulty,
        Difficulty = context.Difficulty / HoosatConstants.ShareMultiplier,
    };

    if (isBlockCandidate)
    {
        var hashBytes = SerializeHeader(BlockTemplate.Header, false);
        result.IsBlockCandidate = true;
        result.BlockHash = hashBytes.ToHexString();
    }

    return result;
}

public virtual Share ProcessShare(StratumConnection worker, string nonce, string powHashStr)
{
    Contract.RequiresNonNull(worker);
    Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

    var context = worker.ContextAs<HoosatWorkerContext>();

    if (nonce.StartsWith("0x"))
    {
        nonce = nonce.Substring(2);
    }

    if (nonce.Length <= (HoosatConstants.NonceLength - context.ExtraNonce1.Length))
    {
        nonce = context.ExtraNonce1.PadRight(HoosatConstants.NonceLength - context.ExtraNonce1.Length, '0') + nonce;
    }

    if (!RegisterSubmit(nonce))
    {
        throw new StratumException(StratumError.DuplicateShare, "duplicate share");
    }

    var share = ProcessShareInternal(worker, nonce, powHashStr);

    return share;
}


private double CalculateShareDifficulty(BigInteger targetHashBigInteger)
{
    return (double)new BigRational(HoosatConstants.Diff1b, targetHashBigInteger)
        * HoosatConstants.Pow2xDiff1TargetNumZero
        * (double)HoosatConstants.MinHash / HoosatConstants.ShareMultiplier;
}

public object[] GetJobParams()
{

    if (jobParams != null && jobParams.Length > 0)
    {
        for (int i = 0; i < jobParams.Length; i++)
        {
            if (jobParams[i] is ulong[] ulongArray)
            {
                for (int j = 0; j < ulongArray.Length; j++)
                {
       //             Console.WriteLine($"Debug HoosatJob -----> GetJobParams --- jobParams[{i}][{j}]: {ulongArray[j]}");
                }
            }
        }
    }
    return jobParams;
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
    int exponent = (int)((bits >> 24) & 0xFF);
    BigInteger mantissa = bits & 0xFFFFFF;

    if ((mantissa & 0x800000) != 0)
        mantissa = mantissa >> 1;

    BigInteger target = mantissa * BigInteger.Pow(2, (8 * (exponent - 3)));
    return target;
}

}
}