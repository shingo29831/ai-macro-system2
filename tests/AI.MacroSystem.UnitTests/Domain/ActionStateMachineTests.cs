namespace AI.MacroSystem.UnitTests.Domain;

using AI.MacroSystem.Domain.Enums;
using AI.MacroSystem.Domain.Exceptions;
using AI.MacroSystem.Domain.StateMachines;
using FluentAssertions;
using Xunit;

public class ActionStateMachineTests
{
    public static readonly TheoryData<ActionStatus, ActionStatus, ActionTrigger, string> AllowedTransitionsData = new()
    {
        { ActionStatus.Pending, ActionStatus.Running, ActionTrigger.Start, "ActionStarted" },
        { ActionStatus.Pending, ActionStatus.Skipped, ActionTrigger.Skip, "ActionSkipped" },
        { ActionStatus.Pending, ActionStatus.Cancelled, ActionTrigger.Cancel, "ActionCancelled" },

        { ActionStatus.Running, ActionStatus.Succeeded, ActionTrigger.CompleteSuccess, "ActionSucceeded" },
        { ActionStatus.Running, ActionStatus.Retrying, ActionTrigger.ScheduleRetry, "ActionRetryScheduled" },
        { ActionStatus.Running, ActionStatus.Failed, ActionTrigger.Fail, "ActionFailed" },
        { ActionStatus.Running, ActionStatus.Unknown, ActionTrigger.FaultUnknown, "ActionFaultedUnknown" },
        { ActionStatus.Running, ActionStatus.Cancelled, ActionTrigger.Cancel, "ActionCancelled" },

        { ActionStatus.Retrying, ActionStatus.Running, ActionTrigger.StartRetry, "ActionRetrying" },
        { ActionStatus.Retrying, ActionStatus.Failed, ActionTrigger.Fail, "ActionFailed" },
        { ActionStatus.Retrying, ActionStatus.Cancelled, ActionTrigger.Cancel, "ActionCancelled" },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitionsData))]
    public void AllowedTransitions_ShouldSucceed_AndEmitExpectedAuditEvent(
        ActionStatus from,
        ActionStatus to,
        ActionTrigger trigger,
        string expectedEvent)
    {
        ActionStateMachine.CanTransition(from, to).Should().BeTrue();
        ActionStateMachine.CanTransition(from, trigger).Should().BeTrue();

        var resultFromTrigger = ActionStateMachine.Transition(from, trigger);
        resultFromTrigger.CurrentStatus.Should().Be(from);
        resultFromTrigger.NewStatus.Should().Be(to);
        resultFromTrigger.AuditEventType.Should().Be(expectedEvent);

        var resultFromTarget = ActionStateMachine.Transition(from, to);
        resultFromTarget.CurrentStatus.Should().Be(from);
        resultFromTarget.NewStatus.Should().Be(to);
        resultFromTarget.AuditEventType.Should().Be(expectedEvent);
    }

    [Fact]
    public void DisallowedTransitions_InFullMatrix_MustBeRejected()
    {
        var allStatuses = Enum.GetValues<ActionStatus>();
        var allowedPairs = ActionStateMachine.Rules.Select(r => (r.From, r.To)).ToHashSet();

        foreach (var from in allStatuses)
        {
            foreach (var to in allStatuses)
            {
                if (allowedPairs.Contains((from, to)))
                {
                    continue;
                }

                ActionStateMachine.CanTransition(from, to).Should().BeFalse($"Transition from {from} to {to} must be disallowed");

                var act = () => ActionStateMachine.Transition(from, to);
                act.Should().Throw<InvalidStateTransitionException>()
                    .Where(ex => ex.EntityType == "ActionExecution" && ex.CurrentState == from.ToString());
            }
        }
    }

    [Fact]
    public void TerminalStates_MustNotAllowAnyTransitions()
    {
        var terminalStates = new[]
        {
            ActionStatus.Succeeded,
            ActionStatus.Failed,
            ActionStatus.Skipped,
            ActionStatus.Cancelled,
            ActionStatus.Unknown,
        };

        var allTriggers = Enum.GetValues<ActionTrigger>();

        foreach (var terminal in terminalStates)
        {
            foreach (var trigger in allTriggers)
            {
                ActionStateMachine.CanTransition(terminal, trigger).Should().BeFalse();

                var act = () => ActionStateMachine.Transition(terminal, trigger);
                act.Should().Throw<InvalidStateTransitionException>();
            }
        }
    }
}
