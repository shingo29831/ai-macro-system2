namespace AI.MacroSystem.Domain.Workflow.Expressions;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LiteralExpression), "Literal")]
[JsonDerivedType(typeof(VariableExpression), "Variable")]
public abstract class Expression
{
}
