using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Miningcore.Contracts;
using Miningcore.Extensions;
using NBitcoin;

namespace Miningcore.Blockchain.Bitcoin.AuxPow;

public static class AuxPowUtils
{
  // "Merged mining" header magic as used by AuxPoW chains (fabe6d6d)
  private static readonly byte[] Magic = { 0xfa, 0xbe, 0x6d, 0x6d };

  public static byte[] BuildCoinbaseCommitment(byte[] chainMerkleRootLE, uint merkleTreeSize, uint merkleNonce)
  {
    Contract.RequiresNonNull(chainMerkleRootLE);
    Contract.Requires<ArgumentException>(chainMerkleRootLE.Length == 32);

    var result = new byte[4 + 32 + 4 + 4];
    Buffer.BlockCopy(Magic, 0, result, 0, 4);
    Buffer.BlockCopy(chainMerkleRootLE, 0, result, 4, 32);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), merkleTreeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), merkleNonce);
    return result;
  }

  /// <summary>
  /// Computes the AuxPoW merkle index for a chainId within a tree of given size.
  /// This matches the common Namecoin-style LCG used by AuxPoW implementations.
  /// </summary>
  public static uint ComputeMerkleIndex(uint chainId, uint nonce, uint merkleTreeSize)
  {
    Contract.Requires<ArgumentException>(merkleTreeSize > 0);

    unchecked
    {
      uint rand = nonce;
      rand = rand * 1103515245 + 12345;
      rand += chainId;
      rand = rand * 1103515245 + 12345;
      return rand % merkleTreeSize;
    }
  }

  public static AuxPowJobData BuildAuxPowJobData(IEnumerable<(string CoinId, string AuxHashHex, uint ChainId, uint256 TargetValue)> chains,
      uint? fixedNonce = null)
  {
    Contract.RequiresNonNull(chains);

    var chainList = chains.ToList();
    if (chainList.Count == 0)
      return null;

    // smallest power-of-two >= count
    uint size = 1;
    while (size < chainList.Count)
      size <<= 1;

    // find nonce with non-colliding indices
    uint nonce = fixedNonce ?? (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

    const int maxAttempts = 10_000;
    for (var attempt = 0; attempt < maxAttempts; attempt++)
    {
      var used = new HashSet<uint>();
      var indices = chainList
          .Select(x => ComputeMerkleIndex(x.ChainId, nonce, size))
          .ToArray();

      var collision = false;
      foreach (var idx in indices)
      {
        if (!used.Add(idx))
        {
          collision = true;
          break;
        }
      }

      if (!collision)
      {
        // build sparse leaf set
        var leaves = Enumerable.Repeat(new byte[32], (int)size).ToArray();

        for (var i = 0; i < chainList.Count; i++)
        {
          var leafIndex = indices[i];
          var leafHash = uint256.Parse(chainList[i].AuxHashHex).ToBytes(); // little-endian
          leaves[leafIndex] = leafHash;
        }

        var (root, branchesByIndex) = BuildMerkleRootAndBranches(leaves);

        var works = new List<AuxChainWork>(chainList.Count);
        for (var i = 0; i < chainList.Count; i++)
        {
          var idx = indices[i];
          works.Add(new AuxChainWork
          {
            CoinId = chainList[i].CoinId,
            AuxBlockHash = chainList[i].AuxHashHex,
            ChainId = chainList[i].ChainId,
            TargetValue = chainList[i].TargetValue,
            MerkleIndex = idx,
            MerkleBranch = branchesByIndex[idx],
          });
        }

        return new AuxPowJobData
        {
          MerkleTreeSize = size,
          MerkleNonce = nonce,
          ChainMerkleRoot = root,
          Chains = works,
        };
      }

      nonce++;
    }

    throw new InvalidOperationException($"Unable to find AuxPoW nonce without merkle index collisions after {maxAttempts} attempts");
  }

  /// <summary>
  /// Returns merkle root (LE) and per-leaf branch list.
  /// </summary>
  private static (byte[] Root, Dictionary<uint, IList<byte[]>> BranchesByIndex) BuildMerkleRootAndBranches(byte[][] leaves)
  {
    Contract.RequiresNonNull(leaves);
    Contract.Requires<ArgumentException>(leaves.Length > 0);

    // Validate power-of-two
    if ((leaves.Length & (leaves.Length - 1)) != 0)
      throw new ArgumentException("leaves length must be a power-of-two");

    var branches = new Dictionary<uint, IList<byte[]>>(leaves.Length);

    // Precompute branches for every index
    for (uint leafIndex = 0; leafIndex < leaves.Length; leafIndex++)
    {
      var index = leafIndex;
      var level = leaves.Select(x => x).ToArray();
      var branch = new List<byte[]>();

      while (level.Length > 1)
      {
        var siblingIndex = (int)(index ^ 1);
        branch.Add(level[siblingIndex]);

        // build next level
        var next = new byte[level.Length / 2][];
        for (var i = 0; i < level.Length; i += 2)
        {
          next[i / 2] = DoubleSha256(level[i].Concat(level[i + 1]).ToArray());
        }

        level = next;
        index >>= 1;
      }

      branches[leafIndex] = branch;
    }

    // Compute root
    var current = leaves.Select(x => x).ToArray();
    while (current.Length > 1)
    {
      var next = new byte[current.Length / 2][];
      for (var i = 0; i < current.Length; i += 2)
        next[i / 2] = DoubleSha256(current[i].Concat(current[i + 1]).ToArray());
      current = next;
    }

    return (current[0], branches);
  }

  private static byte[] DoubleSha256(byte[] data)
  {
    using var sha256 = SHA256.Create();
    var first = sha256.ComputeHash(data);
    return sha256.ComputeHash(first);
  }

  public static byte[] SerializeAuxPow(byte[] coinbaseTx,
      IList<byte[]> coinbaseMerkleBranch,
      int coinbaseMerkleIndex,
      byte[] parentBlockHeader,
      IList<byte[]> chainMerkleBranch,
      int chainMerkleIndex)
  {
    Contract.RequiresNonNull(coinbaseTx);
    Contract.RequiresNonNull(parentBlockHeader);
    Contract.RequiresNonNull(coinbaseMerkleBranch);
    Contract.RequiresNonNull(chainMerkleBranch);
    Contract.Requires<ArgumentException>(parentBlockHeader.Length == 80);

    using var stream = new MemoryStream();
    var bs = new BitcoinStream(stream, true);

    // Coinbase tx (CTransaction)
    bs.ReadWrite(coinbaseTx);

    // vMerkleBranch (coinbase -> parent merkle root)
    var count = (uint)coinbaseMerkleBranch.Count;
    bs.ReadWriteAsVarInt(ref count);
    foreach (var step in coinbaseMerkleBranch)
    {
      if (step?.Length != 32)
        throw new ArgumentException("coinbase merkle branch elements must be 32 bytes");
      bs.ReadWrite(step);
    }

    // nIndex
    var idx = coinbaseMerkleIndex;
    bs.ReadWrite(ref idx);

    // parent block header
    bs.ReadWrite(parentBlockHeader);

    // vChainMerkleBranch (aux hash -> chain merkle root)
    var chainCount = (uint)chainMerkleBranch.Count;
    bs.ReadWriteAsVarInt(ref chainCount);
    foreach (var step in chainMerkleBranch)
    {
      if (step?.Length != 32)
        throw new ArgumentException("chain merkle branch elements must be 32 bytes");
      bs.ReadWrite(step);
    }

    // nChainIndex
    var chainIdx = chainMerkleIndex;
    bs.ReadWrite(ref chainIdx);

    return stream.ToArray();
  }
}
