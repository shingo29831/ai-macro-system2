namespace AI.MacroSystem.UnitTests.Domain;

using AI.MacroSystem.Domain.Enums;
using FluentAssertions;
using Xunit;

public class EnumsTests
{
    [Fact]
    public void SideEffectTypeShouldContainExpectedDefinitions()
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
    public void RiskLevelShouldContainExpectedDefinitions()
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

    [Fact]
    public void ExecutionStatusShouldContainAllExpectedLifecycleStates()
    {
        Enum.GetValues<ExecutionStatus>()
            .Should()
            .BeEquivalentTo(new[]
            {
                ExecutionStatus.Pending,
                ExecutionStatus.Validating,
                ExecutionStatus.AwaitingApproval,
                ExecutionStatus.Ready,
                ExecutionStatus.Running,
                ExecutionStatus.Paused,
                ExecutionStatus.Succeeded,
                ExecutionStatus.Failed,
                ExecutionStatus.Cancelled,
                ExecutionStatus.Unknown,
            });
    }

    [Fact]
    public void ActionStatusShouldContainAllExpectedLifecycleStates()
    {
        Enum.GetValues<ActionStatus>()
            .Should()
            .BeEquivalentTo(new[]
            {
                ActionStatus.Pending,
                ActionStatus.Running,
                ActionStatus.Retrying,
                ActionStatus.Succeeded,
                ActionStatus.Failed,
                ActionStatus.Skipped,
                ActionStatus.Cancelled,
                ActionStatus.Unknown,
            });
    }

    [Fact]
    public void ActionErrorCategoryShouldContainExpectedDefinitions()
    {
        Enum.GetValues<ActionErrorCategory>()
            .Should()
            .BeEquivalentTo(new[]
            {
                ActionErrorCategory.None,
                ActionErrorCategory.ValidationError,
                ActionErrorCategory.Timeout,
                ActionErrorCategory.TargetNotFound,
                ActionErrorCategory.NetworkOrIO,
                ActionErrorCategory.SecurityViolation,
                ActionErrorCategory.SystemOrInternal,
                ActionErrorCategory.Unknown,
            });
    }
}
