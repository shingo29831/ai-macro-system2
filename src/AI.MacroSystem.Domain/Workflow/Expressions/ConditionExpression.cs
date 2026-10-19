namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BinaryConditionExpression), "Binary")]
[JsonDerivedType(typeof(UnaryConditionExpression), "Unary")]
[JsonDerivedType(typeof(LogicalConditionExpression), "Logical")]
public abstract class ConditionExpression
{
}
