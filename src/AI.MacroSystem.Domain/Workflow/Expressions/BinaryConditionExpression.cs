namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

public enum BinaryOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    StartsWith,
    EndsWith,
}

public sealed class BinaryConditionExpression : ConditionExpression
{
    [JsonPropertyName("operator")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required BinaryOperator Operator { get; init; }

    [JsonPropertyName("left")]
    public required Expression Left { get; init; }

    [JsonPropertyName("right")]
    public required Expression Right { get; init; }
}
