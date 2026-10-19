namespace AI.MacroSystem.Domain.Enums;

/// <summary>
/// ワークフローおよび操作のリスク分類を定義します。Prohibitedは承認でも許可されません。
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Prohibited,
}
