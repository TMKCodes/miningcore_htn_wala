using System;
using System.Collections.Generic;
using NBitcoin;

namespace Miningcore.Blockchain.Bitcoin.AuxPow;

public sealed class AuxPowJobData
{
  public uint MerkleTreeSize { get; init; }
  public uint MerkleNonce { get; init; }
  public byte[] ChainMerkleRoot { get; init; } = null!; // 32 bytes, little-endian
  public IReadOnlyList<AuxChainWork> Chains { get; init; } = null!;
}

public sealed class AuxChainWork
{
  public string CoinId { get; init; } = null!;

  /// <summary>
  /// Aux block hash (display hex as returned by daemon).
  /// </summary>
  public string AuxBlockHash { get; init; } = null!;

  public uint ChainId { get; init; }

  /// <summary>
  /// Target threshold for aux chain (uint256 compare against parent PoW hash).
  /// </summary>
  public uint256 TargetValue { get; init; } = null!;

  /// <summary>
  /// Leaf index in the chain-merkle tree.
  /// </summary>
  public uint MerkleIndex { get; init; }

  /// <summary>
  /// Merkle branch proving aux hash -> chain merkle root.
  /// Each entry is a 32-byte hash (little-endian).
  /// </summary>
  public IList<byte[]> MerkleBranch { get; init; } = null!;
}

public sealed class AuxPowSubmission
{
  public string CoinId { get; init; } = null!;
  public string AuxBlockHash { get; init; } = null!;
  public string AuxPowHex { get; init; } = null!;
}
