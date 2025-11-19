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

namespace Miningcore.Blockchain.Kaspa.Custom.Waglayla;


public class XoShiRo256PlusPlus
{
    private ulong _s0, _s1, _s2, _s3;

    // Constructeur
    public XoShiRo256PlusPlus(Span<byte> hash)
    {
        if (hash.Length != 32)
            throw new ArgumentException("Hash must be 32 bytes long.");

        _s0 = BitConverter.ToUInt64(hash.Slice(0, 8));
        _s1 = BitConverter.ToUInt64(hash.Slice(8, 8));
        _s2 = BitConverter.ToUInt64(hash.Slice(16, 8));
        _s3 = BitConverter.ToUInt64(hash.Slice(24, 8));
    }

    // Génère un nombre pseudo-aléatoire de 64 bits
    public ulong Uint64()
    {
        ulong result = _s0 + RotateLeft(_s0 + _s3, 23);

        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;

        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);

        return result;
    }

    // Fonction utilitaire : rotation circulaire vers la gauche
    private static ulong RotateLeft(ulong value, int shift)
    {
        return (value << shift) | (value >> (64 - shift));
    }
}

public static class Uint256Extensions
{
    public static byte[] ToByteArray(this uint256 value, bool isUnsigned, bool isBigEndian)
    {
        var bytes = new byte[32];
        value.ToBytes(bytes); // Remplit le tableau de bytes
        return bytes;
    }
    public static byte[] ToNewReverseArray(this uint256 value)
    {
        // Convert uint256 to a byte array with explicit parameters for unsigned and endianness
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        // Reverse the array to switch endianness
        Array.Reverse(bytes);

        // Return the reversed array
        return bytes;
    }
}

public static class ByteArrayExtensions
{
    public static byte[] ToNewReverseArray(this byte[] array)
    {
        if (array == null || array.Length == 0)
            throw new ArgumentException("Array cannot be null or empty");

        var reversedArray = (byte[])array.Clone();
        Array.Reverse(reversedArray);
        return reversedArray;
    }
}


public class WaglaylaJob : KaspaJob
{
    protected Blake3 blake3Hasher;
    protected Sha3_256 sha3_256Hasher;

    public WaglaylaJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
        : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
        // Initialize any specific hashers or components
        blake3Hasher = new Blake3();
        sha3_256Hasher = new Sha3_256();

        Console.WriteLine("WaglaylaJob initialized with custom hashers.");
    }


    protected override ushort[][] GenerateMatrix(Span<byte> prePowHash)
    {
        // Utilisation d'un tableau rectangulaire pour les performances
        ushort[,] rectMatrix = new ushort[64, 64];
        var generator = new XoShiRo256PlusPlus(prePowHash);

        int maxAttempts = 1000; // Limite pour éviter une boucle infinie
        while (maxAttempts-- > 0)
        {
            // Remplissage de la matrice
            for (int i = 0; i < 64; i++)
            {
                for (int j = 0; j < 64; j += 16)
                {
                    ulong val = generator.Uint64();
                    for (int shift = 0; shift < 16; shift++)
                        rectMatrix[i, j + shift] = (ushort)((val >> (4 * shift)) & 0x0F);
                }
            }

            // Vérification du rang
            if (ComputeRank(rectMatrix) == 64)
                return ConvertToJagged(rectMatrix); // Conversion en tableau jagged
        }

        throw new InvalidOperationException("Failed to generate a matrix of rank 64 after 1000 attempts.");
    }

    // Méthode pour convertir un tableau rectangulaire en tableau jagged
    private static ushort[][] ConvertToJagged(ushort[,] rectMatrix)
    {
        int rows = rectMatrix.GetLength(0);
        int cols = rectMatrix.GetLength(1);
        ushort[][] jaggedMatrix = new ushort[rows][];

        for (int i = 0; i < rows; i++)
        {
            jaggedMatrix[i] = new ushort[cols];
            for (int j = 0; j < cols; j++)
            {
                jaggedMatrix[i][j] = rectMatrix[i, j];
            }
        }

        return jaggedMatrix;
    }

    // Méthode ComputeRank adaptée pour ushort[,]
    public virtual int ComputeRank(ushort[,] matrix)
    {
        const ushort MODULO = 1024; // Utilisé si une réduction modulo est nécessaire pour éviter les dépassements
        ushort[,] matCopy = (ushort[,])matrix.Clone();
        bool[] rowSelected = new bool[64];
        int rank = 0;

        for (int i = 0; i < 64; i++)
        {
            int j = 0;
            while (j < 64 && (rowSelected[j] || matCopy[j, i] == 0))
                j++;

            if (j < 64)
            {
                rank++;
                rowSelected[j] = true;

                for (int p = i + 1; p < 64; p++)
                    matCopy[j, p] /= matCopy[j, i];

                for (int k = 0; k < 64; k++)
                {
                    if (k != j && matCopy[k, i] > 0)
                    {
                        for (int p = i + 1; p < 64; p++)
                            matCopy[k, p] -= (ushort)((matCopy[j, p] * matCopy[k, i]) % MODULO);
                    }
                }
            }
        }

        return rank;
    }







    protected override Span<byte> ComputeCoinbase(Span<byte> prePowHash, Span<byte> data)
    {
        // Utilisation de ushort[][] retourné par GenerateMatrix
        ushort[][] matrix = GenerateMatrix(prePowHash);

        ushort[] vector = new ushort[64];
        ushort[] product = new ushort[64];

        // Décomposer `data` en un vecteur de 64 éléments (4 bits chacun)
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
                sum += (ushort)(matrix[i][j] * vector[j]); // Notez l'accès avec [i][j] pour le tableau jagged

            product[i] = (ushort)(sum >> 10); // Réduction à 6 bits
        }

        // Recombinaison et XOR
        byte[] res = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            byte combined = (byte)((product[2 * i] << 4) | product[2 * i + 1]); // Combine 2 x 4 bits en 1 octet
            res[i] = (byte)(data[i] ^ combined); // XOR avec les données originales
        }

        return res;
    }








    public virtual byte[] CalculateProofOfWorkValue(Span<byte> prePowHash, ulong timestamp, ulong nonce)
    {
        Console.WriteLine("elva Debug WaglaylaJob -----> CalculateProofOfWorkValue ----> Starting calculation.");

        // Étape 1 : Concaténation des données (pré-PoW, timestamp, padding, nonce)
        using var stream = new MemoryStream();
        stream.Write(prePowHash.ToArray()); // Conversion Span<byte> en byte[] pour MemoryStream
        stream.Write(BitConverter.GetBytes(timestamp)); // TIME
        stream.Write(new byte[32]); // 32 zero byte padding
        stream.Write(BitConverter.GetBytes(nonce)); // NONCE
        var powHashBytes = stream.ToArray();

        Console.WriteLine($"elva Debug WaglaylaJob -----> CalculateProofOfWorkValue ----> PreHash: {BitConverter.ToString(powHashBytes)}");

        // Étape 2 : Calcul SHA3-256
        Span<byte> sha3_256Bytes = stackalloc byte[32];
        var sha3Hasher = new Sha3_256();
        sha3Hasher.Digest(powHashBytes, sha3_256Bytes);

        Console.WriteLine($"elva Debug WaglaylaJob -----> CalculateProofOfWorkValue ----> SHA3-256 Hash: {BitConverter.ToString(sha3_256Bytes.ToArray())}");

        // Étape 3 : Calcul du HeavyHash
        Span<byte> heavyHashBytes = stackalloc byte[32];
        var heavyHasher = new HeavyHash();
        heavyHasher.Digest(sha3_256Bytes, heavyHashBytes);

        Console.WriteLine($"elva Debug WaglaylaJob -----> CalculateProofOfWorkValue ----> HeavyHash: {BitConverter.ToString(heavyHashBytes.ToArray())}");

        // Étape 4 : Conversion en BigInteger
        var resultBigInt = new BigInteger(heavyHashBytes.ToArray().Reverse().ToArray(), true);
        Console.WriteLine($"elva Debug WaglaylaJob -----> CalculateProofOfWorkValue ----> Result (BigInt): {resultBigInt}");

        // Retourner le HeavyHash sous forme de byte[]
        return heavyHashBytes.ToArray();
    }



    protected override Span<byte> SerializeHeader(kaspad.RpcBlockHeader header, bool isPrePow = true, bool isLittleEndian = true)
    {
        Console.WriteLine("elva Debug WALAJob -----> SerializeHeader --- Starting SerializeHeader...");

        // Initialisation de Blake3 avec une clé fixe "BlockHash"
        Span<byte> fixedSizeKey = stackalloc byte[32];
        Encoding.UTF8.GetBytes("BlockHash").CopyTo(fixedSizeKey);

        var blake3Hasher = new Blake3(fixedSizeKey.ToArray());
        Span<byte> hashBytes = stackalloc byte[32];

        using (var stream = new MemoryStream())
        {
            // Écriture de la version (toujours Little Endian)
            var versionBytes = BitConverter.GetBytes((ushort)header.Version);
            stream.Write(versionBytes);
            Console.WriteLine($"elva Debug WALAJob -----> SerializeHeader --- Version: {header.Version}");

            // Écriture du nombre de parents (toujours Little Endian)
            var parentsCountBytes = BitConverter.GetBytes((ulong)header.Parents.Count);
            stream.Write(parentsCountBytes);
            Console.WriteLine($"elva Debug WALAJob -----> SerializeHeader --- Parents Count: {header.Parents.Count}");

            // Écriture des parents et de leurs hashes
            foreach (var parent in header.Parents)
            {
                var parentHashesCountBytes = BitConverter.GetBytes((ulong)parent.ParentHashes.Count);
                stream.Write(parentHashesCountBytes);

                foreach (var parentHash in parent.ParentHashes)
                {
                    var parentHashBytes = parentHash.HexToByteArray();
                    stream.Write(parentHashBytes);
                }
            }

            // Écriture des Merkle roots et des autres champs
            stream.Write(header.HashMerkleRoot.HexToByteArray());
            stream.Write(header.AcceptedIdMerkleRoot.HexToByteArray());
            stream.Write(header.UtxoCommitment.HexToByteArray());

            Console.WriteLine("elva Debug WALAJob -----> SerializeHeader --- Merkle roots serialized.");

            // Écriture des données additionnelles (Timestamp, Bits, Nonce, DaaScore, BlueScore)
            ulong timestamp = isPrePow ? 0 : (ulong)header.Timestamp;
            ulong nonce = isPrePow ? 0 : header.Nonce;

            stream.Write(BitConverter.GetBytes(timestamp));
            // Écriture de header.Bits dans le flux
            stream.Write(BitConverter.GetBytes(header.Bits));

            // Affichage de header.Bits dans un format lisible
            Console.WriteLine($"elva Debug WALAJob -----> SerializeHeader --- header.Bits {BitConverter.ToString(BitConverter.GetBytes(header.Bits))}");


            stream.Write(BitConverter.GetBytes(nonce));
            stream.Write(BitConverter.GetBytes(header.DaaScore));
            stream.Write(BitConverter.GetBytes(header.BlueScore));

            Console.WriteLine("elva Debug WALAJob -----> SerializeHeader --- Additional fields serialized.");

            // Gestion de BlueWork avec padding (toujours Little Endian)
            var blueWork = header.BlueWork.PadLeft(header.BlueWork.Length + (header.BlueWork.Length % 2), '0');
            var blueWorkBytes = blueWork.HexToByteArray();

            var blueWorkLengthBytes = BitConverter.GetBytes((ulong)blueWorkBytes.Length);
            stream.Write(blueWorkLengthBytes);
            stream.Write(blueWorkBytes);

            Console.WriteLine($"elva Debug WALAJob -----> SerializeHeader --- BlueWork serialized. Length: {blueWorkBytes.Length}");

            // Écriture du PruningPoint
            stream.Write(header.PruningPoint.HexToByteArray());
            Console.WriteLine("elva Debug WALAJob -----> SerializeHeader --- PruningPoint serialized.");

            // Finalisation du hash
            var serializedData = stream.ToArray();
            blake3Hasher.Digest(serializedData, hashBytes);

            Console.WriteLine($"elva Debug WALAJob -----> SerializeHeader --- Hash Result: {BitConverter.ToString(hashBytes.ToArray())}");
        }

        Console.WriteLine("elva Debug WALAJob -----> SerializeHeader --- SerializeHeader completed.");
        return hashBytes.ToArray();
    }

    protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
    {
        Console.WriteLine("elva Debug WaglaylaJob -----> ProcessShareInternal --- Starting ProcessShareInternal...");

        // 1. Contexte du worker
        var context = worker.ContextAs<KaspaWorkerContext>();
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Worker Context: {context}");

        // 2. Préparer le header pour calculer le prePowHash
        BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Nonce set: {BlockTemplate.Header.Nonce}");

        var prePowHashBytes = SerializeHeader(BlockTemplate.Header, isPrePow: true);
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- prePowHashBytes: {BitConverter.ToString(prePowHashBytes.ToArray())}");

        // 3. Calculer la preuve de travail
        var timestamp = (ulong)BlockTemplate.Header.Timestamp;
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Timestamp: {timestamp}");

        var powHash = CalculateProofOfWorkValue(prePowHashBytes, timestamp, BlockTemplate.Header.Nonce);
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Proof of Work Hash: {BitConverter.ToString(powHash)}");


        // Calcul du hash Coinbase
        Span<byte> hashCoinbaseBytes = stackalloc byte[32];
        var hasher = new Blake3();
        var coinbaseBytes = SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce);
        hasher.Digest(coinbaseBytes, hashCoinbaseBytes);
        Console.WriteLine($"[DEBUG] Hash de la coinbase : {BitConverter.ToString(hashCoinbaseBytes.ToArray())}");

        var targetHashCoinbaseBytes = new Target(new BigInteger(hashCoinbaseBytes.ToNewReverseArray(), true, true));
        var hashCoinbaseBytesValue = targetHashCoinbaseBytes.ToUInt256();
        Console.WriteLine($"[DEBUG] targetHashCoinbaseBytes {targetHashCoinbaseBytes} / Hash Coinbase en BigInteger : {hashCoinbaseBytesValue}");

        var powHashBytes = new Target(new BigInteger(powHash.ToNewReverseArray(), true, true));

        var powHashBytesValue = powHashBytes.ToUInt256();
        Console.WriteLine($"[DEBUG] Hash powHashBytesValue en BigInteger : {powHashBytesValue}");


        // 4. Vérification de la difficulté
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- blockTargetValue : {blockTargetValue}");

        // Conversion de powHash en uint256
        var powHashUint256 = new uint256(powHash);
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- powHashUint256: {powHashUint256}");




        // powHashUint256 = powHashBytesValue;




        // Conversion de blockTargetValue en BigInteger
        var blockTargetValueBigInt = new BigInteger(blockTargetValue.ToByteArray(isUnsigned: true, isBigEndian: true));
        Console.WriteLine($"[DEBUG] blockTargetValue en BigInteger : {blockTargetValueBigInt}");

        // Conversion de powHashBytesValue en BigInteger
        //   var powHashBytesValueBigInt = new BigInteger(powHashBytesValue.ToNewReverseArray(), isUnsigned: true);
        var powHashBytesValueBigInt = new BigInteger(hashCoinbaseBytesValue.ToNewReverseArray(), isUnsigned: true);


        Console.WriteLine($"[DEBUG] powHashBytesValue en BigInteger : {powHashBytesValueBigInt}");

        // Comparaison des deux valeurs normalisées
        var isValidShare = powHashBytesValueBigInt <= blockTargetValueBigInt;
        Console.WriteLine($"[DEBUG] Validité du partage : {isValidShare}");



        // Vérifier si powHash est valide (inférieur ou égal à blockTargetValue)
        //  var isValidShare = powHashUint256 <= blockTargetValue;
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Share Validity: {isValidShare}");

        if (!isValidShare)
        {
            Console.WriteLine("elva Debug WaglaylaJob -----> ProcessShareInternal --- Invalid share, does not meet block target.");
            //    throw new StratumException(StratumError.LowDifficultyShare, "Invalid share, does not meet block target.");
        }

        // 5. Calcul de la difficulté du share
        var powHashBigInt = new BigInteger(powHash.Reverse().ToArray(), isUnsigned: true);
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- powHash (BigInteger): {powHashBigInt}");

        // var numerator = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        // var shareDifficulty = CalculateShareDifficulty(powHashBigInt, numerator);

        var shareDifficulty0 = (double)new BigRational(WaglaylaConstants.Diff1b, targetHashCoinbaseBytes.ToBigInteger());
        //     var shareDifficulty = (double) new BigRational(SpectreConstants.Diff1b, targetHashCoinbaseBytes.ToBigInteger()) * 256;

        // Calcul de la difficulté du share
        var shareDifficulty = (double)new BigRational(WaglaylaConstants.Diff1b, blockTargetValueBigInt)
                        * WaglaylaConstants.Pow2xDiff1TargetNumZero
                        * (double)WaglaylaConstants.MinHash
                        / WaglaylaConstants.ShareMultiplier;


        // var shareDifficulty = CalculateShareDifficulty(powHashBigInt, KaspaConstants.Diff1b);
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Share Difficulty: {shareDifficulty}");


        var stratumDifficulty = context.Difficulty;
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Stratum Difficulty: {stratumDifficulty}");




        /* Mainnet
            if (shareDifficulty < stratumDifficulty * 0.99)
            {
                Console.WriteLine("elva Debug WaglaylaJob -----> ProcessShareInternal --- Share Difficulty too low.");
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share (ShareDifficulty: {shareDifficulty}, StratumDifficulty: {stratumDifficulty})"); // Désactivé pour TestNet
            }
        */




        // 6. Bloc candidat
        var isBlockCandidate = powHashBytesValueBigInt <= blockTargetValueBigInt;
        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Is Block Candidate: {isBlockCandidate}");

        // 7. Résultat
        var share = new Share
        {
            BlockHeight = (long)BlockTemplate.Header.DaaScore,
            NetworkDifficulty = Difficulty,
            //    Difficulty = shareDifficulty / WaglaylaConstants.ShareMultiplier,
            Difficulty = context.Difficulty / WaglaylaConstants.ShareMultiplier,
            IsBlockCandidate = isBlockCandidate,
        };

        Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Share Object: {share}");

        if (isBlockCandidate)
        {
            var blockHashBytes = SerializeHeader(BlockTemplate.Header, isPrePow: false);
            share.BlockHash = blockHashBytes.ToHexString();
            Console.WriteLine($"elva Debug WaglaylaJob -----> ProcessShareInternal --- Block Hash: {share.BlockHash}");
        }

        Console.WriteLine("elva Debug WaglaylaJob -----> ProcessShareInternal --- ProcessShareInternal completed.");
        return share;
    }

    //Utilisation
    public static ulong ReduceHexToDecimal0(string hex)
    {
        // Supprimer les zéros initiaux
        hex = hex.TrimStart('0');

        // Prendre les 6 premiers caractères significatifs
        if (hex.Length >= 6)
            hex = hex.Substring(0, 4);
        else
            hex = hex.PadLeft(4, '0'); // Compléter si nécessaire

        // Convertir en nombre
        return Convert.ToUInt64(hex, 16);
    }

    public static ulong ReduceHexToDecimal(string hex)
    {
        // Supprimer les zéros initiaux
        hex = hex.TrimStart('0');

        // Prendre les 6 premiers caractères significatifs
        if (hex.Length >= 6)
            hex = hex.Substring(0, 6);
        else
            hex = hex.PadLeft(6, '0'); // Compléter si nécessaire

        // Convertir en nombre
        return Convert.ToUInt64(hex, 16);
    }


    public static double CalculateShareDifficulty(BigInteger powHash, BigInteger diff1b)
    {
        if (powHash <= 0)
            throw new ArgumentOutOfRangeException(nameof(powHash), "powHash must be greater than zero");

        // Calculer la difficulté
        var difficulty = (double)new BigRational(diff1b, powHash);
        return difficulty;
    }


    public static uint BigIntegerToCompact(BigInteger value)
    {
        // Convert BigInteger to a byte array (big-endian)
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        // Calculate size
        int size = bytes.Length;
        uint compact = 0;

        if (size <= 3)
        {
            // For small numbers, shift bytes into the compact format
            compact = (uint)(bytes[0] << 16);
            if (size > 1)
                compact |= (uint)(bytes[1] << 8);
            if (size > 2)
                compact |= bytes[2];
        }
        else
        {
            // For larger numbers, shift right to fit into compact format
            BigInteger temp = value >> (8 * (size - 3));
            byte[] tempBytes = temp.ToByteArray(isUnsigned: true, isBigEndian: true);
            compact = (uint)(tempBytes[0] << 16 | tempBytes[1] << 8 | tempBytes[2]);
            compact |= (uint)(size << 24);
        }

        // Handle sign bit
        if ((compact & 0x00800000) != 0)
        {
            compact >>= 8;
            compact |= (uint)((size + 1) << 24);
        }

        return compact;
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
}
