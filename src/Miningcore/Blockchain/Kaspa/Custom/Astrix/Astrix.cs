using System;
using System.Numerics;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using System.Globalization;

namespace Miningcore.Blockchain.Kaspa.Custom.Astrix
{
    public class AstrixJob : KaspaJob
    {
        protected Blake3 blake3Hasher;
        protected Sha3_256 sha3_256Hasher;

        public AstrixJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
            : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
        {
            this.blake3Hasher = new Blake3();
            this.sha3_256Hasher = new Sha3_256();
        }

        protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
        {
            try
            {
                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Start processing share for worker {worker.ConnectionId}, Nonce = {nonce}");

                var context = worker.ContextAs<KaspaWorkerContext>();
                BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Converted Nonce = {BlockTemplate.Header.Nonce}");

                var prePowHashBytes = SerializeHeader(BlockTemplate.Header, true);
                var coinbaseBytes = SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce);

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Coinbase bytes = {coinbaseBytes.ToHexString()}");

                Span<byte> blake3Bytes = stackalloc byte[32];
                blake3Hasher.Digest(coinbaseBytes, blake3Bytes);

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Blake3 Hash = {blake3Bytes.ToHexString()}");

                Span<byte> sha3_256Bytes = stackalloc byte[32];
                sha3_256Hasher.Digest(blake3Bytes, sha3_256Bytes);

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Sha3_256 Hash = {sha3_256Bytes.ToHexString()}");

                Span<byte> hashCoinbaseBytes = stackalloc byte[32];
                shareHasher.Digest(ComputeCoinbase(prePowHashBytes, sha3_256Bytes), hashCoinbaseBytes);

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Share Hash = {hashCoinbaseBytes.ToHexString()}");

                var targetHashCoinbaseBytes = new Target(new BigInteger(hashCoinbaseBytes.ToNewReverseArray(), true, true));
                var hashCoinbaseBytesValue = targetHashCoinbaseBytes.ToUInt256();

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Hash Value = {hashCoinbaseBytesValue}, Target = {targetHashCoinbaseBytes.ToBigInteger()}");

                // Validate values before calculating difficulty
                if (targetHashCoinbaseBytes.ToBigInteger() == 0 || KaspaConstants.Diff1b <= targetHashCoinbaseBytes.ToBigInteger())
                {
                    Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Invalid difficulty division, returning shareDiff = 0");
                    throw new StratumException(StratumError.LowDifficultyShare, $"Invalid share: TargetHash >= KaspaConstants.Diff1b");
                }

                // Calculate share difficulty
                var numerator = BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
                //var numerator = BigInteger.Parse("000000ffff000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

                var denominator = targetHashCoinbaseBytes.ToBigInteger();

                if (denominator == BigInteger.Zero)
                {
                    throw new StratumException(StratumError.LowDifficultyShare, "Invalid share: TargetHash denominator is 0.");
                }

                if (numerator < denominator)
                {
                    Console.WriteLine("Warning: Numerator is smaller than denominator. The calculated shareDiff will be very small.");
                }

                var shareDiff = (double)new BigRational(numerator, denominator) * 1;
                //    var shareDiff = (double)new BigRational(numerator, denominator) * shareMultiplier;


                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Share Difficulty = {shareDiff}");

                var stratumDifficulty = context.Difficulty;
                var ratio = shareDiff / stratumDifficulty;

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Share Ratio = {ratio}, Stratum Difficulty = {stratumDifficulty}");

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> hashCoinbaseBytesValue = {hashCoinbaseBytesValue} / blockTargetValue {blockTargetValue}");

                // Check if the share meets the much harder block difficulty (block candidate)
                var isBlockCandidate = hashCoinbaseBytesValue <= blockTargetValue;

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Is Block Candidate = {isBlockCandidate}");

                if (!isBlockCandidate && ratio < 0.99)
                {
                    if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                    {
                        ratio = shareDiff / context.PreviousDifficulty.Value;
                        Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Adjusted Ratio = {ratio}, Previous Difficulty = {context.PreviousDifficulty.Value}");

                        if (ratio < 0.99)
                            throw new StratumException(StratumError.LowDifficultyShare, $"Low difficulty share ({shareDiff})");

                        stratumDifficulty = context.PreviousDifficulty.Value;
                    }
                    else
                    {
                        throw new StratumException(StratumError.LowDifficultyShare, $"Low difficulty share ({shareDiff})");
                    }
                }

                var result = new Share
                {
                    BlockHeight = (long)BlockTemplate.Header.DaaScore,
                    NetworkDifficulty = Difficulty,
                    Difficulty = context.Difficulty
                    //Difficulty = context.Difficulty / shareMultiplier

                };

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Share Created: BlockHeight = {result.BlockHeight}, NetworkDifficulty = {result.NetworkDifficulty}, Difficulty = {result.Difficulty}");

                if (isBlockCandidate)
                {
                    var hashBytes = SerializeHeader(BlockTemplate.Header, false);
                    result.IsBlockCandidate = true;
                    result.BlockHash = hashBytes.ToHexString();

                    Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Block Candidate Found: BlockHash = {result.BlockHash}");
                }

                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Final Result: Share = {result}, IsBlockCandidate = {result.IsBlockCandidate}");

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"elva Debug AstrixJob -----> ProcessShareInternal ---> Exception occurred: {ex.Message}");
                throw;
            }
        }
    }
}
