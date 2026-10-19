namespace AI.MacroSystem.Domain.Workflow;

using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Nodes;

public sealed class WorkflowDefinition
{
    public const string SupportedSchemaVersion = "0.7";

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("workflowId")]
    public required string WorkflowId { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("root")]
    public required WorkflowNode Root { get; init; }
}
