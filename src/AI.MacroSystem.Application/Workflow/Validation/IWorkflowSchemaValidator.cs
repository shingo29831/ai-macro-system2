namespace AI.MacroSystem.Application.Workflow.Validation;

public interface IWorkflowSchemaValidator
{
    ValidationResult Validate(string json);
}
