using Miningcore.Blockchain.Scash;
using Miningcore.Extensions;
using Miningcore.Native;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;

namespace Miningcore.Crypto
{
    public class RandomXService : RandomX, IHashAlgorithm
    {
        private static readonly object LockObject = new();
        private static readonly ConcurrentDictionary<int, RandomX.RxVm> vmCache = new();

        /// <summary>
        /// Vérifie la preuve de travail RandomX.
        /// </summary>
        internal static bool CheckProofOfWorkRandomX(byte[] headerBytes, Span<byte> headerHash, string seedHex, BigInteger target)
{
    Console.WriteLine($"elva Debug ScashJob -----> CheckProofOfWorkRandomX: Début de la vérification PoW RandomX.");
    Console.WriteLine($"elva Debug ScashJob -----> SeedHex: {seedHex}, Target: {target}");

    try
    {
        if (target <= 0)
        {
            Console.WriteLine($"elva Error ScashJob -----> La cible est invalide (<= 0).");
            return false;
        }

        var epoch = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600);
        Console.WriteLine($"elva Debug ScashJob -----> Epoch: {epoch}");

        // ✅ Création d'une instance de RandomXService
        var randomXService = new RandomXService();

        var seed = RandomX.GetSeed("scash", seedHex);
        if (seed == null)
        {
            Console.WriteLine($"elva Error ScashJob -----> Impossible d'obtenir un seed pour SeedHex={seedHex}.");
            return false;
        }

        Span<byte> calculatedHash = stackalloc byte[32];
        RandomX.CalculateHash("scash", seedHex, headerBytes, calculatedHash);

        Console.WriteLine($"elva Debug ScashJob -----> CalculatedHash: {calculatedHash.ToHexString()}");

        if (!calculatedHash.SequenceEqual(headerHash))
        {
            Console.WriteLine($"elva Error ScashJob -----> Les hachages ne correspondent pas. HeaderHash: {headerHash.ToHexString()}, CalculatedHash: {calculatedHash.ToHexString()}");
            return false;
        }

        var headerValue = new BigInteger(calculatedHash, isBigEndian: true, isUnsigned: true);
        if (headerValue > target)
        {
            Console.WriteLine($"elva Error ScashJob -----> Le hash dépasse la cible. HeaderValue: {headerValue}, Target: {target}");
            return false;
        }

        Console.WriteLine($"elva Debug ScashJob -----> CheckProofOfWorkRandomX: Preuve de travail valide.");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"elva Error ScashJob -----> Exception lors de la vérification PoW RandomX: {ex.Message}");
        throw;
    }
    finally
    {
        Console.WriteLine($"elva Debug ScashJob -----> CheckProofOfWorkRandomX: Fin de la vérification.");
    }
}

        /// <summary>
        /// Récupère une VM RandomX pour un epoch spécifique.
        /// </summary>
        private static RandomX.RxVm GetVM(uint epoch)
        {
            lock (LockObject)
            {
                if (vmCache.TryGetValue((int)epoch, out var vm))
                {
                    Console.WriteLine($"elva Debug ScashJob -----> VM trouvée en cache pour epoch {epoch}.");
                    return vm;
                }

                Console.WriteLine($"elva Debug ScashJob -----> Création d'une nouvelle VM RandomX pour epoch {epoch}.");

                var flags = RandomX.randomx_flags.RANDOMX_FLAG_DEFAULT | RandomX.randomx_flags.RANDOMX_FLAG_FULL_MEM;
                var vmInstance = new RandomX.RxVm();
                vmInstance.Init(GetSeedHex(epoch).HexToByteArray(), flags);

                vmCache[(int)epoch] = vmInstance;
                Console.WriteLine($"elva Debug ScashJob -----> Nouvelle VM RandomX ajoutée au cache pour epoch {epoch}.");
                return vmInstance;
            }
        }

        /// <summary>
        /// Génère un seed hexadécimal pour un epoch donné.
        /// </summary>
        private static string GetSeedHex(uint epoch)
        {
            return $"Scash/RandomX/Epoch/{epoch}";
        }

        /// <summary>
        /// Crée un seed RandomX.
        /// </summary>
        public void CreateSeed(string realm, string seedHex, RandomX.randomx_flags? flagsOverride = null, int vmCount = 1)
        {
            Console.WriteLine($"elva Debug ScashJob -----> CreateSeed: Realm={realm}, SeedHex={seedHex}, VmCount={vmCount}");

            RandomX.CreateSeed(realm, seedHex, flagsOverride ?? RandomX.randomx_flags.RANDOMX_FLAG_DEFAULT, null, vmCount);

            Console.WriteLine($"elva Debug ScashJob -----> CreateSeed: Seed créé avec succès pour Realm={realm}, SeedHex={seedHex}");
        }


        /// <summary>
        /// Supprime un seed RandomX.
        /// </summary>
        public new void DeleteSeed(string realm, string seedHex)
        {
            Console.WriteLine($"elva Debug ScashJob -----> DeleteSeed: Realm={realm}, SeedHex={seedHex}");
            RandomX.DeleteSeed(realm, seedHex);
        }

        /// <summary>
        /// Récupère le seed RandomX.
        /// </summary>
        public new Tuple<RandomX.GenContext, BlockingCollection<RandomX.RxVm>> GetSeed(string realm, string seedHex)
        {
            Console.WriteLine($"elva Debug ScashJob -----> GetSeed: Realm={realm}, SeedHex={seedHex}");
            return RandomX.GetSeed(realm, seedHex);
        }

        /// <summary>
        /// Verrouille une action pour éviter les accès concurrents.
        /// </summary>
        public new void WithLock(Action action)
        {
            Console.WriteLine($"elva Debug ScashJob -----> WithLock: Début de l'exécution verrouillée.");
            RandomX.WithLock(action);
        }

        /// <summary>
        /// Calcule un hash avec des paramètres supplémentaires.
        /// </summary>
        public new void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            Console.WriteLine($"elva Debug ScashJob -----> Digest: Début du calcul avec paramètres supplémentaires.");

            if (extra.Length < 2)
                throw new ArgumentException("Digest nécessite un realm et un seedHex.");

            string realm = extra[0] as string ?? throw new InvalidCastException("Le premier paramètre doit être un string (realm).");
            string seedHex = extra[1] as string ?? throw new InvalidCastException("Le second paramètre doit être un string (seedHex).");

            RandomX.CalculateHash(realm, seedHex, data, result);

            Console.WriteLine($"elva Debug ScashJob -----> Digest: Hachage calculé avec succès.");
        }
    }
}
