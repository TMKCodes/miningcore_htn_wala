using System;
using System.Globalization;
using System.Numerics;
using System.Collections.Concurrent;
using System.Text;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

public class KaspaXoShiRo256PlusPlus
{
    private ulong[] s = new ulong[4];

    public KaspaXoShiRo256PlusPlus(Span<byte> prePowHash)
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

    private static ulong RotateRight64(ulong value, int offset)
    {
        return (value >> offset) | (value << (64 - offset));
    }
}

public class KaspaJob
{
    protected IMasterClock clock;
    public kaspad.RpcBlock BlockTemplate { get; protected set; }
    public double Difficulty { get; protected set; }
    public string JobId { get; protected set; }
    public uint256 blockTargetValue { get; protected set; }
    protected object[] jobParams;
    private readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);

    protected IHashAlgorithm blockHeaderHasher;
    protected IHashAlgorithm coinbaseHasher;
    protected IHashAlgorithm shareHasher;

    public KaspaJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
    {
        Console.WriteLine("elva Debug KaspaJob -----> Initializing KaspaJob...");

        Contract.RequiresNonNull(customBlockHeaderHasher);
        Contract.RequiresNonNull(customCoinbaseHasher);
        Contract.RequiresNonNull(customShareHasher);

        this.blockHeaderHasher = customBlockHeaderHasher;
        Console.WriteLine("elva Debug KaspaJob -----> BlockHeaderHasher initialized.");

        this.coinbaseHasher = customCoinbaseHasher;
        Console.WriteLine("elva Debug KaspaJob -----> CoinbaseHasher initialized.");

        this.shareHasher = customShareHasher;
        Console.WriteLine("elva Debug KaspaJob -----> ShareHasher initialized.");

        Console.WriteLine("elva Debug KaspaJob -----> KaspaJob initialization completed.");
    }

    protected bool RegisterSubmit(string nonce)
    {
        Console.WriteLine($"elva Debug KaspaJob -----> Registering submit for nonce: {nonce}");

        var key = new StringBuilder()
            .Append(nonce)
            .ToString();
        Console.WriteLine($"elva Debug KaspaJob -----> Generated key: {key}");

        bool result = submissions.TryAdd(key, true);
        Console.WriteLine(result ? "Nonce registered successfully." : "Nonce registration failed (duplicate detected).");

        return result;
    }

    protected virtual ushort[][] GenerateMatrix(Span<byte> prePowHash)
    {
        ushort[][] matrix = new ushort[64][];
        for (int i = 0; i < 64; i++)
        {
            matrix[i] = new ushort[64];
        }

        var generator = new KaspaXoShiRo256PlusPlus(prePowHash);
        while (true)
        {
            Console.WriteLine("elva Debug KaspaJob -----> Generating matrix...");

            for (int i = 0; i < 64; i++)
            {
                for (int j = 0; j < 64; j += 16)
                {
                    ulong val = generator.Uint64();
                    //            Console.WriteLine($"elva Debug KaspaJob -----> Generated value: {val}");

                    for (int shift = 0; shift < 16; shift++)
                    {
                        matrix[i][j + shift] = (ushort)((val >> (4 * shift)) & 0x0F);
                        //                 Console.WriteLine($"elva Debug KaspaJob -----> matrix[{i}][{j + shift}] = {matrix[i][j + shift]}");
                    }
                }
            }

            int rank = ComputeRank(matrix);
            Console.WriteLine($"elva Debug KaspaJob -----> Computed rank: {rank}");

            if (rank == 64)
            {
                Console.WriteLine("elva Debug KaspaJob -----> Matrix generated successfully with rank 64.");
                return matrix;
            }
            else
            {
                Console.WriteLine("elva Debug KaspaJob -----> Matrix rank not 64, regenerating...");
            }
        }
    }

    protected virtual int ComputeRank(ushort[][] matrix)
    {
        double Eps = 0.000000001;
        double[][] B = matrix.Select(row => row.Select(val => (double)val).ToArray()).ToArray();
        int rank = 0;
        bool[] rowSelected = new bool[64];

        Console.WriteLine("elva Debug KaspaJob -----> Starting rank computation...");

        for (int i = 0; i < 64; i++)
        {
            int j;
            for (j = 0; j < 64; j++)
            {
                if (!rowSelected[j] && Math.Abs(B[j][i]) > Eps)
                    break;
            }

            if (j != 64)
            {
                rank++;
                rowSelected[j] = true;

                double pivot = B[j][i];
                for (int p = i + 1; p < 64; p++)
                {
                    B[j][p] /= pivot;
                }

                for (int k = 0; k < 64; k++)
                {
                    if (k != j && Math.Abs(B[k][i]) > Eps)
                    {

                        for (int p = i + 1; p < 64; p++)
                        {
                            B[k][p] -= B[j][p] * B[k][i];
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"elva Debug KaspaJob -----> No pivot found for column {i}, continuing...");
            }
        }

        Console.WriteLine($"elva Debug KaspaJob -----> Rank computation completed. Final rank: {rank}");
        return rank;
    }

    protected virtual Span<byte> ComputeCoinbase(Span<byte> prePowHash, Span<byte> data)
    {
        Console.WriteLine("elva Debug KaspaJob -----> Starting ComputeCoinbase...");

        // Génération de la matrice
        ushort[][] matrix = GenerateMatrix(prePowHash);
        Console.WriteLine("elva Debug KaspaJob -----> Matrix generated.");

        // Initialisation des vecteurs
        ushort[] vector = new ushort[64];
        ushort[] product = new ushort[64];

        for (int i = 0; i < 32; i++)
        {
            vector[2 * i] = (ushort)(data[i] >> 4);
            vector[2 * i + 1] = (ushort)(data[i] & 0x0F);
        }

        // Calcul du produit matrice-vecteur
        for (int i = 0; i < 64; i++)
        {
            ushort sum = 0;
            for (int j = 0; j < 64; j++)
            {
                sum += (ushort)(matrix[i][j] * vector[j]);
            }
            product[i] = (ushort)(sum >> 10);
        }

        // Calcul du résultat final
        byte[] res = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            res[i] = (byte)(data[i] ^ ((byte)(product[2 * i] << 4) | (byte)product[2 * i + 1]));
        }

        Console.WriteLine("elva Debug KaspaJob -----> ComputeCoinbase completed.");

        return (Span<byte>)res;
    }

    protected virtual Span<byte> SerializeCoinbase(Span<byte> prePowHash, long timestamp, ulong nonce)
    {
        Console.WriteLine("elva Debug KaspaJob -----> SerializeCoinbase --- Starting SerializeCoinbase...");

        Span<byte> hashBytes = stackalloc byte[32];

        using (var stream = new MemoryStream())
        {
            // Écriture du prePowHash dans le stream
            stream.Write(prePowHash);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeCoinbase --- prePowHash: {BitConverter.ToString(prePowHash.ToArray())}");

            // Écriture du timestamp dans le stream
            byte[] timestampBytes = BitConverter.GetBytes((ulong)timestamp);
            stream.Write(timestampBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeCoinbase --- Timestamp: {timestamp}, Bytes: {BitConverter.ToString(timestampBytes)}");

            // Ajout de 32 octets de padding (zéro)
            byte[] padding = new byte[32];
            stream.Write(padding);
            Console.WriteLine("elva Debug KaspaJob -----> SerializeCoinbase --- 32 zero bytes added as padding.");

            // Écriture du nonce dans le stream
            byte[] nonceBytes = BitConverter.GetBytes(nonce);
            stream.Write(nonceBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeCoinbase --- Nonce: {nonce}, Bytes: {BitConverter.ToString(nonceBytes)}");

            // Calcul du digest
            byte[] streamBytes = stream.ToArray();
            coinbaseHasher.Digest(streamBytes, hashBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeCoinbase --- Serialized data: {BitConverter.ToString(streamBytes)}");
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeCoinbase --- Hash result: {BitConverter.ToString(hashBytes.ToArray())}");

            Console.WriteLine("elva Debug KaspaJob -----> SerializeCoinbase --- SerializeCoinbase completed.");

            return hashBytes.ToArray();
        }
    }

    protected virtual Span<byte> SerializeHeader(kaspad.RpcBlockHeader header, bool isPrePow = true, bool isLittleEndian = false)
    {
        Console.WriteLine("elva Debug KaspaJob -----> SerializeHeader --- Starting SerializeHeader...");

        ulong nonce = isPrePow ? 0 : header.Nonce;
        long timestamp = isPrePow ? 0 : header.Timestamp;
        Span<byte> hashBytes = stackalloc byte[32];

        using (var stream = new MemoryStream())
        {
            // Version
            var versionBytes = (isLittleEndian) ? BitConverter.GetBytes((ushort)header.Version).ReverseInPlace() : BitConverter.GetBytes((ushort)header.Version);
            stream.Write(versionBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  Version: {header.Version}, Bytes: {BitConverter.ToString(versionBytes)}");

            // Parents count
            var parentsBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong)header.Parents.Count).ReverseInPlace() : BitConverter.GetBytes((ulong)header.Parents.Count);
            stream.Write(parentsBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  Parents count: {header.Parents.Count}, Bytes: {BitConverter.ToString(parentsBytes)}");

            // Parents and their hashes
            foreach (var parent in header.Parents)
            {
                var parentHashesBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong)parent.ParentHashes.Count).ReverseInPlace() : BitConverter.GetBytes((ulong)parent.ParentHashes.Count);
                stream.Write(parentHashesBytes);

                foreach (var parentHash in parent.ParentHashes)
                {
                    var parentHashBytes = parentHash.HexToByteArray();
                    stream.Write(parentHashBytes);
                }
            }

            // Merkle roots and other fields
            stream.Write(header.HashMerkleRoot.HexToByteArray());
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  HashMerkleRoot: {header.HashMerkleRoot}");

            stream.Write(header.AcceptedIdMerkleRoot.HexToByteArray());
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  AcceptedIdMerkleRoot: {header.AcceptedIdMerkleRoot}");

            stream.Write(header.UtxoCommitment.HexToByteArray());
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  UtxoCommitment: {header.UtxoCommitment}");

            // Timestamp
            var timestampBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong)timestamp).ReverseInPlace() : BitConverter.GetBytes((ulong)timestamp);
            stream.Write(timestampBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  Timestamp: {timestamp}, Bytes: {BitConverter.ToString(timestampBytes)}");

            // Bits
            var bitsBytes = (isLittleEndian) ? BitConverter.GetBytes(header.Bits).ReverseInPlace() : BitConverter.GetBytes(header.Bits);
            stream.Write(bitsBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  Bits: {header.Bits}, Bytes: {BitConverter.ToString(bitsBytes)}");

            // Nonce
            var nonceBytes = (isLittleEndian) ? BitConverter.GetBytes(nonce).ReverseInPlace() : BitConverter.GetBytes(nonce);
            stream.Write(nonceBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  Nonce: {nonce}, Bytes: {BitConverter.ToString(nonceBytes)}");

            // DaaScore
            var daaScoreBytes = (isLittleEndian) ? BitConverter.GetBytes(header.DaaScore).ReverseInPlace() : BitConverter.GetBytes(header.DaaScore);
            stream.Write(daaScoreBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  DaaScore: {header.DaaScore}, Bytes: {BitConverter.ToString(daaScoreBytes)}");

            // BlueScore
            var blueScoreBytes = (isLittleEndian) ? BitConverter.GetBytes(header.BlueScore).ReverseInPlace() : BitConverter.GetBytes(header.BlueScore);
            stream.Write(blueScoreBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  BlueScore: {header.BlueScore}, Bytes: {BitConverter.ToString(blueScoreBytes)}");

            // BlueWork
            var blueWork = header.BlueWork.PadLeft(header.BlueWork.Length + (header.BlueWork.Length % 2), '0');
            var blueWorkBytes = blueWork.HexToByteArray();

            var blueWorkLengthBytes = (isLittleEndian) ? BitConverter.GetBytes((ulong)blueWorkBytes.Length).ReverseInPlace() : BitConverter.GetBytes((ulong)blueWorkBytes.Length);
            stream.Write(blueWorkLengthBytes);
            stream.Write(blueWorkBytes);
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  BlueWork: {header.BlueWork}, Length: {blueWorkBytes.Length}, Bytes: {BitConverter.ToString(blueWorkBytes)}");

            // PruningPoint
            stream.Write(header.PruningPoint.HexToByteArray());
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  PruningPoint: {header.PruningPoint}");

            // Calculating the hash
            byte[] streamBytes = stream.ToArray();
            blockHeaderHasher.Digest(streamBytes, hashBytes);
            // Console.WriteLine($"elva Debug KaspaJob -----> Serialized data: {BitConverter.ToString(streamBytes)}");
            Console.WriteLine($"elva Debug KaspaJob -----> SerializeHeader ---  Hash result: {BitConverter.ToString(hashBytes.ToArray())}");

            Console.WriteLine("elva Debug KaspaJob -----> SerializeHeader ---  SerializeHeader completed.");

            return hashBytes.ToArray();
        }
    }

    protected virtual (string, ulong[]) SerializeJobParamsData(Span<byte> prePowHash, bool isLittleEndian = false)
    {
        Console.WriteLine("elva Debug KaspaJob -----> SerializeJobParamsData --- Starting SerializeJobParamsData...");

        ulong[] preHashU64s = new ulong[4];
        string preHashStrings = "";

        for (int i = 0; i < 4; i++)
        {
            // Extraction de la tranche de 8 octets
            var slice = (isLittleEndian) ? prePowHash.Slice(i * 8, 8).ToNewReverseArray() : prePowHash.Slice(i * 8, 8);

            // Conversion de la tranche en chaîne hexadécimale et ajout à preHashStrings
            string hexString = slice.ToHexString().PadLeft(16, '0');
            preHashStrings += hexString;

            // Conversion de la tranche en UInt64
            preHashU64s[i] = BitConverter.ToUInt64(slice);
        }

        Console.WriteLine($"elva Debug KaspaJob -----> SerializeJobParamsData --- Final preHashStrings: {preHashStrings}");
        Console.WriteLine("elva Debug KaspaJob -----> SerializeJobParamsData --- SerializeJobParamsData completed.");

        return (preHashStrings, preHashU64s);
    }


    protected virtual Share ProcessShareInternal(StratumConnection worker, string nonce)
    {
        Console.WriteLine("elva Debug KaspaJob -----> ProcessShareInternal --- Starting ProcessShareInternal...");

        var context = worker.ContextAs<KaspaWorkerContext>();

        BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- Nonce set: {BlockTemplate.Header.Nonce}");

        var prePowHashBytes = SerializeHeader(BlockTemplate.Header, true);
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- prePowHashBytes: {BitConverter.ToString(prePowHashBytes.ToArray())}");

        var coinbaseBytes = SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce);
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- coinbaseBytes: {BitConverter.ToString(coinbaseBytes.ToArray())}");

        Span<byte> hashCoinbaseBytes = stackalloc byte[32];

        if (shareHasher is not FishHashKarlsen)
        {
            shareHasher.Digest(ComputeCoinbase(prePowHashBytes, coinbaseBytes), hashCoinbaseBytes);
            Console.WriteLine("elva Debug KaspaJob -----> ProcessShareInternal --- Computed hash using ComputeCoinbase.");
        }
        else
        {
            shareHasher.Digest(coinbaseBytes, hashCoinbaseBytes);
            Console.WriteLine("elva Debug KaspaJob -----> ProcessShareInternal --- Computed hash directly from coinbaseBytes.");
        }

        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- hashCoinbaseBytes: {BitConverter.ToString(hashCoinbaseBytes.ToArray())}");



        var targetHashCoinbaseBytes = new Target(new BigInteger(hashCoinbaseBytes.ToNewReverseArray(), true, true));
        var hashCoinbaseBytesValue = targetHashCoinbaseBytes.ToUInt256();
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- Target hash: {targetHashCoinbaseBytes.ToBigInteger()}");
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- hashCoinbaseBytesValue: {hashCoinbaseBytesValue}");
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- blockTargetValue: {blockTargetValue}");

        // Calculate share difficulty
        var shareDiff = (double)new BigRational(KaspaConstants.Diff1b, targetHashCoinbaseBytes.ToBigInteger()) * KaspaConstants.Pow2xDiff1TargetNumZero * (double)KaspaConstants.MinHash / KaspaConstants.ShareMultiplier;
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- shareDiff: {shareDiff}");

        // Difficulty check
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- stratumDifficulty: {stratumDifficulty}, ratio: {ratio}");

        // Check if the share meets the block difficulty (block candidate)
        var isBlockCandidate = hashCoinbaseBytesValue <= blockTargetValue;
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- isBlockCandidate: {isBlockCandidate}");

        // Test if the share meets at least the worker's current difficulty
        if (!isBlockCandidate && ratio < 0.99)
        {
            if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;
                Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- Ratio with previous difficulty: {ratio}");

                if (ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // Use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
                Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- Using previous difficulty: {stratumDifficulty}");
            }
            else
            {
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }
        }

        var result = new Share
        {
            BlockHeight = (long)BlockTemplate.Header.DaaScore,
            NetworkDifficulty = Difficulty,
            Difficulty = context.Difficulty / KaspaConstants.ShareMultiplier
        };

        if (isBlockCandidate)
        {
            var hashBytes = SerializeHeader(BlockTemplate.Header, false);
            Console.WriteLine($"elva Debug KaspaJob -----> ProcessShareInternal --- Block candidate detected. Block hash: {BitConverter.ToString(hashBytes.ToArray())}");

            result.IsBlockCandidate = true;
            result.BlockHash = hashBytes.ToHexString();
        }

        Console.WriteLine("elva Debug KaspaJob -----> ProcessShareInternal --- ProcessShareInternal completed.");
        return result;
    }


    public object[] GetJobParams()
    {
        //  Console.WriteLine("elva Debug KaspaJob -----> GetJobParams --- Getting job parameters...");

        if (jobParams != null && jobParams.Length > 0)
        {
            for (int i = 0; i < jobParams.Length; i++)
            {
                //       Console.WriteLine($"elva Debug KaspaJob -----> GetJobParams ---- jobParams[{i}]: {jobParams[i]}");
            }
        }
        else
        {
            //       Console.WriteLine("elva Debug KaspaJob -----> GetJobParams ---- jobParams is null or empty.");
        }

        return jobParams;
    }


    public virtual Share ProcessShare(StratumConnection worker, string nonce)
    {
        Console.WriteLine("elva Debug KaspaJob -----> ProcessShare --- Starting ProcessShare...");

        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShare --- Nonce received: {nonce}");

        var context = worker.ContextAs<KaspaWorkerContext>();

        // Supprimer le préfixe "0x" si présent
        nonce = (nonce.StartsWith("0x")) ? nonce.Substring(2) : nonce;
        Console.WriteLine($"elva Debug KaspaJob -----> ProcessShare --- Nonce after removing '0x': {nonce}");

        // Ajout de l'extranonce au nonce si activé et si le nonce soumis est plus court que prévu
        if (nonce.Length <= (KaspaConstants.NonceLength - context.ExtraNonce1.Length))
        {
            nonce = context.ExtraNonce1.PadRight(KaspaConstants.NonceLength - context.ExtraNonce1.Length, '0') + nonce;
            Console.WriteLine($"elva Debug KaspaJob -----> ProcessShare --- Nonce after adding ExtraNonce1: {nonce}");
        }

        // Vérification des duplications
        if (!RegisterSubmit(nonce))
        {
            Console.WriteLine("elva Debug KaspaJob -----> ProcessShare --- Duplicate share detected.");
            throw new StratumException(StratumError.DuplicateShare, $"duplicate share");
        }

        Console.WriteLine("elva Debug KaspaJob -----> ProcessShare --- Nonce is unique, proceeding with ProcessShareInternal...");
        var share = ProcessShareInternal(worker, nonce);

        Console.WriteLine("elva Debug KaspaJob -----> ProcessShare --- ProcessShare completed.");
        return share;
    }



    public virtual void Init(kaspad.RpcBlock blockTemplate, string jobId, double ShareMultiplier)
    {
        Console.WriteLine("elva Debug KaspaJob -----> Init --- Initializing job...");

        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(jobId);

        JobId = jobId;
        Console.WriteLine($"elva Debug KaspaJob -----> Init --- Job ID set: {JobId}");

        var target = new Target(KaspaUtils.CompactToBig(blockTemplate.Header.Bits));
        Console.WriteLine($"elva Debug KaspaJob -----> Init --- Target calculated from bits: {target.ToBigInteger()}");

        Difficulty = KaspaUtils.TargetToDifficulty(target.ToBigInteger()) * (double)KaspaConstants.MinHash / KaspaConstants.ShareMultiplier;
        Console.WriteLine($"elva Debug KaspaJob -----> Init --- Calculated Difficulty: {Difficulty}");

        blockTargetValue = target.ToUInt256();
        Console.WriteLine($"elva Debug KaspaJob -----> Init --- Block target value: {blockTargetValue}");
        Console.WriteLine($"elva Debug KaspaJob -----> Init --- Header Bits: {blockTemplate.Header.Bits:X}");

        BlockTemplate = blockTemplate;
        Console.WriteLine("elva Debug KaspaJob -----> Init --- BlockTemplate set.");

        var (largeJob, regularJob) = SerializeJobParamsData(SerializeHeader(blockTemplate.Header));
        //   Console.WriteLine($"elva Debug KaspaJob -----> Serialized job parameters: largeJob = {largeJob}, regularJob = [{string.Join(", ", regularJob)}]");

        jobParams = new object[]
        {
        JobId,
        largeJob + BitConverter.GetBytes(blockTemplate.Header.Timestamp).ToHexString().PadLeft(16, '0'),
        regularJob,
        blockTemplate.Header.Timestamp,
        };

        //    Console.WriteLine($"elva Debug KaspaJob -----> Job parameters set: JobId = {jobParams[0]}, Job = {jobParams[1]}, RegularJob = {jobParams[2]}, Timestamp = {jobParams[3]}");
        Console.WriteLine("elva Debug KaspaJob -----> Init --- Job initialization completed.");
    }

}
