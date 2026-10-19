// @role: ワークフローIR検証の結果および成功時の不変Validated IRを保持するモデル
namespace AI.MacroSystem.Application.Workflow.Validation;

using AI.MacroSystem.Domain.Workflow;

public sealed class ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<WorkflowValidationError> DetailedErrors { get; }
    public ValidatedWorkflowDefinition? ValidatedWorkflow { get; }

    private ValidationResult(
        bool isValid,
        IReadOnlyList<string> errors,
        IReadOnlyList<WorkflowValidationError> detailedErrors,
        ValidatedWorkflowDefinition? validatedWorkflow)
    {
        IsValid = isValid;
        Errors = errors;
        DetailedErrors = detailedErrors;
        ValidatedWorkflow = validatedWorkflow;
    }

    public static ValidationResult Success(ValidatedWorkflowDefinition? validatedWorkflow = null)
        => new(true, [], [], validatedWorkflow);

    public static ValidationResult Failure(IReadOnlyList<string> errors)
    {
        var detailed = errors.Select(e => new WorkflowValidationError(
            ValidationErrorCode.SchemaViolation,
            null,
            "$",
            e)).ToList();

        return new(false, errors, detailed, null);
    }

    public static ValidationResult Failure(IReadOnlyList<WorkflowValidationError> detailedErrors)
    {
        var errors = detailedErrors
            .Select(d => $"[{d.ErrorCode}] {(d.NodeId != null ? $"Node '{d.NodeId}': " : "")}{d.Message} (Path: {d.Path})")
            .ToList();

        return new(false, errors, detailedErrors, null);
    }
}
