using System;
using Miningcore.Native;

namespace Miningcore.Blockchain.Scash
{
    public interface IRandomX
    {
        void CreateSeed(string realm, string seedHex, RandomX.randomx_flags? flagsOverride = null, int vmCount = 1);
        void DeleteSeed(string realm, string seedHex);
        Tuple<RandomX.GenContext, System.Collections.Concurrent.BlockingCollection<RandomX.RxVm>> GetSeed(string realm, string seedHex);
        void WithLock(Action action);
        void CalculateHash(string realm, string seedHex, ReadOnlySpan<byte> data, Span<byte> result);
    }
}
