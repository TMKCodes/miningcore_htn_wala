namespace Miningcore.Blockchain.Kaspa.Configuration;

public class KaspaPaymentProcessingConfigExtra
{
    /// <summary>
    /// Password for unlocking wallet
    /// </summary>
    public string WalletPassword { get; set; }

    /// <summary>
    /// Minimum block confirmations
    /// Default: 10
    /// </summary>
    public int? MinimumConfirmations { get; set; }

    
    /// <summary>
    /// kaspawallet daemon version which enables MaxFee (KASPA: "v0.12.18-rc5")
    /// </summary>
    public string VersionEnablingMaxFee { get; set; }

    /// <summary>
    /// Maximum number of payouts which can be done in parallel
    /// Default: 2
    /// /// Maximum amount you're willing to pay (in SOMPI)
    /// Default: 20000 (0.0002 KAS)
    /// </summary>
    //public int? MaxDegreeOfParallelPayouts { get; set; }
    public ulong? MaxFee { get; set; }
}
