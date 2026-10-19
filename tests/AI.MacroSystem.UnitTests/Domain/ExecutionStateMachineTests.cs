namespace AI.MacroSystem.UnitTests.Domain;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Exceptions;
using AI.MacroSystem.Domain.StateMachines;
using FluentAssertions;
using Xunit;

public class ExecutionStateMachineTests
{
    public static readonly TheoryData<ExecutionStatus, ExecutionStatus, ExecutionTrigger, string> AllowedTransitionsData = new()
    {
        { ExecutionStatus.Pending, ExecutionStatus.Validating, ExecutionTrigger.StartValidation, "ExecutionValidationStarted" },
        { ExecutionStatus.Pending, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled" },

        { ExecutionStatus.Validating, ExecutionStatus.Ready, ExecutionTrigger.ValidationPassedAutoApprove, "ExecutionValidationPassed" },
        { ExecutionStatus.Validating, ExecutionStatus.AwaitingApproval, ExecutionTrigger.ValidationPassedRequireApproval, "ExecutionApprovalRequested" },
        { ExecutionStatus.Validating, ExecutionStatus.Failed, ExecutionTrigger.ValidationFailed, "ExecutionValidationFailed" },
        { ExecutionStatus.Validating, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled" },

        { ExecutionStatus.AwaitingApproval, ExecutionStatus.Ready, ExecutionTrigger.Approve, "ExecutionApproved" },
        { ExecutionStatus.AwaitingApproval, ExecutionStatus.Failed, ExecutionTrigger.RejectApproval, "ExecutionApprovalRejected" },
        { ExecutionStatus.AwaitingApproval, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled" },

        { ExecutionStatus.Ready, ExecutionStatus.Running, ExecutionTrigger.StartExecution, "ExecutionStarted" },
        { ExecutionStatus.Ready, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled" },

        { ExecutionStatus.Running, ExecutionStatus.Paused, ExecutionTrigger.Pause, "ExecutionPaused" },
        { ExecutionStatus.Running, ExecutionStatus.Succeeded, ExecutionTrigger.CompleteSuccess, "ExecutionSucceeded" },
        { ExecutionStatus.Running, ExecutionStatus.Failed, ExecutionTrigger.FailExecution, "ExecutionFailed" },
        { ExecutionStatus.Running, ExecutionStatus.FaultUnknown, ExecutionTrigger.FaultUnknown, "ExecutionFaultedUnknown" },
        { ExecutionStatus.Running, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled" },

        { ExecutionStatus.Paused, ExecutionStatus.Running, ExecutionTrigger.Resume, "ExecutionResumed" },
        { ExecutionStatus.Paused, ExecutionStatus.Cancelled, ExecutionTrigger.Cancel, "ExecutionCancelled" },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitionsData))]
    public void AllowedTransitions_ShouldSucceed_AndEmitExpectedAuditEvent(
        ExecutionStatus from,
        ExecutionStatus to,
        ExecutionTrigger trigger,
        string expectedEvent)
    {
        ExecutionStateMachine.CanTransition(from, to).Should().BeTrue();
        ExecutionStateMachine.CanTransition(from, trigger).Should().BeTrue();

        var resultFromTrigger = ExecutionStateMachine.Transition(from, trigger);
        resultFromTrigger.CurrentStatus.Should().Be(from);
        resultFromTrigger.NewStatus.Should().Be(to);
        resultFromTrigger.AuditEventType.Should().Be(expectedEvent);

        var resultFromTarget = ExecutionStateMachine.Transition(from, to);
        resultFromTarget.CurrentStatus.Should().Be(from);
        resultFromTarget.NewStatus.Should().Be(to);
        resultFromTarget.AuditEventType.Should().Be(expectedEvent);
    }

    [Fact]
    public void DisallowedTransitions_InFullMatrix_MustBeRejected()
    {
        var allStatuses = Enum.GetValues<ExecutionStatus>();
        var allowedPairs = ExecutionStateMachine.Rules.Select(r => (r.From, r.To)).ToHashSet();

        foreach (var from in allStatuses)
        {
            foreach (var to in allStatuses)
            {
                if (allowedPairs.Contains((from, to)))
                {
                    continue;
                }

                ExecutionStateMachine.CanTransition(from, to).Should().BeFalse($"Transition from {from} to {to} must be disallowed");

                var act = () => ExecutionStateMachine.Transition(from, to);
                act.Should().Throw<InvalidStateTransitionException>()
                    .Where(ex => ex.EntityType == "Execution" && ex.CurrentState == from.ToString());
            }
        }
    }

    [Fact]
    public void TerminalStates_MustNotAllowAnyTransitions()
    {
        var terminalStates = new[]
        {
            ExecutionStatus.Succeeded,
            ExecutionStatus.Failed,
            ExecutionStatus.Cancelled,
            ExecutionStatus.Unknown,
        };

        var allTriggers = Enum.GetValues<ExecutionTrigger>();

        foreach (var terminal in terminalStates)
        {
            foreach (var trigger in allTriggers)
            {
                ExecutionStateMachine.CanTransition(terminal, trigger).Should().BeFalse();

                var act = () => ExecutionStateMachine.Transition(terminal, trigger);
                act.Should().Throw<InvalidStateTransitionException>();
            }
        }
    }
}
