using System;
using System.Runtime.InteropServices;

namespace Miningcore.Blockchain.Hoosat
{
    public static class HoohashNative
    {
        [DllImport("libhoohash.so", EntryPoint = "CalculateProofOfWorkValueExported")]
        private static extern void NativeCalculateProofOfWorkValue(
            [In] byte[] prevHash,
            long timestamp,
            ulong nonce,
            [In] double[,] mat,
            [Out] byte[] output);

        public static void CalculateProofOfWork(byte[] prevHash, long timestamp, ulong nonce, double[,] mat, byte[] output)
        {

            NativeCalculateProofOfWorkValue(prevHash, timestamp, nonce, mat, output);
        }
    }
}
