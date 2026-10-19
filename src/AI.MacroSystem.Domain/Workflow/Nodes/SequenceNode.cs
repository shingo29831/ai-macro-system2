namespace AI.MacroSystem.Domain.Workflow.Nodes;

using System.Text.Json.Serialization;

public sealed class SequenceNode : WorkflowNode
{
    [JsonPropertyName("nodes")]
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; } = [];
}
