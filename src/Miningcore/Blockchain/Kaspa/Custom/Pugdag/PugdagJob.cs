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

namespace Miningcore.Blockchain.Kaspa.Custom.Pugdag;

  public class PugdagXoShiRo256PlusPlus
    {
        private ulong[] s = new ulong[4];

        public PugdagXoShiRo256PlusPlus(Span<byte> prePowHash)
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

public static class TargetExtensions
{
    public static string ToHex(this NBitcoin.Target target)
    {
        // Convertir la cible en une valeur compacte et retourner la chaîne hexadécimale
        uint compact = target.ToCompact();
        return compact.ToString("X").PadLeft(8, '0');
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

public static class StringExtensions
{
    public static BigInteger ToUInt256(this string hexString)
    {
        if (string.IsNullOrWhiteSpace(hexString))
            throw new ArgumentException("Hex string cannot be null or empty");

        // Retirer le préfixe "0x" si présent
        if (hexString.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hexString = hexString.Substring(2);

        // Convertir en tableau de bytes
        var bytes = BigInteger.Parse(hexString, System.Globalization.NumberStyles.HexNumber).ToByteArray();

        // S'assurer que la taille des bytes ne dépasse pas 32 octets
        if (bytes.Length > 32)
            throw new ArgumentException("Hex string is too large to fit in 256 bits");

        return new BigInteger(bytes);
    }
}


public class PugdagJob : KaspaJob
{
    protected Blake3 blake3Hasher;
    protected Sha3_256 sha3_256Hasher;

    private const double EPS = 1e-9;


    public PugdagJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
        : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
        // Initialize any specific hashers or components
        blake3Hasher = new Blake3();
        sha3_256Hasher = new Sha3_256();

        Console.WriteLine(" PugdagJob initialized with custom hashers.");
    }

protected override ushort[][] GenerateMatrix(Span<byte> prePowHash)
{
    // Utilisation d'un tableau rectangulaire pour les performances
    ushort[,] rectMatrix = new ushort[64, 64];
    var generator = new PugdagXoShiRo256PlusPlus(prePowHash);

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


/*
private const int DOMAIN_HASH_SIZE = 32;
   public byte[] CalculateProofOfWorkValue(byte[] prePowHash, long timestamp, ulong nonce, ushort[][] matrix)
{

    Span<byte> firstPass = stackalloc byte[DOMAIN_HASH_SIZE];
    Span<byte> lastPass = stackalloc byte[DOMAIN_HASH_SIZE];
    Span<byte> zeroes = stackalloc byte[DOMAIN_HASH_SIZE];

    var hasher = new Blake3();

    byte[] timestampBytes = BitConverter.GetBytes(timestamp);
    byte[] nonceBytes = BitConverter.GetBytes(nonce);

    if (!BitConverter.IsLittleEndian)
    {
        Array.Reverse(timestampBytes);
        Array.Reverse(nonceBytes);
    }

// Initialisation du buffer combiné avec la taille totale nécessaire
Span<byte> combinedData = stackalloc byte[
    prePowHash.Length + timestampBytes.Length + zeroes.Length + nonceBytes.Length
];

// 1. Copier le prePowHash au début du buffer
prePowHash.CopyTo(combinedData);

// 2. Copier le timestamp juste après le prePowHash
timestampBytes.CopyTo(combinedData.Slice(prePowHash.Length));

// 3. Copier les zéros après le timestamp
zeroes.CopyTo(combinedData.Slice(prePowHash.Length + timestampBytes.Length));

// 4. Copier le nonce après les zéros
nonceBytes.CopyTo(combinedData.Slice(prePowHash.Length + timestampBytes.Length + zeroes.Length));

    hasher.Digest(combinedData, firstPass);

    HoohashMatrixMultiplication(matrix, firstPass, lastPass);

    return lastPass.ToArray();
}
*/


private const int DOMAIN_HASH_SIZE = 32;
public byte[] CalculateProofOfWorkValue(byte[] prePowHash, long timestamp, ulong nonce, ushort[][] matrix)
{

    Span<byte> firstPass = stackalloc byte[DOMAIN_HASH_SIZE];
    Span<byte> lastPass = stackalloc byte[DOMAIN_HASH_SIZE];
    Span<byte> zeroes = stackalloc byte[DOMAIN_HASH_SIZE];

    var hasher = new Blake3();

    byte[] timestampBytes = BitConverter.GetBytes(timestamp);
    byte[] nonceBytes = BitConverter.GetBytes(nonce);

    if (!BitConverter.IsLittleEndian)
    {
        Array.Reverse(timestampBytes);
        Array.Reverse(nonceBytes);
    }

// Initialisation du buffer combiné avec la taille totale nécessaire
Span<byte> combinedData = stackalloc byte[
    prePowHash.Length + timestampBytes.Length + zeroes.Length + nonceBytes.Length
];

// 1. Copier le prePowHash au début du buffer
prePowHash.CopyTo(combinedData);

// 2. Copier le timestamp juste après le prePowHash
timestampBytes.CopyTo(combinedData.Slice(prePowHash.Length));

// 3. Copier les zéros après le timestamp
zeroes.CopyTo(combinedData.Slice(prePowHash.Length + timestampBytes.Length));

// 4. Copier le nonce après les zéros
nonceBytes.CopyTo(combinedData.Slice(prePowHash.Length + timestampBytes.Length + zeroes.Length));

    hasher.Digest(combinedData, firstPass);

    HoohashMatrixMultiplication(matrix, firstPass, lastPass);

    return lastPass.ToArray();
}






























    protected override Span<byte> SerializeHeader(kaspad.RpcBlockHeader header, bool isPrePow = true, bool isLittleEndian = false)
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

    protected override Span<byte> SerializeCoinbase(Span<byte> prePowHash, long timestamp, ulong nonce)
    {
        Span<byte> hashBytes = stackalloc byte[32];
        Span<byte> zeroes = stackalloc byte[32]; // 32 zero byte padding, initialisé à zéro

        // Convertir le timestamp et le nonce en little endian
        byte[] timestampBytes = BitConverter.GetBytes(timestamp);
        byte[] nonceBytes = BitConverter.GetBytes(nonce);

        // Création d'une instance de Blake3
        var hasher = new Blake3();

        // Combiner les données en un seul buffer
        Span<byte> combinedData = stackalloc byte[prePowHash.Length + timestampBytes.Length + zeroes.Length + nonceBytes.Length];
        prePowHash.CopyTo(combinedData);
        timestampBytes.CopyTo(combinedData.Slice(prePowHash.Length));
        zeroes.CopyTo(combinedData.Slice(prePowHash.Length + timestampBytes.Length));
        nonceBytes.CopyTo(combinedData.Slice(prePowHash.Length + timestampBytes.Length + zeroes.Length));

        // Utilisation de Digest pour calculer le hash de toutes les données combinées
        hasher.Digest(combinedData, hashBytes);

        // Appel à HoohashMatrixMultiplication en utilisant le résultat du premier hachage (hashBytes)
        var matrix = GenerateHoohashMatrix(prePowHash);
        Span<byte> lastPass = stackalloc byte[32];
        HoohashMatrixMultiplication(matrix, hashBytes, lastPass);

        // Copier le résultat final dans hashBytes
        lastPass.CopyTo(hashBytes);

        return hashBytes.ToArray();
    }

protected virtual ushort[][] GenerateHoohashMatrix(Span<byte> prePowHash)
{
    ushort[][] matrix = new ushort[64][];
    for (int i = 0; i < 64; i++)
        matrix[i] = new ushort[64];

    var generator = new PugdagXoShiRo256PlusPlus(prePowHash);
    while (true)
    {
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < 64; j += 16)
            {
                ulong val = generator.Uint64();
                for (int shift = 0; shift < 16; shift++)
                    matrix[i][j + shift] = (ushort)((val >> (4 * shift)) & 0x0F);
            }
        }

int rank = ComputeHoohashRank(matrix);

        if (ComputeHoohashRank(matrix) == 64)
                    {

        return matrix;
    }
            else
{
}
    }
}

protected virtual int ComputeHoohashRank(ushort[][] matrix)
{
    double[][] B = matrix.Select(row => row.Select(val => (double)val + ComplexNonLinear(val)).ToArray()).ToArray();
    int rank = 0;
    bool[] rowSelected = new bool[64];

    for (int i = 0; i < 64; i++)
    {
        int j;
        for (j = 0; j < 64; j++)
        {
            if (!rowSelected[j] && Math.Abs(B[j][i]) > EPS)
                break;
        }
        if (j != 64)
        {
            rank++;
            rowSelected[j] = true;
            double pivot = B[j][i];
            for (int p = i + 1; p < 64; p++)
                B[j][p] /= pivot;

            for (int k = 0; k < 64; k++)
            {
                if (k != j && Math.Abs(B[k][i]) > EPS)
                {
                    for (int p = i + 1; p < 64; p++)
                        B[k][p] -= B[j][p] * B[k][i];
                }
            }
        }

                    else
{
}
    }
return rank;
}


private const double PI = Math.PI;
public virtual double MediumComplexNonLinear(double x)
{
    return Math.Exp(Math.Sin(x) + Math.Cos(x));
}

public virtual double IntermediateComplexNonLinear(double x)
{
    if (x == PI / 2 || x == 3 * PI / 2)
    {
        return 0; // Avoid singularity
    }
    return Math.Sin(x) * Math.Cos(x) * Math.Tan(x);
}

public virtual double HighComplexNonLinear(double x)
{
    return Math.Exp(x) * Math.Log(x + 1);
}


public virtual double ComplexNonLinear(double x)
{
//      Console.WriteLine("elva Debug PugdagJob -----> ComplexNonLinear Calling ...");

    double transformFactor = x % 4 / 4;
    if (x < 1)
    {
        if (transformFactor < 0.25)
        {
            return MediumComplexNonLinear(x + (1 + transformFactor));
        }
        else if (transformFactor < 0.5)
        {
            return MediumComplexNonLinear(x - (1 + transformFactor));
        }
        else if (transformFactor < 0.75)
        {
            return MediumComplexNonLinear(x * (1 + transformFactor));
        }
        else
        {
            return MediumComplexNonLinear(x / (1 + transformFactor));
        }
    }
    else if (x < 10)
    {
        if (transformFactor < 0.25)
        {
            return IntermediateComplexNonLinear(x + (1 + transformFactor));
        }
        else if (transformFactor < 0.5)
        {
            return IntermediateComplexNonLinear(x - (1 + transformFactor));
        }
        else if (transformFactor < 0.75)
        {
            return IntermediateComplexNonLinear(x * (1 + transformFactor));
        }
        else
        {
            return IntermediateComplexNonLinear(x / (1 + transformFactor));
        }
    }
    else
    {
        if (transformFactor < 0.25)
        {
            return HighComplexNonLinear(x + (1 + transformFactor));
        }
        else if (transformFactor < 0.5)
        {
            return HighComplexNonLinear(x - (1 + transformFactor));
        }
        else if (transformFactor < 0.75)
        {
            return HighComplexNonLinear(x * (1 + transformFactor));
        }
        else
        {
            return HighComplexNonLinear(x / (1 + transformFactor));
        }
    }

    //   Console.WriteLine($"elva Debug PugdagJob -----> ComplexNonLinear done");
}

protected virtual void HoohashMatrixMultiplication(ushort[][] matrix, Span<byte> hashBytes, Span<byte> output)
{
    float[] vector = new float[64];
    float[] product = new float[64];
    byte[] res = new byte[32];

    // Populate the vector with floating-point values
    for (int i = 0; i < 32; i++)
    {
        vector[2 * i] = (float)(hashBytes[i] >> 4);
        vector[2 * i + 1] = (float)(hashBytes[i] & 0x0F);
    }

    // Debug : Affichage du vecteur initial
    for (int i = 0; i < 64; i++)
    {
 //       Console.Write($"{vector[i]:0.00}, ");
    }
    Console.WriteLine();

    // Matrix-vector multiplication with floating-point operations
    for (int i = 0; i < 64; i++)
    {
        for (int j = 0; j < 64; j++)
        {
            float forComplex = matrix[i][j] * vector[j];

            // Debug : Affichage de chaque étape de la multiplication
     //       Console.WriteLine($"elva Debug PugdagJob -----> HoohashMatrixMultiplication --- Multiplying matrix[{i}][{j}] ({matrix[i][j]}) * vector[{j}] ({vector[j]}) = {forComplex}");

            while (forComplex > 16)
            {
                forComplex *= 0.1f;
            }

            // Ajout du résultat dans le produit après transformation
            product[i] += (float)ComplexNonLinear(forComplex);

            // Debug : Affichage du produit partiel
    //        Console.WriteLine($"elva Debug PugdagJob -----> HoohashMatrixMultiplication --- Updated product[{i}]: {product[i]}");
        }
    }

    // Debug : Affichage des produits finaux avant conversion
    for (int i = 0; i < 64; i++)
    {
        Console.Write($"{product[i]:0.00}, ");
    }

    // Convert product back to uint16 and then to byte array
    for (int i = 0; i < 32; i++)
    {
        ulong high = (ulong)(product[2 * i] * 0.00000001);
        ulong low = (ulong)(product[2 * i + 1] * 0.00000001);

        // Debug : Affichage des valeurs intermédiaires high et low
  //      Console.WriteLine($"elva Debug PugdagJob -----> HoohashMatrixMultiplication --- High: {high}, Low: {low} for index {i}");

        // Combine high and low into a single byte
        byte combined = (byte)((high ^ low) & 0xFF);
        res[i] = (byte)(hashBytes[i] ^ combined);

        // Debug : Affichage du résultat XOR
   //     Console.WriteLine($"elva Debug PugdagJob -----> HoohashMatrixMultiplication --- XOR result for res[{i}]: {res[i]}");
    }

    // Création d'une instance de Blake3
    var blake3 = new Blake3(); // Assure-toi d'utiliser l'implémentation correcte de Blake3

    // Préparation du buffer de sortie pour le hash
    Span<byte> blake3Hash = stackalloc byte[32];

    // Utilisation de Digest pour calculer le hash de 'res' et le stocker dans 'blake3Hash'
   // res[31] ^= 1; // Modifier légèrement le dernier octet avant le hachage

    blake3.Digest(res, blake3Hash);

    // Copier le hash final dans 'output'
    blake3Hash.CopyTo(output);

    // Debug : Affichage du résultat final après Blake3
    for (int i = 0; i < 32; i++)
    {
        Console.Write($"{output[i]}, ");
    }
}





/*

protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
{
    // Vérifications initiales
    if (worker == null)
        throw new ArgumentNullException(nameof(worker));
    if (string.IsNullOrEmpty(nonce))
        throw new ArgumentException("Nonce cannot be null or empty", nameof(nonce));

    var context = worker.ContextAs<KaspaWorkerContext>();
    Console.WriteLine("elva Debug -----> Début du traitement du share.");

    // Conversion du nonce (Hex → ulong)
    Console.WriteLine($"elva Debug -----> Nonce reçu : {nonce}");
    if (!ulong.TryParse(nonce, System.Globalization.NumberStyles.HexNumber, null, out ulong parsedNonce))
    {
        Console.WriteLine("elva Error -----> Format de nonce invalide.");
        throw new ArgumentException("Invalid nonce format");
    }
    Console.WriteLine($"elva Debug -----> Nonce parsé en ulong : {parsedNonce}");

    // Conversion en HEX puis en Little-endian
    string nonceHex = parsedNonce.ToString("X").PadLeft(16, '0');
    byte[] nonceBytes = Enumerable.Range(0, nonceHex.Length / 2)
                                  .Select(x => Convert.ToByte(nonceHex.Substring(x * 2, 2), 16))
                                  .ToArray();
    Array.Reverse(nonceBytes);
    BigInteger littleEndianNonce = new BigInteger(nonceBytes, isUnsigned: true, isBigEndian: false);
    ulong finalNonce = (ulong)littleEndianNonce;
    Console.WriteLine($"elva Debug -----> Nonce final en Little-endian : {finalNonce}");

    // Vérification du BlockTemplate et Header
    if (BlockTemplate?.Header == null)
    {
        Console.WriteLine("elva Error -----> BlockTemplate ou Header est null.");
        throw new InvalidOperationException("BlockTemplate or its Header is not initialized");
    }

    var timestamp = BlockTemplate.Header.Timestamp;
    Console.WriteLine($"elva Debug -----> Timestamp du block : {timestamp}");

    // Sérialisation du header avec le nonce
    var prePowHash = SerializeHeader(BlockTemplate.Header).ToArray();
    Console.WriteLine($"elva Debug -----> Header pré-POW hashé : {BitConverter.ToString(prePowHash)}");

    // Calcul du hash Coinbase
    Span<byte> hashCoinbaseBytes = stackalloc byte[32];
    var hasher = new Blake3();
    var coinbaseBytes = SerializeCoinbase(prePowHash, timestamp, finalNonce);
    hasher.Digest(coinbaseBytes, hashCoinbaseBytes);
    Console.WriteLine($"elva Debug -----> Hash de la coinbase : {BitConverter.ToString(hashCoinbaseBytes.ToArray())}");

    // Génération de la matrice Hoohash
    var matrix = GenerateHoohashMatrix(prePowHash);
    Console.WriteLine("elva Debug -----> Matrice Hoohash générée.");

    // Calcul de la valeur Proof-of-Work
    var proofOfWorkValue = CalculateProofOfWorkValue(prePowHash, timestamp, finalNonce, matrix);
    Console.WriteLine($"elva Debug -----> Valeur Proof-of-Work : {BitConverter.ToString(proofOfWorkValue)}");

    // Comparaison avec la cible en utilisant la mantisse

        var targetHashBigInteger = new BigInteger(proofOfWorkValue.Reverse().ToArray(), true, true);
        var blockTargetBigInteger = ConvertBitsToBigInteger(BlockTemplate.Header.Bits);
        Console.WriteLine($"elva Debug -----> Hash cible POW : {targetHashBigInteger}");
        Console.WriteLine($"elva Debug -----> Hash cible du block : {blockTargetBigInteger}");

        // Conversion des cibles compactes
        string POWHex = targetHashBigInteger.ToString("X");
        string TargetHex = blockTargetBigInteger.ToString("X");

    uint POWmantissa = ExtractMantissa(POWHex);
    uint Targetmantissa = ExtractMantissa(TargetHex);

    bool isBlockCandidate = POWmantissa <= Targetmantissa;
    // Console.WriteLine($"elva Debug -----> Est-ce un candidat au block ? {isBlockCandidate} / bigInt0 {bigInt0} / bigInt1 {bigInt1}");
    Console.WriteLine($"elva Debug -----> Est-ce un candidat au block ? {isBlockCandidate}");

    // Calcul de la difficulté du share
    var shareDiff = (double)new BigRational(KaspaConstants.Diff1b, targetHashBigInteger)
                    * KaspaConstants.Pow2xDiff1TargetNumZero
                    * (double)KaspaConstants.MinHash
                    / KaspaConstants.ShareMultiplier;
    Console.WriteLine($"elva Debug -----> Difficulté du share : {shareDiff}");

    // Vérification de la difficulté
    var stratumDifficulty = context.Difficulty;
    if (!isBlockCandidate && shareDiff / stratumDifficulty < 0.99)
    {
        Console.WriteLine($"elva Error -----> Share rejeté - difficulté trop faible : {shareDiff}");
        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
    }

    // Mise à jour du header
    BlockTemplate.Header.Nonce = finalNonce;
    Console.WriteLine($"elva Debug -----> Nonce du header mis à jour : {BlockTemplate.Header.Nonce}");

    // Création du résultat Share
    var result = new Share
    {
        BlockHeight = (long)BlockTemplate.Header.DaaScore,
        NetworkDifficulty = Difficulty,
        Difficulty = context.Difficulty / KaspaConstants.ShareMultiplier,
        IsBlockCandidate = isBlockCandidate
    };

    if (isBlockCandidate)
    {
        var hashBytes = SerializeHeader(BlockTemplate.Header, false);
        result.BlockHash = hashBytes.ToHexString();
        Console.WriteLine($"elva Debug -----> Block candidat détecté avec le hash : {result.BlockHash}");
    }

    Console.WriteLine("elva Debug -----> Share process terminé avec succès.");
    return result;
}

*/

protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
{
    var context = worker.ContextAs<KaspaWorkerContext>();
    Console.WriteLine($"elva Debug -----> Début du traitement du share pour le worker");

    // Conversion du nonce (BigInteger → ulong)
    Console.WriteLine($"elva Debug -----> Nonce reçu : {nonce}");
    if (!ulong.TryParse(nonce, System.Globalization.NumberStyles.HexNumber, null, out ulong parsedNonce))
    {
        Console.WriteLine($"elva Error -----> Format de nonce invalide : {nonce}");
        throw new ArgumentException("Invalid nonce format");
    }
    Console.WriteLine($"elva Debug -----> Nonce parsé en ulong : {parsedNonce}");

    // Conversion du nonce en HEX puis en Little Endian
    string nonceHex = parsedNonce.ToString("X").PadLeft(16, '0');
    Console.WriteLine($"elva Debug -----> Nonce en hexadécimal : {nonceHex}");

    byte[] nonceBytes = Enumerable.Range(0, nonceHex.Length / 2)
                                  .Select(x => Convert.ToByte(nonceHex.Substring(x * 2, 2), 16))
                                  .ToArray();
    Array.Reverse(nonceBytes);
    BigInteger littleEndianNonce = new BigInteger(nonceBytes, isUnsigned: true, isBigEndian: false);
    Console.WriteLine($"elva Debug -----> Nonce en Little Endian : {littleEndianNonce}");

    // Vérification si le nonce peut être converti en ulong
    if (littleEndianNonce > ulong.MaxValue)
    {
        Console.WriteLine($"elva Error -----> Le nonce dépasse la valeur maximale autorisée pour ulong : {littleEndianNonce}");
        throw new OverflowException("Nonce value exceeds the maximum value for ulong.");
    }

    ulong finalNonce = (ulong)littleEndianNonce;
    Console.WriteLine($"elva Debug -----> Nonce final utilisé : {finalNonce}");

    // Sérialisation du header avec le nonce
    var timestamp = BlockTemplate.Header.Timestamp;
    Console.WriteLine($"elva Debug -----> Timestamp du block : {timestamp}");

    var prePowHash = SerializeHeader(BlockTemplate.Header).ToArray();
    Console.WriteLine($"elva Debug -----> Header pré-POW hashé : {BitConverter.ToString(prePowHash)}");

    // Calcul du hash Coinbase
    Span<byte> hashCoinbaseBytes = stackalloc byte[32];
    var hasher = new Blake3();
    var coinbaseBytes = SerializeCoinbase(prePowHash, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce);
    hasher.Digest(coinbaseBytes, hashCoinbaseBytes);
    Console.WriteLine($"elva Debug -----> Hash de la coinbase : {BitConverter.ToString(hashCoinbaseBytes.ToArray())}");

    var targetHashCoinbaseBytes = new Target(new BigInteger(hashCoinbaseBytes.ToNewReverseArray(), true, true));
    var hashCoinbaseBytesValue = targetHashCoinbaseBytes.ToUInt256();
    Console.WriteLine($"elva Debug -----> Hash Coinbase en BigInteger : {hashCoinbaseBytesValue}");

    // Génération de la matrice Hoohash
    var matrix = GenerateHoohashMatrix(prePowHash);
    Console.WriteLine($"elva Debug -----> Matrice Hoohash générée.");

    // Calcul de la valeur Proof-of-Work
    var proofOfWorkValue = CalculateProofOfWorkValue(prePowHash, timestamp, finalNonce, matrix);
    Console.WriteLine($"elva Debug -----> Valeur Proof-of-Work : {BitConverter.ToString(proofOfWorkValue)}");

    var targetHashBigInteger = new BigInteger(proofOfWorkValue.Reverse().ToArray(), true, true);
    var blockTargetBigInteger = ConvertBitsToBigInteger(BlockTemplate.Header.Bits);
    Console.WriteLine($"elva Debug -----> Hash cible POW : {targetHashBigInteger}");
    Console.WriteLine($"elva Debug -----> Hash cible du block : {blockTargetBigInteger}");

    // Calcul de la difficulté du share
    var shareDiff = (double)new BigRational(KaspaConstants.Diff1b, targetHashBigInteger)
                    * KaspaConstants.Pow2xDiff1TargetNumZero
                    * (double)KaspaConstants.MinHash
                    / KaspaConstants.ShareMultiplier;
    Console.WriteLine($"elva Debug -----> Difficulté du share : {shareDiff}");

    // Récupération de la difficulté du stratum
    var stratumDifficulty = context.Difficulty;
    var ratio = shareDiff / stratumDifficulty;
    Console.WriteLine($"elva Debug -----> Ratio de difficulté : {ratio}");

    // Vérification si le share est un candidat au block
    var isBlockCandidate = targetHashBigInteger <= blockTargetBigInteger;
    Console.WriteLine($"elva Debug -----> Est-ce un candidat au block ? {isBlockCandidate}");

    // Vérification de la difficulté du share
    if (!isBlockCandidate && ratio < 0.99)
    {
        if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
        {
            ratio = shareDiff / context.PreviousDifficulty.Value;

            if (ratio < 0.99)
            {
                Console.WriteLine($"elva Error -----> Share rejeté - difficulté trop faible : {shareDiff}");
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            // Utilisation de la difficulté précédente
            stratumDifficulty = context.PreviousDifficulty.Value;
            Console.WriteLine($"elva Debug -----> Utilisation de la difficulté précédente : {stratumDifficulty}");
        }
        else
        {
            Console.WriteLine($"elva Error -----> Share rejeté - difficulté trop faible : {shareDiff}");
            throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }
    }

    BlockTemplate.Header.Nonce = finalNonce;
    Console.WriteLine($"elva Debug -----> Nonce du header mis à jour : {BlockTemplate.Header.Nonce}");

    var result = new Share
    {
        BlockHeight = (long)BlockTemplate.Header.DaaScore,
        NetworkDifficulty = Difficulty,
        Difficulty = context.Difficulty / KaspaConstants.ShareMultiplier,
    };

    if (isBlockCandidate)
    {
        var hashBytes = SerializeHeader(BlockTemplate.Header, false);
        result.IsBlockCandidate = true;
        result.BlockHash = hashBytes.ToHexString();
        Console.WriteLine($"elva Debug -----> Block candidat détecté avec le hash : {result.BlockHash}");
    }

    Console.WriteLine($"elva Debug -----> Share process terminé avec succès.");
    return result;
}

























public static uint ExtractMantissa(string hexValue)
{
    // Convertir la chaîne hexadécimale en tableau de bytes
    byte[] valueBytes = Enumerable.Range(0, hexValue.Length / 2)
        .Select(x => Convert.ToByte(hexValue.Substring(x * 2, 2), 16))
        .ToArray();

    // Retirer les zéros inutiles en début
    int firstNonZeroIndex = Array.FindIndex(valueBytes, b => b != 0);

    // Si la valeur est entièrement composée de zéros
    if (firstNonZeroIndex == -1)
        return 0;

    // Décaler la partie significative vers la gauche
    byte[] significantBytes = valueBytes.Skip(firstNonZeroIndex).ToArray();

    // Récupérer les 3 octets significatifs
    uint mantissa = 0;
    for (int i = 0; i < Math.Min(3, significantBytes.Length); i++)
    {
        mantissa |= (uint)(significantBytes[i] << (8 * (2 - i)));
    }

    return mantissa;
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
