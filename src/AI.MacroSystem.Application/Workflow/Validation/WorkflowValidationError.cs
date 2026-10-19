// @role: 機械可読コード、NodeId、対象パス、詳細説明を保持する検証エラーレコード
namespace AI.MacroSystem.Application.Workflow.Validation;

public sealed record WorkflowValidationError(
    string ErrorCode,
    string? NodeId,
    string Path,
    string Message);
