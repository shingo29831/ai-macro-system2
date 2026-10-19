namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

public sealed class VariableExpression : Expression
{
    [JsonPropertyName("variableName")]
    public required string VariableName { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}
