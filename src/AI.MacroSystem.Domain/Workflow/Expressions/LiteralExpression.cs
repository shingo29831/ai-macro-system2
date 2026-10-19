namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

public sealed class LiteralExpression : Expression
{
    [JsonPropertyName("value")]
    public required object? Value { get; init; }
}
