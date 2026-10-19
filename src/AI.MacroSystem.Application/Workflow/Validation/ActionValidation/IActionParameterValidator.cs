// @role: アクションパラメータ専用の型・値域・相互依存性検証インターフェース
namespace AI.MacroSystem.Application.Workflow.Validation.ActionValidation;

using AI.MacroSystem.Domain.Workflow.Actions;

public interface IActionParameterValidator
{
    bool CanValidate(string actionType);
    IEnumerable<WorkflowValidationError> Validate(string actionType, ActionParameters parameters, string nodeId, string path);
}
