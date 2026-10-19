namespace AI.MacroSystem.Domain.Workflow.Nodes;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SequenceNode), "Sequence")]
[JsonDerivedType(typeof(IfNode), "If")]
[JsonDerivedType(typeof(ForEachNode), "ForEach")]
[JsonDerivedType(typeof(WhileNode), "While")]
[JsonDerivedType(typeof(ActionNode), "Action")]
public abstract class WorkflowNode
{
    [JsonPropertyName("nodeId")]
    public required string NodeId { get; init; }
}
