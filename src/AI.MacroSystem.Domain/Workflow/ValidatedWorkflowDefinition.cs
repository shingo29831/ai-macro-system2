// @role: 検証を通過したことの証明となる不変なワークフロー定義ラッパー型（外部任意構築不可）
namespace AI.MacroSystem.Domain.Workflow;

public sealed class ValidatedWorkflowDefinition
{
    public WorkflowDefinition Definition { get; }
    public DateTime ValidatedAtUtc { get; }

    internal ValidatedWorkflowDefinition(WorkflowDefinition definition, DateTime validatedAtUtc)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ValidatedAtUtc = validatedAtUtc;
    }
}
