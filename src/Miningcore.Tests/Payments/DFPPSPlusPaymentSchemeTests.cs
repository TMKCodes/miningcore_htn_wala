using System;
using System.Data;
using System.Globalization;
using System.IO;
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
using Xunit;

namespace Miningcore.Tests.Payments;

public class DFPPSPlusPaymentSchemeTests
{
  [Fact]
  public async Task UpdateBalancesAsync_PaysExpectedValueWithoutSubtractingFeeTwice()
  {
    var cf = Substitute.For<IConnectionFactory>();
    var blockRepo = Substitute.For<IBlockRepository>();
    var shareRepo = Substitute.For<IShareRepository>();
    var balanceRepo = Substitute.For<IBalanceRepository>();
    var payoutHandler = Substitute.For<IPayoutHandler>();
    var pool = Substitute.For<IMiningPool>();
    var con = Substitute.For<IDbConnection>();
    var tx = Substitute.For<IDbTransaction>();

    var poolConfig = new PoolConfig
    {
      Id = "htn",
      Address = "pool-wallet",
      RewardRecipients = new[]
        {
                new RewardRecipient { Address = "fee-wallet", Percentage = 3m, Type = "op" },
            },
    };

    var block = new Block
    {
      BlockHeight = 123,
      Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
      NetworkDifficulty = 1000,
    };

    var shares = new[]
    {
        new Share { Miner = "miner-a", Difficulty = 250, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-2) },
        new Share { Miner = "miner-b", Difficulty = 750, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-1) },
    };

    pool.Config.Returns(poolConfig);
    cf.OpenConnectionAsync().Returns(Task.FromResult(con));

    shareRepo.StreamSharesBefore(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), true)
      .Returns(shares);

    payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
        .Returns(callInfo => (double)callInfo[0]);
    payoutHandler.FormatAmount(Arg.Any<decimal>())
        .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

    var subject = new DFPPSPlusPaymentScheme(cf, blockRepo, shareRepo, balanceRepo);

    await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 970m, CancellationToken.None);

    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-a", 242.5m,
        Arg.Any<string>(), Arg.Any<string[]>());
    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-b", 727.5m,
        Arg.Any<string>(), Arg.Any<string[]>());
    await balanceRepo.DidNotReceive().AddAmountAsync(con, tx, poolConfig.Id, "fee-wallet", Arg.Any<decimal>(),
        Arg.Any<string>(), Arg.Any<string[]>());
  }

  [Fact]
  public async Task UpdateBalancesAsync_CanCreditMoreThanCurrentBlockRewardWhenPoolWasUnlucky()
  {
    var cf = Substitute.For<IConnectionFactory>();
    var blockRepo = Substitute.For<IBlockRepository>();
    var shareRepo = Substitute.For<IShareRepository>();
    var balanceRepo = Substitute.For<IBalanceRepository>();
    var payoutHandler = Substitute.For<IPayoutHandler>();
    var pool = Substitute.For<IMiningPool>();
    var con = Substitute.For<IDbConnection>();
    var tx = Substitute.For<IDbTransaction>();

    var poolConfig = new PoolConfig
    {
      Id = "htn",
      Address = "pool-wallet",
      RewardRecipients = new[]
        {
                new RewardRecipient { Address = "fee-wallet", Percentage = 1m, Type = "op" },
            },
    };

    var block = new Block
    {
      BlockHeight = 124,
      Created = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
      NetworkDifficulty = 1000,
    };

    var shares = new[]
    {
            new Share { Miner = "miner-a", Difficulty = 300, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-2) },
            new Share { Miner = "miner-b", Difficulty = 800, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-1) },
        };

    pool.Config.Returns(poolConfig);
    cf.OpenConnectionAsync().Returns(Task.FromResult(con));

    shareRepo.StreamSharesBefore(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), true)
      .Returns(shares);

    payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
        .Returns(callInfo => (double)callInfo[0]);
    payoutHandler.FormatAmount(Arg.Any<decimal>())
        .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

    var subject = new DFPPSPlusPaymentScheme(cf, blockRepo, shareRepo, balanceRepo);

    await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 1000m, CancellationToken.None);

    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-b", 800m,
        Arg.Any<string>(), Arg.Any<string[]>());
    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-a", 300m,
        Arg.Any<string>(), Arg.Any<string[]>());
  }

  [Fact]
  public async Task UpdateBalancesAsync_WarnsWhenCreditingMoreThanReward()
  {
    var cf = Substitute.For<IConnectionFactory>();
    var blockRepo = Substitute.For<IBlockRepository>();
    var shareRepo = Substitute.For<IShareRepository>();
    var balanceRepo = Substitute.For<IBalanceRepository>();
    var payoutHandler = Substitute.For<IPayoutHandler>();
    var pool = Substitute.For<IMiningPool>();
    var con = Substitute.For<IDbConnection>();
    var tx = Substitute.For<IDbTransaction>();

    var poolConfig = new PoolConfig
    {
      Id = "htn",
      Address = "pool-wallet",
    };

    var block = new Block
    {
      BlockHeight = 125,
      Created = new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc),
      NetworkDifficulty = 1000,
    };

    var shares = new[]
    {
            new Share { Miner = "miner-a", Difficulty = 1100, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-1) },
        };

    pool.Config.Returns(poolConfig);
    cf.OpenConnectionAsync().Returns(Task.FromResult(con));

    shareRepo.StreamSharesBefore(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), true)
      .Returns(shares);

    payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
        .Returns(callInfo => (double)callInfo[0]);
    payoutHandler.FormatAmount(Arg.Any<decimal>())
        .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

    var originalOut = Console.Out;
    try
    {
      using var sw = new StringWriter();
      Console.SetOut(sw);

      var subject = new DFPPSPlusPaymentScheme(cf, blockRepo, shareRepo, balanceRepo);
      await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 1m, CancellationToken.None);

      var output = sw.ToString();
      Assert.Contains("[DFPPS+ Payment] Payout: Total credited", output);
    }
    finally
    {
      Console.SetOut(originalOut);
    }
  }

  [Fact]
  public async Task UpdateBalancesAsync_BatchesConfirmedBlocksIntoSingleShareScanAndReturnsDeferredCleanupCutoff()
  {
    var cf = Substitute.For<IConnectionFactory>();
    var blockRepo = Substitute.For<IBlockRepository>();
    var shareRepo = Substitute.For<IShareRepository>();
    var balanceRepo = Substitute.For<IBalanceRepository>();
    var payoutHandler = Substitute.For<IPayoutHandler>();
    var pool = Substitute.For<IMiningPool>();
    var con = Substitute.For<IDbConnection>();
    var tx = Substitute.For<IDbTransaction>();

    var poolConfig = new PoolConfig
    {
      Id = "htn",
      Address = "pool-wallet",
    };

    var firstBlock = new Block
    {
      BlockHeight = 201,
      Created = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc),
      NetworkDifficulty = 1000,
    };

    var secondBlock = new Block
    {
      BlockHeight = 202,
      Created = new DateTime(2026, 1, 1, 0, 0, 20, DateTimeKind.Utc),
      NetworkDifficulty = 1000,
    };

    var previousConfirmed = new Block
    {
      BlockHeight = 200,
      Created = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc),
      NetworkDifficulty = 1000,
    };

    var shares = new[]
    {
      new Share { Miner = "miner-a", Difficulty = 250, NetworkDifficulty = 1000, Created = firstBlock.Created.AddSeconds(-5) },
      new Share { Miner = "miner-b", Difficulty = 750, NetworkDifficulty = 1000, Created = firstBlock.Created.AddSeconds(-1) },
      new Share { Miner = "miner-c", Difficulty = 500, NetworkDifficulty = 1000, Created = secondBlock.Created.AddSeconds(-1) },
    };

    pool.Config.Returns(poolConfig);
    cf.OpenConnectionAsync().Returns(Task.FromResult(con));

    blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, Arg.Any<BlockStatus[]>(), firstBlock.Created)
      .Returns(previousConfirmed);

    shareRepo.StreamSharesBetween(Arg.Any<IDbConnection>(), poolConfig.Id, previousConfirmed.Created, secondBlock.Created, true, true)
      .Returns(shares.OrderBy(x => x.Created));

    payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
      .Returns(callInfo => (double) callInfo[0]);
    payoutHandler.FormatAmount(Arg.Any<decimal>())
      .Returns(callInfo => ((decimal) callInfo[0]).ToString(CultureInfo.InvariantCulture));

    var subject = new DFPPSPlusPaymentScheme(cf, blockRepo, shareRepo, balanceRepo);

    var result = await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler,
      new[]
      {
        new ConfirmedBlockPayout(firstBlock, 1000m),
        new ConfirmedBlockPayout(secondBlock, 1000m),
      }, CancellationToken.None);

    shareRepo.Received(1).StreamSharesBetween(con, poolConfig.Id, previousConfirmed.Created, secondBlock.Created, true, true);
    await shareRepo.DidNotReceive().DeleteSharesBeforeAsync(con, tx, poolConfig.Id,
      Arg.Any<DateTime>(), CancellationToken.None);
    Assert.Equal(secondBlock.Created.AddTicks(10), result.ProcessedShareCutoff);

    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-a", 250m,
      Arg.Any<string>(), Arg.Any<string[]>());
    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-b", 750m,
      Arg.Any<string>(), Arg.Any<string[]>());
    await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-c", 500m,
      Arg.Any<string>(), Arg.Any<string[]>());
  }
}
