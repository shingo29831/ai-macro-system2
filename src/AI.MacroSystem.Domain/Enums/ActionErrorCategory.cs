// @role: アクション実行におけるエラー分類を定義する列挙型
namespace AI.MacroSystem.Domain.Enums;

public enum ActionErrorCategory
{
    None,
    ValidationError,
    Timeout,
    TargetNotFound,
    NetworkOrIO,
    SecurityViolation,
    SystemOrInternal,
    Unknown,
}
