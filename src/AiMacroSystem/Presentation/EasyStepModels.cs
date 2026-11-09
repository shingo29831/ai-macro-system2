// Module: @role: マクロ手順ブロック・安全検証結果・実行前レビュー項目のUI表示用データモデル
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed class EasyStepItem
{
    public required int StepNumber { get; set; }
    public required string NodeId { get; init; }
    public required string ActionType { get; init; }
    public required string FriendlyTitle { get; init; }
    public required string FriendlyDetail { get; init; }
    public required WorkflowNode UnderlyingNode { get; init; }
    public string BlockIcon { get; init; } = "⚙️";
    public string BlockCategory { get; init; } = "操作ブロック";
    public string AccentColorHex { get; init; } = "#2563EB";
    public string TargetAppBadge { get; init; } = "アプリ";
    public string TargetLocationSummary { get; init; } = string.Empty;
    public string OperationValueSummary { get; init; } = string.Empty;
    public string SourceLogSummary { get; init; } = "複数ログ統合抽出";
    public bool IsSelectedForEdit { get; set; }
    public bool HasNextBlock { get; set; }
}

public sealed class FriendlyValidationItem
{
    public required string Severity { get; init; }
    public required string FriendlyLocation { get; init; }
    public required string FriendlyReason { get; init; }
    public required string HowToFix { get; init; }
    public required string TechnicalCode { get; init; }
}

public sealed class FriendlyPlanReviewItem
{
    public required string NodeId { get; init; }
    public required string OperationName { get; init; }
    public required string TargetDescription { get; init; }
    public required string SideEffectDescription { get; init; }
    public required string RiskBadge { get; init; }
    public required bool NeedsExtraApproval { get; init; }
}
