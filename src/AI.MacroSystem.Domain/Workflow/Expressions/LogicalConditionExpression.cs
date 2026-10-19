namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

public enum LogicalOperator
{
    And,
    Or,
}

public sealed class LogicalConditionExpression : ConditionExpression
{
    [JsonPropertyName("operator")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required LogicalOperator Operator { get; init; }

    [JsonPropertyName("conditions")]
    public required IReadOnlyList<ConditionExpression> Conditions { get; init; } = [];
}
