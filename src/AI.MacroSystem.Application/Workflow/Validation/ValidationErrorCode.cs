// @role: ワークフローIR検証エラーの機械可読な標準エラーコード定義
namespace AI.MacroSystem.Application.Workflow.Validation;

public static class ValidationErrorCode
{
    public const string JsonSyntaxError = "SYNTAX_ERROR";
    public const string SchemaViolation = "SCHEMA_VIOLATION";
    public const string UnsupportedSchemaVersion = "UNSUPPORTED_SCHEMA_VERSION";
    public const string DuplicateNodeId = "DUPLICATE_NODE_ID";
    public const string UnknownNodeType = "UNKNOWN_NODE_TYPE";
    public const string UnknownActionType = "UNKNOWN_ACTION_TYPE";
    public const string InvalidActionParameter = "INVALID_ACTION_PARAMETER";
    public const string MissingRequiredParameter = "MISSING_REQUIRED_PARAMETER";
    public const string ParameterDependencyFailed = "PARAMETER_DEPENDENCY_FAILED";
    public const string UndefinedVariable = "UNDEFINED_VARIABLE";
    public const string VariableScopeViolation = "VARIABLE_SCOPE_VIOLATION";
    public const string BranchVariableUncertain = "BRANCH_VARIABLE_UNCERTAIN";
    public const string LoopLimitExceeded = "LOOP_LIMIT_EXCEEDED";
    public const string LoopLimitInvalid = "LOOP_LIMIT_INVALID";
    public const string InvalidExpression = "INVALID_EXPRESSION";
}
