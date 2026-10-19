namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

public enum UnaryOperator
{
    Not,
    IsNull,
    IsNotNull,
    IsEmpty,
    IsNotEmpty,
}

public sealed class UnaryConditionExpression : ConditionExpression
{
    [JsonPropertyName("operator")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required UnaryOperator Operator { get; init; }

    [JsonPropertyName("operand")]
    public required Expression Operand { get; init; }
}
