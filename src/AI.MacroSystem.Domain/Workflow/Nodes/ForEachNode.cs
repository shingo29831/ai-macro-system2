namespace AI.MacroSystem.Domain.Workflow.Nodes;

using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Expressions;

public sealed class ForEachNode : WorkflowNode
{
    [JsonPropertyName("itemVariable")]
    public required string ItemVariable { get; init; }

    [JsonPropertyName("source")]
    public required Expression Source { get; init; }

    [JsonPropertyName("body")]
    public required WorkflowNode Body { get; init; }

    [JsonPropertyName("maxIterations")]
    public int? MaxIterations { get; init; }
}
