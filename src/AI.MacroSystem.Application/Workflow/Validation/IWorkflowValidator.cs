// @role: 構文・型・スキーマ・参照・制御フロー・ループ上限を網羅的に検証するValidator契約
namespace AI.MacroSystem.Application.Workflow.Validation;

using AI.MacroSystem.Domain.Workflow;

public interface IWorkflowValidator
{
    ValidationResult Validate(string json);
    ValidationResult Validate(WorkflowDefinition definition);
}
