using System;
using System.Collections.Generic;
using Autofac;
using JetBrains.Annotations;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Crypto.Hashing.Progpow;
using Newtonsoft.Json;
using System.ComponentModel;
using NBitcoin;
using System.Globalization;
using System.Numerics;
using Miningcore.Blockchain.Scash;


namespace Miningcore.Configuration
{
    public abstract partial class CoinTemplate
    {

        public T As<T>() where T : CoinTemplate
        {
            return (T)this;
        }

        /*      public static T As<T>(this CoinTemplate coin) where T : CoinTemplate
              {
                  if (coin is T template)
                      return template;
                  throw new InvalidCastException($"Unable to cast object of type '{coin.GetType()}' to type '{typeof(T)}'");
              }
      */

        /// <summary>
        /// JSON source file where this template originated from
        /// </summary>
        [JsonIgnore]
        public string Source { get; set; }

        /// <summary>
        /// Returns the algorithm name for the coin
        /// </summary>
        public abstract string GetAlgorithmName();
    }

    public partial class AlephiumCoinTemplate : CoinTemplate
    {
        public override string GetAlgorithmName()
        {
            return "Blake3";
        }
    }

    public partial class BeamCoinTemplate : CoinTemplate
    {
        public override string GetAlgorithmName()
        {
            return "BeamHash";
        }
    }

    public partial class BitcoinTemplate
    {
        public BitcoinNetworkParams GetNetwork(ChainName chain)
        {
            if (Networks == null || Networks.Count == 0)
                return null;

            if (chain == ChainName.Mainnet)
                return Networks["main"];
            else if (chain == ChainName.Testnet)
                return Networks["test"];
            else if (chain == ChainName.Regtest)
                return Networks["regtest"];

            throw new NotSupportedException("unsupported network type");
        }

        #region Overrides of CoinTemplate

        public override string GetAlgorithmName()
        {
            switch (Symbol)
            {
                case "HNS":
                    return HeaderHasherValue.GetType().Name + " + " + ShareHasherValue.GetType().Name;
                case "KCN":
                    return HeaderHasherValue.GetType().Name;
                default:
                    var hash = HeaderHasherValue;

                    if (hash.GetType() == typeof(DigestReverser))
                        return ((DigestReverser)hash).Upstream.GetType().Name;

                    return hash.GetType().Name;
            }
        }

        #endregion
    }

    /*
    public partial class ScashTemplate : CoinTemplate
        {
            /// <summary>
            /// Retourne le nom de l'algorithme de hachage utilisé par SCASH.
            /// </summary>
            public override string GetAlgorithmName()
            {
                return Algorithm;
            }

            /// <summary>
            /// Algorithme de hachage pour Scash.
            /// </summary>
            [JsonProperty("algorithm")]
            public string Algorithm { get; set; } = "randomx";

            /// <summary>
            /// Domaine spécifique pour RandomX.
            /// </summary>
            [JsonProperty("RandomXRealm")]
            public string RandomXRealm { get; set; } = "default-realm";

            /// <summary>
            /// Indique si l'algorithme est basé sur RandomX.
            /// </summary>
            [JsonIgnore]
            public bool IsRandomX => Algorithm.Equals("randomx", StringComparison.OrdinalIgnoreCase);
        }
    */



    public partial class ScashTemplate : CoinTemplate
    {


        /// <summary>
        /// Retourne le nom de l'algorithme de hachage utilisé par SCASH.
        /// </summary>
        public override string GetAlgorithmName()
        {
            return Algorithm;
        }

        [JsonProperty("algorithm")]
        public string Algorithm { get; set; } = "randomx";

        [JsonProperty("RandomXRealm")]
        public string RandomXRealm { get; set; } = "default-realm";

        [JsonIgnore]
        public bool IsRandomX => Algorithm.Equals("randomx", StringComparison.OrdinalIgnoreCase);

        [JsonProperty("supportsSegWit")]
        public bool SupportsSegWit { get; set; } = true;

        [JsonProperty("bech32Prefix")]
        public string Bech32Prefix { get; set; } = "scash";

        [JsonIgnore]
        public bool IsBech32 => !string.IsNullOrEmpty(Bech32Prefix);

        public override string ToString()
        {
            return $"Algorithm: {Algorithm}, RandomXRealm: {RandomXRealm}, SupportsSegWit: {SupportsSegWit}, Bech32Prefix: {Bech32Prefix}";
        }
    }













    public partial class EquihashCoinTemplate
    {
        public partial class EquihashNetworkParams
        {
            public EquihashNetworkParams()
            {
                diff1Value = new Lazy<Org.BouncyCastle.Math.BigInteger>(() =>
                {
                    if (string.IsNullOrEmpty(Diff1))
                        throw new InvalidOperationException("Diff1 has not yet been initialized");

                    return new Org.BouncyCastle.Math.BigInteger(Diff1, 16);
                });

                diff1BValue = new Lazy<BigInteger>(() =>
                {
                    if (string.IsNullOrEmpty(Diff1))
                        throw new InvalidOperationException("Diff1 has not yet been initialized");

                    return BigInteger.Parse(Diff1, NumberStyles.HexNumber);
                });
            }

            private readonly Lazy<Org.BouncyCastle.Math.BigInteger> diff1Value;
            private readonly Lazy<BigInteger> diff1BValue;

            [JsonIgnore]
            public Org.BouncyCastle.Math.BigInteger Diff1Value => diff1Value.Value;

            [JsonIgnore]
            public BigInteger Diff1BValue => diff1BValue.Value;

            [JsonIgnore]
            public ulong FoundersRewardSubsidySlowStartShift => FoundersRewardSubsidySlowStartInterval / 2;

            [JsonIgnore]
            public ulong LastFoundersRewardBlockHeight => FoundersRewardSubsidyHalvingInterval + FoundersRewardSubsidySlowStartShift - 1;
        }

        public EquihashNetworkParams GetNetwork(ChainName chain)
        {
            if (chain == ChainName.Mainnet)
                return Networks["main"];
            else if (chain == ChainName.Testnet)
                return Networks["test"];
            else if (chain == ChainName.Regtest)
                return Networks["regtest"];

            throw new NotSupportedException("unsupported network type");
        }

        #region Overrides of CoinTemplate

        public override string GetAlgorithmName()
        {
            switch (Symbol)
            {
                case "VRSC":
                    return "Verushash";
                default:
                    // TODO: return variant
                    return "Equihash";
            }
        }

        #endregion
    }

    public partial class EthereumCoinTemplate : CoinTemplate
    {
        private readonly Lazy<IEthashLight> ethashLightValue;

        public EthereumCoinTemplate()
        {
            ethashLightValue = new Lazy<IEthashLight>(() =>
            {
                if (string.IsNullOrEmpty(Symbol))
                    throw new InvalidOperationException("Symbol must be initialized before accessing Ethash.");

                return EthashFactory.GetEthash(Symbol, ComponentContext, Ethasher);
            });
        }

        public IComponentContext ComponentContext { get; [UsedImplicitly] init; }
        public IEthashLight Ethash => ethashLightValue.Value;

        public override string GetAlgorithmName()
        {
            return Ethash?.AlgoName ?? "Unknown";
        }

        public string Symbol { get; set; } = "ETH";
        //   public string Ethasher { get; set; } = "ethash";
    }

    public partial class KaspaCoinTemplate
    {
        #region Overrides of CoinTemplate

        public override string GetAlgorithmName()
        {
            // Ajouter un log initial pour signaler l'exécution de la méthode
            Console.WriteLine($"[INFO] Execution de GetAlgorithmName pour le Symbol : {Symbol}");

            switch (Symbol)
            {
                case "PUG":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : Hoohash");
                    return "Hoohash";

                case "WALA":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : WalaHash");
                    return "WalaHash";

                case "AIX":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : AstrixHash");
                    return "AstrixHash";

                case "KLS":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : Karlsenhashv2");
                    return "Karlsenhashv2";

                case "CSS":
                case "NTL":
                case "NXL":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : Karlsenhash");
                    return "Karlsenhash";

                case "CAS":
                case "PYI":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : Pyrinhash");
                    return "Pyrinhash";

                case "SPR":
                    Console.WriteLine($"[INFO] Symbol reconnu : {Symbol} - Algorithme : SpectreX");
                    return "SpectreX";

                default:
                    // Log détaillé pour capturer les cas non reconnus
                    Console.WriteLine($"[WARN] Symbol non reconnu : {Symbol}. Algorithme par défaut : kHeavyHash");
                    return "kHeavyHash";
            }
        }

        #endregion
    }


    public partial class HoosatCoinTemplate : CoinTemplate
    {
    }

    public partial class ProgpowCoinTemplate : BitcoinTemplate
    {
        #region Overrides of CoinTemplate

        public ProgpowCoinTemplate() : base()
        {
            progpowLightValue = new Lazy<IProgpowLight>(() =>
                ProgpowFactory.GetProgpow(Symbol, ComponentContext, Progpower));
        }

        private readonly Lazy<IProgpowLight> progpowLightValue;

        public IProgpowLight ProgpowHasher => progpowLightValue.Value;

        public override string GetAlgorithmName()
        {
            return ProgpowHasher.AlgoName;
        }

        #endregion
    }

    public partial class WarthogCoinTemplate : CoinTemplate
    {
        public override string GetAlgorithmName()
        {
            return "PoBW";
        }
    }

    public partial class ConcealCoinTemplate
    {
        #region Overrides of CoinTemplate

        public override string GetAlgorithmName()
        {
            //        switch(Hash)
            //        {
            //            case CryptonightHashType.RandomX:
            //                return "RandomX";
            //        }

            return Hash.ToString();
        }

        #endregion
    }

    public partial class CryptonoteCoinTemplate
    {
        #region Overrides of CoinTemplate

        public override string GetAlgorithmName()
        {
            //        switch(Hash)
            //        {
            //            case CryptonightHashType.RandomX:
            //                return "RandomX";
            //        }

            return Hash.ToString();
        }

        #endregion
    }

    public partial class ErgoCoinTemplate : CoinTemplate
    {
        public override string GetAlgorithmName()
        {
            return "Autolykos";
        }
    }

    public partial class PoolConfig
    {
    }
}
