namespace AI.MacroSystem.Domain.Workflow.Nodes;

using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Expressions;

public sealed class IfNode : WorkflowNode
{
    [JsonPropertyName("condition")]
    public required ConditionExpression Condition { get; init; }

    [JsonPropertyName("thenBranch")]
    public required WorkflowNode ThenBranch { get; init; }

    [JsonPropertyName("elseBranch")]
    public WorkflowNode? ElseBranch { get; init; }
}
