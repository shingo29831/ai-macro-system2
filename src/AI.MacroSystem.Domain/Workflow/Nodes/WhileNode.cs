namespace AI.MacroSystem.Domain.Workflow.Nodes;

using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Expressions;

public sealed class WhileNode : WorkflowNode
{
    [JsonPropertyName("condition")]
    public required ConditionExpression Condition { get; init; }

    [JsonPropertyName("body")]
    public required WorkflowNode Body { get; init; }

    [JsonPropertyName("maxIterations")]
    public required int MaxIterations { get; init; }
}
