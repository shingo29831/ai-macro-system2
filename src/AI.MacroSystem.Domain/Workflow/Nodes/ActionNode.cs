namespace AI.MacroSystem.Domain.Workflow.Nodes;

using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Actions;

public sealed class ActionNode : WorkflowNode
{
    [JsonPropertyName("actionType")]
    public required string ActionType { get; init; }

    [JsonPropertyName("parameters")]
    public required ActionParameters Parameters { get; init; }
}
