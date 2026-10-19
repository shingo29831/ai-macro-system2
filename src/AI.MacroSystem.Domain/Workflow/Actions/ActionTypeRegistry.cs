namespace AI.MacroSystem.Domain.Workflow.Actions;

public sealed class ActionTypeRegistry : IActionTypeRegistry
{
    private readonly Dictionary<string, Type> _registeredTypes = new(StringComparer.OrdinalIgnoreCase);

    public void Register<TParameters>(string actionType) where TParameters : ActionParameters
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        _registeredTypes[actionType] = typeof(TParameters);
    }

    public Type? GetParametersType(string actionType)
    {
        return _registeredTypes.TryGetValue(actionType, out var type) ? type : null;
    }

    public bool IsRegistered(string actionType) => _registeredTypes.ContainsKey(actionType);

    public IReadOnlyCollection<string> RegisteredTypes => _registeredTypes.Keys;

    public static ActionTypeRegistry CreateDefault()
    {
        var registry = new ActionTypeRegistry();
        registry.Register<DelayParameters>("Delay");
        registry.Register<LogParameters>("Log");
        registry.Register<WebNavigateParameters>("WebNavigate");
        registry.Register<ExcelReadCellParameters>("ExcelReadCell");
        registry.Register<ExcelWriteCellParameters>("ExcelWriteCell");
        return registry;
    }
}
