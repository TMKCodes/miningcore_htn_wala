using System;
using System.Numerics;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Blockchain.Kaspa.Custom.Kobra;
using NBitcoin;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;



namespace Miningcore.Blockchain.Kaspa.Custom.Kobra
{
    public class KobraJob : KaspaJob
    {
        public KobraJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
            : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher) { }


        public class KobraXoShiRo256PlusPlus
        {
            private ulong[] s = new ulong[4];

            public KobraXoShiRo256PlusPlus(Span<byte> prePowHash)
            {
                Contract.Requires<ArgumentException>(prePowHash.Length >= 32);

                for (int i = 0; i < 4; i++)
                {
                    s[i] = BitConverter.ToUInt64(prePowHash.Slice(i * 8, 8));
                }
            }

            public ulong NextU64()
            {
                ulong result = RotateLeft64(s[0] + s[3], 23) + s[0];

                ulong t = s[1] << 17;

                s[2] ^= s[0];
                s[3] ^= s[1];
                s[1] ^= s[2];
                s[0] ^= s[3];

                s[2] ^= t;
                s[3] = RotateLeft64(s[3], 45);

                return result;
            }

            private static ulong RotateLeft64(ulong value, int shift)
            {
                return (value << shift) | (value >> (64 - shift));
            }
        }




        public class Matrix
        {
            private readonly ushort[,] matrix;

            private Matrix(ushort[,] matrix)
            {
                this.matrix = matrix;
            }

            public static Matrix Generate(byte[] hash)
            {
                var generator = new KobraXoShiRo256PlusPlus(hash);
                while (true)
                {
                    var mat = RandMatrixNoRankCheck(generator);
                    if (mat.ComputeRank() == 64)
                    {
                        return mat;
                    }
                }
            }

            private static Matrix RandMatrixNoRankCheck(KobraXoShiRo256PlusPlus generator)
            {
                ushort[,] mat = new ushort[64, 64];
                for (int i = 0; i < 64; i++)
                {
                    ulong val = 0;
                    for (int j = 0; j < 64; j++)
                    {
                        int shift = j % 16;
                        if (shift == 0)
                        {
                            val = generator.NextU64(); // Appel de la méthode corrigée
                        }
                        mat[i, j] = (ushort)((val >> (4 * shift)) & 0x0F);
                    }
                }
                return new Matrix(mat);
            }


            private double[,] ConvertToFloat()
            {
                var result = new double[64, 64];
                for (int i = 0; i < 64; i++)
                {
                    for (int j = 0; j < 64; j++)
                    {
                        result[i, j] = matrix[i, j];
                    }
                }
                return result;
            }

            public int ComputeRank()
            {
                const double EPS = 1e-9;
                var matFloat = ConvertToFloat();
                bool[] rowSelected = new bool[64];
                int rank = 0;

                for (int i = 0; i < 64; i++)
                {
                    int j = 0;
                    while (j < 64 && (rowSelected[j] || Math.Abs(matFloat[j, i]) <= EPS))
                    {
                        j++;
                    }

                    if (j < 64)
                    {
                        rank++;
                        rowSelected[j] = true;
                        for (int p = i + 1; p < 64; p++)
                        {
                            matFloat[j, p] /= matFloat[j, i];
                        }

                        for (int k = 0; k < 64; k++)
                        {
                            if (k != j && Math.Abs(matFloat[k, i]) > EPS)
                            {
                                for (int p = i + 1; p < 64; p++)
                                {
                                    matFloat[k, p] -= matFloat[j, p] * matFloat[k, i];
                                }
                            }
                        }
                    }
                }

                return rank;
            }

            public byte[] HeavyHash(byte[] hash)
            {
                // Convert input hash to 4-bit vector
                byte[] vec = new byte[64];
                for (int i = 0; i < 32; i++)
                {
                    vec[2 * i] = (byte)(hash[i] >> 4);
                    vec[2 * i + 1] = (byte)(hash[i] & 0x0F);
                }

                // Matrix-vector multiplication and reduction
                byte[] product = new byte[32];
                for (int i = 0; i < 32; i++)
                {
                    ushort sum1 = 0, sum2 = 0;
                    for (int j = 0; j < 64; j++)
                    {
                        sum1 += (ushort)(matrix[2 * i, j] * vec[j]);
                        sum2 += (ushort)(matrix[2 * i + 1, j] * vec[j]);
                    }
                    product[i] = (byte)((((sum1 & 0xF) ^ ((sum1 >> 4) & 0xF) ^ ((sum1 >> 8) & 0xF)) << 4) |
                                         ((sum2 & 0xF) ^ ((sum2 >> 4) & 0xF) ^ ((sum2 >> 8) & 0xF)));
                }

                // XOR the original hash with the result
                for (int i = 0; i < 32; i++)
                {
                    product[i] ^= hash[i];
                }

                // Calculer le HeavyHash
                Span<byte> heavyHashBytes = stackalloc byte[32];
                var heavyHasher = new HeavyHash();
                heavyHasher.Digest(product, heavyHashBytes);

                // Retourner le résultat final
                return heavyHashBytes.ToArray();
            }

        }


        public class PowHash
        {
            private readonly byte[] prePowHash;
            private readonly long timestamp;

            public PowHash(byte[] prePowHash, long timestamp)
            {
                this.prePowHash = prePowHash;
                this.timestamp = timestamp;
            }

            public byte[] FinalizeWithNonce(ulong nonce)
            {
                // Combine prePowHash, timestamp, and nonce
                using (var stream = new MemoryStream())
                {
                    stream.Write(prePowHash, 0, prePowHash.Length);
                    stream.Write(BitConverter.GetBytes(timestamp), 0, 8);
                    stream.Write(new byte[32], 0, 32); // Zero padding
                    stream.Write(BitConverter.GetBytes(nonce), 0, 8);

                    // Apply hashing logic if needed
                    return stream.ToArray();
                }
            }
        }


























        protected override Share ProcessShareInternal(StratumConnection worker, string nonceHex)
        {
            var context = worker.ContextAs<KaspaWorkerContext>();

            // Convert nonce from hex string to ulong
            ulong nonce = Convert.ToUInt64(nonceHex, 16);
            Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> Nonce = {nonce}");

            // Prepare the State object
            var prePowHashBytes = SerializeHeader(BlockTemplate.Header, true);
            Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> PrePowHash = {prePowHashBytes.ToHexString()}");

            // Convert target to BigInteger
            var target = new Target(KaspaUtils.CompactToBig(BlockTemplate.Header.Bits));
            var targetUInt256 = target.ToUInt256(); // Vérifiez si cette méthode est disponible
            var targetBigInteger = UInt256ToBigInteger(targetUInt256);
            Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> Target = {targetBigInteger}");

            var state = new KobraState(prePowHashBytes, BlockTemplate.Header.Timestamp, targetBigInteger);

            // Calculate and verify PoW
            var (isValid, pow) = state.CheckPow(nonce);
            Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> PoW = {pow}, IsValid = {isValid}");

            if (!isValid)
                throw new StratumException(StratumError.LowDifficultyShare, $"Invalid share: Nonce {nonce}, PoW {pow}");

            // Calculate share difficulty
            var shareDiff = (double)new BigRational(KobraConstants.Diff1b, pow) * 256;
            Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> ShareDiff = {shareDiff}");

            // Determine if the share is a block candidate
            var blockTargetBigInteger = UInt256ToBigInteger(blockTargetValue);
            var isBlockCandidate = pow <= blockTargetBigInteger;
            Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> IsBlockCandidate = {isBlockCandidate}");

            // Create the share object
            var result = new Share
            {
                BlockHeight = (long)BlockTemplate.Header.DaaScore,
                NetworkDifficulty = Difficulty,
                Difficulty = context.Difficulty / 256,
                IsBlockCandidate = isBlockCandidate
            };

            if (isBlockCandidate)
            {
                var hashBytes = SerializeHeader(BlockTemplate.Header, false);
                result.BlockHash = hashBytes.ToHexString();
                Console.WriteLine($"elva Debug KobraJob -----> ProcessShareInternal ---> BlockHash = {result.BlockHash}");
            }

            return result;
        }




















        /// <summary>
        /// Utility method to convert uint256 to BigInteger
        /// </summary>
        private static BigInteger UInt256ToBigInteger(uint256 value)
        {
            var bytes = value.ToBytes(); // Convert uint256 to byte array
            return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        }

        private class KobraState
        {
            private readonly Matrix matrix;
            private readonly BigInteger target;
            private readonly PowHash hasher;

            public KobraState(Span<byte> prePowHash, long timestamp, BigInteger target)
            {
                this.target = target;

                // Initialize the hasher with prePowHash and timestamp
                this.hasher = new PowHash(prePowHash.ToArray(), timestamp);
                Console.WriteLine($"elva Debug KobraJob -----> KobraState ---> Hasher initialized with PrePowHash and Timestamp = {timestamp}");

                // Generate the matrix
                this.matrix = Matrix.Generate(prePowHash.ToArray());
                Console.WriteLine("elva Debug KobraJob -----> KobraState ---> Matrix generated successfully");
            }

            public (bool, BigInteger) CheckPow(ulong nonce)
            {
                // Finalize the PoW hash
                var hash = hasher.FinalizeWithNonce(nonce);
                Console.WriteLine($"elva Debug KobraJob -----> CheckPow ---> Hash after adding nonce = {hash.ToHexString()}");

                // Apply heavy hash using the matrix
                var heavyHash = matrix.HeavyHash(hash);
                Console.WriteLine($"elva Debug KobraJob -----> CheckPow ---> HeavyHash = {heavyHash.ToHexString()}");

                // Convert to BigInteger
                var pow = new BigInteger(heavyHash, true, true);
                Console.WriteLine($"elva Debug KobraJob -----> CheckPow ---> PoW as BigInteger = {pow}");

                // Check if PoW meets the target
                return (pow <= target, pow);
            }
        }































    }
}
