// @role: IR構文・型・スキーマ・参照到達可能性・制御フロー・ループ上限の総合検証器
namespace AI.MacroSystem.Application.Workflow.Validation;

using System.Text.RegularExpressions;
using AI.MacroSystem.Application.Workflow.Serialization;
using AI.MacroSystem.Application.Workflow.Validation.ActionValidation;
using AI.MacroSystem.Application.Workflow.Validation.Settings;
using AI.MacroSystem.Domain.Workflow;
using AI.MacroSystem.Domain.Workflow.Actions;
using AI.MacroSystem.Domain.Workflow.Expressions;
using AI.MacroSystem.Domain.Workflow.Nodes;

public sealed class WorkflowValidator : IWorkflowValidator
{
    private static readonly Regex IdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private readonly IWorkflowSchemaValidator _schemaValidator;
    private readonly WorkflowIrJsonSerializer _serializer;
    private readonly IActionTypeRegistry _actionRegistry;
    private readonly IActionParameterValidator _parameterValidator;
    private readonly CentralLoopSettings _loopSettings;

    public WorkflowValidator(
        IWorkflowSchemaValidator? schemaValidator = null,
        WorkflowIrJsonSerializer? serializer = null,
        IActionTypeRegistry? actionRegistry = null,
        IActionParameterValidator? parameterValidator = null,
        CentralLoopSettings? loopSettings = null)
    {
        _actionRegistry = actionRegistry ?? ActionTypeRegistry.CreateDefault();
        _schemaValidator = schemaValidator ?? new WorkflowSchemaValidator();
        _serializer = serializer ?? new WorkflowIrJsonSerializer(_actionRegistry);
        _parameterValidator = parameterValidator ?? new DefaultActionParameterValidator();
        _loopSettings = loopSettings ?? new CentralLoopSettings();
    }

    public ValidationResult Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ValidationResult.Failure([
                new WorkflowValidationError(ValidationErrorCode.JsonSyntaxError, null, "$", "Workflow JSON cannot be null or empty.")
            ]);
        }

        var schemaResult = _schemaValidator.Validate(json);
        if (!schemaResult.IsValid)
        {
            return schemaResult;
        }

        WorkflowDefinition definition;
        try
        {
            definition = _serializer.Deserialize(json);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure([
                new WorkflowValidationError(ValidationErrorCode.JsonSyntaxError, null, "$", $"Deserialization failed: {ex.Message}")
            ]);
        }

        return Validate(definition);
    }

    public ValidationResult Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<WorkflowValidationError>();

        if (definition.SchemaVersion != WorkflowDefinition.SupportedSchemaVersion)
        {
            errors.Add(new WorkflowValidationError(
                ValidationErrorCode.UnsupportedSchemaVersion,
                null,
                "$.schemaVersion",
                $"Unsupported schemaVersion '{definition.SchemaVersion}'. Expected '{WorkflowDefinition.SupportedSchemaVersion}'."));
        }

        var context = new ValidationContext(_actionRegistry, _parameterValidator, _loopSettings);
        var initialScope = new DefiniteScope();

        AnalyzeNode(definition.Root, context, initialScope, "$.root");

        errors.AddRange(context.Errors);

        if (errors.Count > 0)
        {
            return ValidationResult.Failure(errors);
        }

        var validated = new ValidatedWorkflowDefinition(definition, DateTime.UtcNow);
        return ValidationResult.Success(validated);
    }

    private void AnalyzeNode(WorkflowNode node, ValidationContext context, DefiniteScope scope, string path)
    {
        if (!context.VisitedNodeIds.Add(node.NodeId))
        {
            context.Errors.Add(new WorkflowValidationError(
                ValidationErrorCode.DuplicateNodeId,
                node.NodeId,
                $"{path}.nodeId",
                $"Duplicate nodeId '{node.NodeId}' detected. Node identifiers must be globally unique."));
        }

        switch (node)
        {
            case SequenceNode seq:
                for (int i = 0; i < seq.Nodes.Count; i++)
                {
                    AnalyzeNode(seq.Nodes[i], context, scope, $"{path}.nodes[{i}]");
                }
                break;

            case ActionNode act:
                AnalyzeActionNode(act, context, scope, path);
                break;

            case IfNode ifNode:
                AnalyzeIfNode(ifNode, context, scope, path);
                break;

            case WhileNode whileNode:
                AnalyzeWhileNode(whileNode, context, scope, path);
                break;

            case ForEachNode forEachNode:
                AnalyzeForEachNode(forEachNode, context, scope, path);
                break;

            default:
                context.Errors.Add(new WorkflowValidationError(
                    ValidationErrorCode.UnknownNodeType,
                    node.NodeId,
                    path,
                    $"Unknown node kind '{node.GetType().Name}'."));
                break;
        }
    }

    private void AnalyzeActionNode(ActionNode act, ValidationContext context, DefiniteScope scope, string path)
    {
        if (!_actionRegistry.IsRegistered(act.ActionType))
        {
            context.Errors.Add(new WorkflowValidationError(
                ValidationErrorCode.UnknownActionType,
                act.NodeId,
                $"{path}.actionType",
                $"Unknown or unregistered action type '{act.ActionType}'."));
            return;
        }

        if (_parameterValidator.CanValidate(act.ActionType))
        {
            var paramErrors = _parameterValidator.Validate(act.ActionType, act.Parameters, act.NodeId, path);
            context.Errors.AddRange(paramErrors);
        }

        // アクション引数内の式で参照されている変数の検証
        if (act.Parameters is ExcelWriteCellParameters writeParams)
        {
            ValidateExpressionVariables(writeParams.Value, scope, context, act.NodeId, $"{path}.parameters.value");
        }

        // アクションによる新規変数の確定定義
        if (act.Parameters is ExcelReadCellParameters readParams && !string.IsNullOrWhiteSpace(readParams.OutputVariable))
        {
            scope.DefineVariable(readParams.OutputVariable);
        }
    }

    private void AnalyzeIfNode(IfNode ifNode, ValidationContext context, DefiniteScope scope, string path)
    {
        ValidateConditionVariables(ifNode.Condition, scope, context, ifNode.NodeId, $"{path}.condition");

        var thenScope = scope.CreateChild();
        AnalyzeNode(ifNode.ThenBranch, context, thenScope, $"{path}.thenBranch");
        var thenDefined = thenScope.GetLocallyDefinedVariables();

        if (ifNode.ElseBranch != null)
        {
            var elseScope = scope.CreateChild();
            AnalyzeNode(ifNode.ElseBranch, context, elseScope, $"{path}.elseBranch");
            var elseDefined = elseScope.GetLocallyDefinedVariables();

            // 両方の分岐で確実に定義された変数は外側でも確定
            var common = thenDefined.Intersect(elseDefined).ToList();
            foreach (var v in common)
            {
                scope.DefineVariable(v);
            }

            // 片方の分岐でしか定義されていない変数は不確定としてマーク（直接参照は拒否対象）
            var uncertain = thenDefined.Except(common).Concat(elseDefined.Except(common)).ToList();
            foreach (var u in uncertain)
            {
                scope.MarkUncertainVariable(u);
            }
        }
        else
        {
            // Else分岐がない場合、Thenの定義は後続にとって常に不確実
            foreach (var v in thenDefined)
            {
                scope.MarkUncertainVariable(v);
            }
        }
    }

    private void AnalyzeWhileNode(WhileNode whileNode, ValidationContext context, DefiniteScope scope, string path)
    {
        ValidateConditionVariables(whileNode.Condition, scope, context, whileNode.NodeId, $"{path}.condition");

        if (whileNode.MaxIterations <= 0)
        {
            context.Errors.Add(new WorkflowValidationError(
                ValidationErrorCode.LoopLimitInvalid,
                whileNode.NodeId,
                $"{path}.maxIterations",
                $"While maxIterations must be greater than 0, but was {whileNode.MaxIterations}."));
        }
        else if (whileNode.MaxIterations > context.LoopSettings.MaxWhileIterationsLimit)
        {
            context.Errors.Add(new WorkflowValidationError(
                ValidationErrorCode.LoopLimitExceeded,
                whileNode.NodeId,
                $"{path}.maxIterations",
                $"While maxIterations exceeds system limit {context.LoopSettings.MaxWhileIterationsLimit} (was {whileNode.MaxIterations})."));
        }

        var bodyScope = scope.CreateChild();
        AnalyzeNode(whileNode.Body, context, bodyScope, $"{path}.body");

        // ループ内定義変数は0回実行の可能性があるため外部では不確実
        foreach (var v in bodyScope.GetLocallyDefinedVariables())
        {
            scope.MarkUncertainVariable(v);
        }
    }

    private void AnalyzeForEachNode(ForEachNode forEachNode, ValidationContext context, DefiniteScope scope, string path)
    {
        ValidateExpressionVariables(forEachNode.Source, scope, context, forEachNode.NodeId, $"{path}.source");

        if (string.IsNullOrWhiteSpace(forEachNode.ItemVariable) || !IdentifierRegex.IsMatch(forEachNode.ItemVariable))
        {
            context.Errors.Add(new WorkflowValidationError(
                ValidationErrorCode.InvalidActionParameter,
                forEachNode.NodeId,
                $"{path}.itemVariable",
                $"ForEach itemVariable '{forEachNode.ItemVariable}' must be a valid identifier."));
        }

        if (forEachNode.MaxIterations.HasValue)
        {
            if (forEachNode.MaxIterations.Value <= 0)
            {
                context.Errors.Add(new WorkflowValidationError(
                    ValidationErrorCode.LoopLimitInvalid,
                    forEachNode.NodeId,
                    $"{path}.maxIterations",
                    $"ForEach maxIterations must be greater than 0, but was {forEachNode.MaxIterations.Value}."));
            }
            else if (forEachNode.MaxIterations.Value > context.LoopSettings.MaxForEachIterationsLimit)
            {
                context.Errors.Add(new WorkflowValidationError(
                    ValidationErrorCode.LoopLimitExceeded,
                    forEachNode.NodeId,
                    $"{path}.maxIterations",
                    $"ForEach maxIterations exceeds system limit {context.LoopSettings.MaxForEachIterationsLimit} (was {forEachNode.MaxIterations.Value})."));
            }
        }

        var bodyScope = scope.CreateChild();
        bodyScope.DefineVariable(forEachNode.ItemVariable);

        AnalyzeNode(forEachNode.Body, context, bodyScope, $"{path}.body");

        // ItemVariableはループ外では参照不可
        foreach (var v in bodyScope.GetLocallyDefinedVariables())
        {
            if (v != forEachNode.ItemVariable)
            {
                scope.MarkUncertainVariable(v);
            }
        }
    }

    private void ValidateExpressionVariables(Expression expr, DefiniteScope scope, ValidationContext context, string? nodeId, string path)
    {
        switch (expr)
        {
            case VariableExpression varExpr:
                if (scope.IsUncertain(varExpr.VariableName))
                {
                    context.Errors.Add(new WorkflowValidationError(
                        ValidationErrorCode.BranchVariableUncertain,
                        nodeId,
                        path,
                        $"Variable '{varExpr.VariableName}' is only conditionally defined in certain branches/loops and cannot be safely referenced without an explicit Optional fallback."));
                }
                else if (!scope.IsDefinite(varExpr.VariableName))
                {
                    context.Errors.Add(new WorkflowValidationError(
                        ValidationErrorCode.UndefinedVariable,
                        nodeId,
                        path,
                        $"Referenced variable '{varExpr.VariableName}' has not been defined in the accessible scope."));
                }
                break;

            case LiteralExpression:
                break;

            default:
                context.Errors.Add(new WorkflowValidationError(
                    ValidationErrorCode.InvalidExpression,
                    nodeId,
                    path,
                    $"Unsupported expression type '{expr.GetType().Name}'."));
                break;
        }
    }

    private void ValidateConditionVariables(ConditionExpression cond, DefiniteScope scope, ValidationContext context, string? nodeId, string path)
    {
        switch (cond)
        {
            case BinaryConditionExpression binary:
                ValidateExpressionVariables(binary.Left, scope, context, nodeId, $"{path}.left");
                ValidateExpressionVariables(binary.Right, scope, context, nodeId, $"{path}.right");
                break;

            case UnaryConditionExpression unary:
                ValidateExpressionVariables(unary.Operand, scope, context, nodeId, $"{path}.operand");
                break;

            case LogicalConditionExpression logical:
                for (int i = 0; i < logical.Conditions.Count; i++)
                {
                    ValidateConditionVariables(logical.Conditions[i], scope, context, nodeId, $"{path}.conditions[{i}]");
                }
                break;

            default:
                context.Errors.Add(new WorkflowValidationError(
                    ValidationErrorCode.InvalidExpression,
                    nodeId,
                    path,
                    $"Unsupported condition expression type '{cond.GetType().Name}'."));
                break;
        }
    }

    private sealed class ValidationContext
    {
        public HashSet<string> VisitedNodeIds { get; } = new(StringComparer.Ordinal);
        public List<WorkflowValidationError> Errors { get; } = [];
        public IActionTypeRegistry ActionRegistry { get; }
        public IActionParameterValidator ParameterValidator { get; }
        public CentralLoopSettings LoopSettings { get; }

        public ValidationContext(
            IActionTypeRegistry actionRegistry,
            IActionParameterValidator parameterValidator,
            CentralLoopSettings loopSettings)
        {
            ActionRegistry = actionRegistry;
            ParameterValidator = parameterValidator;
            LoopSettings = loopSettings;
        }
    }

    private sealed class DefiniteScope
    {
        private readonly DefiniteScope? _parent;
        private readonly HashSet<string> _definite = new(StringComparer.Ordinal);
        private readonly HashSet<string> _uncertain = new(StringComparer.Ordinal);

        public DefiniteScope(DefiniteScope? parent = null)
        {
            _parent = parent;
        }

        public DefiniteScope CreateChild() => new(this);

        public void DefineVariable(string name)
        {
            _definite.Add(name);
            _uncertain.Remove(name);
        }

        public void MarkUncertainVariable(string name)
        {
            if (!_definite.Contains(name))
            {
                _uncertain.Add(name);
            }
        }

        public bool IsDefinite(string name)
        {
            if (_definite.Contains(name)) return true;
            return _parent?.IsDefinite(name) ?? false;
        }

        public bool IsUncertain(string name)
        {
            if (IsDefinite(name)) return false;
            if (_uncertain.Contains(name)) return true;
            return _parent?.IsUncertain(name) ?? false;
        }

        public IReadOnlySet<string> GetLocallyDefinedVariables() => _definite;
    }
}
