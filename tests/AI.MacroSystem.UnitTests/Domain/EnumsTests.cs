namespace AI.MacroSystem.UnitTests.Domain;

using AI.MacroSystem.Domain.Enums;
using FluentAssertions;
using Xunit;

public class EnumsTests
{
    [Fact]
    public void SideEffectType_ShouldContainExpectedDefinitions()
    {
        Enum.GetValues<SideEffectType>()
            .Should()
            .BeEquivalentTo(new[]
            {
                SideEffectType.Read,
                SideEffectType.LocalMutation,
                SideEffectType.ExternalSend,
                SideEffectType.Destructive,
            });
    }

    [Fact]
    public void RiskLevel_ShouldContainExpectedDefinitions()
    {
        Enum.GetValues<RiskLevel>()
            .Should()
            .BeEquivalentTo(new[]
            {
                RiskLevel.Low,
                RiskLevel.Medium,
                RiskLevel.High,
                RiskLevel.Prohibited,
            });
    }
}
