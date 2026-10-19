namespace AI.MacroSystem.Domain.Workflow.Actions;

public interface IActionTypeRegistry
{
    void Register<TParameters>(string actionType) where TParameters : ActionParameters;
    Type? GetParametersType(string actionType);
    bool IsRegistered(string actionType);
    IReadOnlyCollection<string> RegisteredTypes { get; }
}
