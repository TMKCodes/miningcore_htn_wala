using System;
using System.Diagnostics;
using System.Linq;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Scash;

/// <summary>
/// Utilitaires pour le traitement des adresses SCASH.
/// </summary>
public static class ScashUtils
{
    /// <summary>
    /// Convertit une adresse SCASH en une destination NBitcoin appropriée.
    /// Supporte les formats :
    /// - P2PKH (Base58, commence par '1')
    /// - P2SH (Base58, commence par '3')
    /// - Bech32 (SegWit Native, commence par 'scash1')
    /// </summary>
    public static IDestination AddressToDestination(string address, Network expectedNetwork)
    {
        ValidateInputs(address, expectedNetwork);

        try
        {
            if (address.StartsWith("scash1", StringComparison.OrdinalIgnoreCase))
                return BechSegwitAddressToDestination(address, expectedNetwork);
            else if (address.StartsWith("1") || address.StartsWith("3"))
                return Base58AddressToDestination(address, expectedNetwork);
            else
                throw new FormatException($"Unsupported SCASH address format: {address}");
        }
        catch (Exception ex)
        {
            throw new FormatException($"Failed to parse SCASH address '{address}' on network '{expectedNetwork?.Name ?? "Unknown"}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gestion des adresses Base58 (P2PKH / P2SH) pour SCASH.
    /// </summary>
    public static IDestination Base58AddressToDestination(string address, Network expectedNetwork)
    {
        ValidateInputs(address, expectedNetwork);

        try
        {
            var decoded = Encoders.Base58Check.DecodeData(address);
            var pubkeyAddressPrefix = expectedNetwork.GetVersionBytes(Base58Type.PUBKEY_ADDRESS, true);
            var scriptAddressPrefix = expectedNetwork.GetVersionBytes(Base58Type.SCRIPT_ADDRESS, true);

            if (decoded.Take(pubkeyAddressPrefix.Length).SequenceEqual(pubkeyAddressPrefix))
                return new KeyId(decoded.Skip(pubkeyAddressPrefix.Length).ToArray());
            else if (decoded.Take(scriptAddressPrefix.Length).SequenceEqual(scriptAddressPrefix))
                return new ScriptId(decoded.Skip(scriptAddressPrefix.Length).ToArray());
            else
                throw new FormatException($"Invalid prefix for Base58 SCASH address: {address}");
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid Base58 SCASH address: {address}. Reason: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gestion des adresses Bech32 SegWit Native pour SCASH.
    /// </summary>
    public static IDestination BechSegwitAddressToDestination(string address, Network expectedNetwork)
{
    ValidateInputs(address, expectedNetwork);

    try
    {
        var encoder = expectedNetwork.GetBech32Encoder(Bech32Type.WITNESS_PUBKEY_ADDRESS, false);
        if (encoder == null)
            throw new InvalidOperationException($"Bech32 encoder for WITNESS_PUBKEY_ADDRESS is not configured in the network: {expectedNetwork.Name}");

        // Décodage de l'adresse Bech32
        var decoded = encoder.Decode(address, out var witVersion);

        if (witVersion != 0)
            throw new FormatException($"Unsupported witness version: {witVersion}");

        // Crée une destination témoin
        var result = new WitKeyId(decoded);

        // Reconstruit l'adresse et valide
        var reconstructedAddress = result.GetAddress(expectedNetwork).ToString();
        Console.WriteLine($"[DEBUG] Reconstructed Address: {reconstructedAddress}");
        Console.WriteLine($"[DEBUG] Original Address: {address}");

        if (!reconstructedAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Decoded address mismatch. Expected: {address}, Got: {reconstructedAddress}");

        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Failed to process Bech32 SCASH address: {address}. Reason: {ex.Message}");
        throw new FormatException($"Unexpected error processing Bech32 SCASH address: {address}. Reason: {ex.Message}", ex);
    }
}


    /// <summary>
    /// Valide si une adresse SCASH est correctement formatée.
    /// </summary>
    public static bool IsValidAddress(string address, Network expectedNetwork)
    {
        if (string.IsNullOrEmpty(address) || expectedNetwork == null)
            return false;

        try
        {
            AddressToDestination(address, expectedNetwork);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Retourne le type d'adresse SCASH.
    /// </summary>
    public static string GetAddressType(string address)
    {
        if (string.IsNullOrEmpty(address))
            return "Invalid";

        if (address.StartsWith("scash1", StringComparison.OrdinalIgnoreCase))
            return "Bech32 (SegWit Native)";
        else if (address.StartsWith("1"))
            return "P2PKH (Pay-to-PubKey-Hash)";
        else if (address.StartsWith("3"))
            return "P2SH (Pay-to-Script-Hash)";
        else
            return "Unknown";
    }

    /// <summary>
    /// Valide les entrées des méthodes d'adresse.
    /// </summary>
    private static void ValidateInputs(string address, Network expectedNetwork)
    {
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Address cannot be null or empty", nameof(address));

        if (expectedNetwork == null)
            throw new ArgumentNullException(nameof(expectedNetwork), "Network cannot be null");
    }
}
