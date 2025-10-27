using System;
using System.IO;
using System.Numerics;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa.Custom.Spectre
{
    public class SpectreJob : KaspaJob
    {
        protected AstroBWTv3 astroBWTv3Hasher;

        public double ShareMultiplier = 256.0;
        //public double ShareMultiplier { get; private set; } = 256.0;


        public SpectreJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
            : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
        {
            this.astroBWTv3Hasher = new AstroBWTv3();
            Console.WriteLine("[DEBUG] [SpectreJob] Constructor called. AstroBWTv3 hasher initialized.");
        }

        protected override Span<byte> SerializeCoinbase(Span<byte> prePowHash, long timestamp, ulong nonce)
        {
            Console.WriteLine($"[DEBUG] [SpectreJob] SerializeCoinbase called. Timestamp: {timestamp}, Nonce: {nonce}");
            using (var stream = new MemoryStream())
            {
                stream.Write(prePowHash);
                stream.Write(BitConverter.GetBytes((ulong)timestamp));
                stream.Write(new byte[32]); // 32 zero bytes padding
                stream.Write(BitConverter.GetBytes(nonce));

                var result = (Span<byte>)stream.ToArray();
                Console.WriteLine($"[DEBUG] [SpectreJob] SerializeCoinbase completed. Length: {result.Length}");
                return result;
            }
        }

        protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
        {
            Console.WriteLine($"[DEBUG] [SpectreJob] ProcessShareInternal called. Nonce: {nonce}");
            var context = worker.ContextAs<KaspaWorkerContext>();
            BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);
            Console.WriteLine($"[DEBUG] [SpectreJob] Nonce converted: {BlockTemplate.Header.Nonce}");

            var prePowHashBytes = SerializeHeader(BlockTemplate.Header, true);
            Console.WriteLine($"[DEBUG] [SpectreJob] prePowHashBytes serialized.");

            var coinbaseRawBytes = SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce);
            Span<byte> coinbaseBytes = stackalloc byte[32];
            coinbaseHasher.Digest(coinbaseRawBytes, coinbaseBytes);
            Console.WriteLine($"[DEBUG] [SpectreJob] coinbaseHasher digest completed.");

            Span<byte> astroBWTv3Bytes = stackalloc byte[32];
            astroBWTv3Hasher.Digest(coinbaseBytes, astroBWTv3Bytes);
            Console.WriteLine($"[DEBUG] [SpectreJob] astroBWTv3Hasher digest completed.");

            Span<byte> hashCoinbaseBytes = stackalloc byte[32];
            shareHasher.Digest(ComputeCoinbase(coinbaseRawBytes, astroBWTv3Bytes), hashCoinbaseBytes);
            Console.WriteLine($"[DEBUG] [SpectreJob] shareHasher digest completed.");

            var targetHashCoinbaseBytes = new Target(new BigInteger(hashCoinbaseBytes.ToNewReverseArray(), true, true));
            var hashCoinbaseBytesValue = targetHashCoinbaseBytes.ToUInt256();

            Console.WriteLine($"[DEBUG] [SpectreJob] HashCoinbaseBytes calculated: {hashCoinbaseBytesValue}");

            var shareDiff = (double)new BigRational(SpectreConstants.Diff1b, targetHashCoinbaseBytes.ToBigInteger()) * ShareMultiplier;
            Console.WriteLine($"[DEBUG] [SpectreJob] Share difficulty calculated: {shareDiff}");

            var stratumDifficulty = context.Difficulty;
            var ratio = shareDiff / stratumDifficulty;

            var isBlockCandidate = hashCoinbaseBytesValue <= blockTargetValue;
            Console.WriteLine($"[DEBUG] [SpectreJob] hashCoinbaseBytesValue {hashCoinbaseBytesValue} / blockTargetValue {blockTargetValue} Block Candidate: {isBlockCandidate}, Ratio: {ratio}");

            if (!isBlockCandidate && ratio < 0.99)
            {
                if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    ratio = shareDiff / context.PreviousDifficulty.Value;
                    if (ratio < 0.99)
                        throw new StratumException(StratumError.LowDifficultyShare, $"[ERROR] [SpectreJob] Low difficulty share ({shareDiff})");

                    stratumDifficulty = context.PreviousDifficulty.Value;
                    Console.WriteLine($"[DEBUG] [SpectreJob] Used previous difficulty: {stratumDifficulty}");
                }
                else
                {
                    throw new StratumException(StratumError.LowDifficultyShare, $"[ERROR] [SpectreJob] Low difficulty share ({shareDiff})");
                }
            }

            var result = new Share
            {
                BlockHeight = (long)BlockTemplate.Header.DaaScore,
                NetworkDifficulty = Difficulty,
                Difficulty = context.Difficulty / 256
            };

            if (isBlockCandidate)
            {
                var hashBytes = SerializeHeader(BlockTemplate.Header, false);
                result.IsBlockCandidate = true;
                result.BlockHash = hashBytes.ToHexString();
                Console.WriteLine($"[DEBUG] [SpectreJob] Block candidate hash: {result.BlockHash}");
            }

            Console.WriteLine($"[DEBUG] [SpectreJob] ProcessShareInternal completed. Share Difficulty: {result.Difficulty}");
            return result;
        }

        public override void Init(kaspad.RpcBlock blockTemplate, string jobId, double ShareMultiplier)
        {
            Console.WriteLine($"[DEBUG] [SpectreJob] Init called. Job ID: {jobId}, ShareMultiplier: {ShareMultiplier}");
            Contract.RequiresNonNull(blockTemplate);
            Contract.RequiresNonNull(jobId);

            JobId = jobId;
            //this.ShareMultiplier = ShareMultiplier;
            this.ShareMultiplier = 256;


            var target = new Target(KaspaUtils.CompactToBig(blockTemplate.Header.Bits));
            Difficulty = KaspaUtils.TargetToDifficulty(target.ToBigInteger()) * (double)SpectreConstants.MinHash;
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

            Console.WriteLine($"[DEBUG] [SpectreJob] Init completed. Job Params: {string.Join(", ", jobParams)}");
        }
    }
}
