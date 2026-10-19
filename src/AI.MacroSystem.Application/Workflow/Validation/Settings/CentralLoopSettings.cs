// @role: While/ForEach反復上限の中央設定および未指定時安全既定値の定義
namespace AI.MacroSystem.Application.Workflow.Validation.Settings;

public sealed class CentralLoopSettings
{
    public int MaxWhileIterationsLimit { get; init; } = 5000;
    public int MaxForEachIterationsLimit { get; init; } = 5000;
    public int DefaultForEachMaxIterations { get; init; } = 1000;
}
