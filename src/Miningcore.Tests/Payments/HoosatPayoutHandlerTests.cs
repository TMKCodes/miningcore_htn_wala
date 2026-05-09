using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Hoosat;
using Miningcore.Messaging;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class HoosatPayoutHandlerTests
{
  [Fact]
  public void AdjustShareDifficulty_ScalesPersistedDifficultyToHoosatDifficultyUnit()
  {
    var subject = CreateSubject();
    const double difficulty = 0.1;

    var result = subject.AdjustShareDifficulty(difficulty);

    Assert.Equal(429496729.6, result, 1);
  }

  [Fact]
  public void AdjustBlockEffort_ScalesPersistedEffortToHoosatDifficultyUnit()
  {
    var subject = CreateSubject();
    const double effort = 0.1;

    var result = subject.AdjustBlockEffort(effort);

    Assert.Equal(429496729.6, result, 1);
  }

  private static HoosatPayoutHandler CreateSubject()
  {
    return new HoosatPayoutHandler(
        Substitute.For<IComponentContext>(),
        Substitute.For<IConnectionFactory>(),
        Substitute.For<IMapper>(),
        Substitute.For<IShareRepository>(),
        Substitute.For<IBlockRepository>(),
        Substitute.For<IBalanceRepository>(),
        Substitute.For<IPaymentRepository>(),
        Substitute.For<IMasterClock>(),
        Substitute.For<IMessageBus>());
  }
}