namespace AI.MacroSystem.Application.Workflow.Validation;

public sealed class ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }

    private ValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    public static ValidationResult Success() => new(true, []);

    public static ValidationResult Failure(IReadOnlyList<string> errors) => new(false, errors);
}
