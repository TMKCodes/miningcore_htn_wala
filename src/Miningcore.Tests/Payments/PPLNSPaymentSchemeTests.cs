using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PPLNSPaymentSchemeTests
{
  [Fact]
  public async Task UpdateBalancesAsync_DoesNotSkipSharesWithSameCreatedTimestampAcrossPages()
  {
    var cf = Substitute.For<IConnectionFactory>();
    var shareRepo = Substitute.For<IShareRepository>();
    var blockRepo = Substitute.For<IBlockRepository>();
    var balanceRepo = Substitute.For<IBalanceRepository>();
    var payoutHandler = Substitute.For<IPayoutHandler>();
    var pool = Substitute.For<IMiningPool>();
    var con = Substitute.For<IDbConnection>();
    var tx = Substitute.For<IDbTransaction>();

    var poolConfig = new PoolConfig
    {
      Id = "htn",
      PaymentProcessing = new PoolPaymentProcessingConfig
      {
        PayoutScheme = PayoutScheme.PPLNS,
        // Force needing > 50_000 shares (page size) to finish the window.
        PayoutSchemeConfig = JToken.FromObject(new { Factor = 50001m }),
      },
    };

    var block = new Block
    {
      BlockHeight = 100,
      Created = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
    };

    // Create 50_001 shares, all with the exact same Created timestamp.
    // Old paging (created < before) would miss the 50_001st share.
    var allShares = Enumerable.Range(0, 50001)
        .Select(_ => new Share
        {
          PoolId = poolConfig.Id,
          Miner = "miner-1",
          Worker = "w",
          Difficulty = 100,
          NetworkDifficulty = 100,
          Created = block.Created,
        })
        .ToArray();

    pool.Config.Returns(poolConfig);
    cf.OpenConnectionAsync().Returns(Task.FromResult(con));

    payoutHandler.AdjustShareDifficulty(Arg.Any<double>()).Returns(callInfo => (double)callInfo[0]);
    payoutHandler.FormatAmount(Arg.Any<decimal>()).Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

    balanceRepo.AddAmountAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string[]>())
      .Returns(Task.FromResult(1));

    // Use offset paging to return deterministic pages from our in-memory share list.
    shareRepo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), poolConfig.Id, block.Created, true, 50000, Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(callInfo =>
        {
          var pageOffset = (int)callInfo[5];
          var page = allShares.Skip(pageOffset).Take(50000).ToArray();
          return Task.FromResult(page);
        });

    // Ensure we don't try to delete shares in this test.
    shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult(0L));

    // blockReward == window makes per-share reward exactly 1 and total payout easy to assert.
    var blockReward = 50001m;

    var subject = new PPLNSPaymentScheme(cf, shareRepo, blockRepo, balanceRepo);

    await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, blockReward, CancellationToken.None);

    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-1", blockReward,
        Arg.Any<string>(), Arg.Any<string[]>());
  }
}
