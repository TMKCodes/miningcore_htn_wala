using System;
using System.Data;
using System.Globalization;
using System.IO;
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

public class FPPSPlusPaymentSchemeTests
{
    [Fact]
    public async Task UpdateBalancesAsync_PaysExpectedValueWithoutSubtractingFeeTwice()
    {
        var cf = Substitute.For<IConnectionFactory>();
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

        shareRepo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult((bool)callInfo[3] ? shares : Array.Empty<Share>()));
        shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0L));

        payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
            .Returns(callInfo => (double)callInfo[0]);
        payoutHandler.FormatAmount(Arg.Any<decimal>())
            .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

        var subject = new FPPSPlusPaymentScheme(cf, shareRepo, balanceRepo);

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

        shareRepo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult((bool)callInfo[3] ? shares : Array.Empty<Share>()));
        shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0L));

        payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
            .Returns(callInfo => (double)callInfo[0]);
        payoutHandler.FormatAmount(Arg.Any<decimal>())
            .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

        var subject = new FPPSPlusPaymentScheme(cf, shareRepo, balanceRepo);

        await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 990m, CancellationToken.None);

        await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-a", 297m,
            Arg.Any<string>(), Arg.Any<string[]>());
        await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-b", 792m,
            Arg.Any<string>(), Arg.Any<string[]>());
    }

    [Fact]
    public async Task UpdateBalancesAsync_LogsWarningWhenTotalCreditedExceedsBlockReward()
    {
        var originalOut = Console.Out;
        using var consoleOutput = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetOut(consoleOutput);

        try
        {
            var cf = Substitute.For<IConnectionFactory>();
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
                BlockHeight = 126,
                Created = new DateTime(2026, 1, 1, 0, 3, 0, DateTimeKind.Utc),
                NetworkDifficulty = 1000,
            };

            var shares = new[]
            {
                new Share { Miner = "miner-a", Difficulty = 300, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-2) },
                new Share { Miner = "miner-b", Difficulty = 800, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-1) },
            };

            pool.Config.Returns(poolConfig);
            cf.OpenConnectionAsync().Returns(Task.FromResult(con));

            shareRepo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult((bool)callInfo[3] ? shares : Array.Empty<Share>()));
            shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(0L));

            payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
                .Returns(callInfo => (double)callInfo[0]);
            payoutHandler.FormatAmount(Arg.Any<decimal>())
                .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

            var subject = new FPPSPlusPaymentScheme(cf, shareRepo, balanceRepo);

            await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 990m, CancellationToken.None);

            var output = consoleOutput.ToString();
            Assert.Contains("[FPPS+ Payment] Payout: Total credited", output);
            Assert.Contains("exceeds block reward 990", output);
            Assert.Contains("for block 126", output);

            await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-a", 297m,
                Arg.Any<string>(), Arg.Any<string[]>());
            await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-b", 792m,
                Arg.Any<string>(), Arg.Any<string[]>());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task UpdateBalancesAsync_DeletesSharesThroughLatestProcessedTimestamp()
    {
        var cf = Substitute.For<IConnectionFactory>();
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
            BlockHeight = 127,
            Created = new DateTime(2026, 1, 1, 0, 4, 0, DateTimeKind.Utc),
            NetworkDifficulty = 1000,
        };

        var latestShareCreated = block.Created.AddSeconds(-1);
        var shares = new[]
        {
            new Share { Miner = "miner-a", Difficulty = 250, NetworkDifficulty = 1000, Created = block.Created.AddSeconds(-2) },
            new Share { Miner = "miner-b", Difficulty = 750, NetworkDifficulty = 1000, Created = latestShareCreated },
        };

        pool.Config.Returns(poolConfig);
        cf.OpenConnectionAsync().Returns(Task.FromResult(con));

        shareRepo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult((bool)callInfo[3] ? shares : Array.Empty<Share>()));
        shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, latestShareCreated.AddTicks(10), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(2L));

        payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
            .Returns(callInfo => (double)callInfo[0]);
        payoutHandler.FormatAmount(Arg.Any<decimal>())
            .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

        var subject = new FPPSPlusPaymentScheme(cf, shareRepo, balanceRepo);

        await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 1000m, CancellationToken.None);

        await shareRepo.Received(1).CountSharesBeforeAsync(con, tx, poolConfig.Id, latestShareCreated.AddTicks(10), Arg.Any<CancellationToken>());
        await shareRepo.Received(1).DeleteSharesBeforeAsync(con, tx, poolConfig.Id, latestShareCreated.AddTicks(10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateBalancesAsync_UsesSolvedBlockDifficultyForExpectedValue()
    {
        var cf = Substitute.For<IConnectionFactory>();
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
            new Share { Miner = "miner-a", Difficulty = 250, NetworkDifficulty = 500, Created = block.Created.AddSeconds(-2) },
            new Share { Miner = "miner-b", Difficulty = 750, NetworkDifficulty = 500, Created = block.Created.AddSeconds(-1) },
        };

        pool.Config.Returns(poolConfig);
        cf.OpenConnectionAsync().Returns(Task.FromResult(con));

        shareRepo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult((bool)callInfo[3] ? shares : Array.Empty<Share>()));
        shareRepo.CountSharesBeforeAsync(con, tx, poolConfig.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0L));

        payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
            .Returns(callInfo => (double)callInfo[0]);
        payoutHandler.FormatAmount(Arg.Any<decimal>())
            .Returns(callInfo => ((decimal)callInfo[0]).ToString(CultureInfo.InvariantCulture));

        var subject = new FPPSPlusPaymentScheme(cf, shareRepo, balanceRepo);

        await subject.UpdateBalancesAsync(con, tx, pool, payoutHandler, block, 1000m, CancellationToken.None);

        await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-a", 250m,
            Arg.Any<string>(), Arg.Any<string[]>());
        await balanceRepo.Received(1).AddAmountAsync(con, tx, poolConfig.Id, "miner-b", 750m,
            Arg.Any<string>(), Arg.Any<string[]>());
    }
}