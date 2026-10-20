// Module: @role: IRの8段階静的検証・データ参照スコープ検証・リスク評価・実行計画ハッシュ・承認検証
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public static class ActionCatalog
{
    private static readonly Dictionary<string, ActionContract> _contracts = new(StringComparer.Ordinal)
    {
        [IrConstants.ActionChromeNavigate] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeNavigate,
            DeclaredSideEffects = SideEffectKind.Read,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string> { ["finalUrl"] = IrConstants.TypeString },
            Timeout = TimeSpan.FromSeconds(30),
            AllowRetry = true,
            MaxRetries = 2,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionChromeClick] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeClick,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Medium,
            OutputTypes = new Dictionary<string, string> { ["clicked"] = IrConstants.TypeBoolean },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionChromeFill] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeFill,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string> { ["filledValue"] = IrConstants.TypeString },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = true,
            MaxRetries = 1,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionChromeSubmit] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeSubmit,
            DeclaredSideEffects = SideEffectKind.ExternalSend,
            BaseRiskLevel = RiskLevel.High,
            OutputTypes = new Dictionary<string, string> { ["submitted"] = IrConstants.TypeBoolean },
            Timeout = TimeSpan.FromSeconds(30),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionExcelReadRange] = new ActionContract
        {
            ActionType = IrConstants.ActionExcelReadRange,
            DeclaredSideEffects = SideEffectKind.Read,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string>
            {
                ["values"] = IrConstants.TypeArrayString,
                ["table"] = IrConstants.TypeTable2D
            },
            Timeout = TimeSpan.FromSeconds(20),
            AllowRetry = true,
            MaxRetries = 2,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionExcelWriteRange] = new ActionContract
        {
            ActionType = IrConstants.ActionExcelWriteRange,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Medium,
            OutputTypes = new Dictionary<string, string> { ["writtenCells"] = IrConstants.TypeNumber },
            Timeout = TimeSpan.FromSeconds(30),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        }
    };

    public static bool TryGetContract(string actionType, out ActionContract contract) =>
        _contracts.TryGetValue(actionType, out contract!);

    public static IReadOnlyDictionary<string, ActionContract> All => _contracts;
}

public sealed class IrValidator
{
    private static readonly Regex _excelRangeRegex = new(@"^[A-Z]+[1-9][0-9]*(:[A-Z]+[1-9][0-9]*)?$", RegexOptions.Compiled);

    public (ValidationResult Result, WorkflowDefinition? Definition) ValidateJson(string rawJson)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            result.AddIssue(ValidationStage.JsonSyntax, "SYNTAX_EMPTY", "JSON input is empty.");
            return (result, null);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            result.AddIssue(ValidationStage.JsonSyntax, "SYNTAX_INVALID_JSON", $"Invalid JSON syntax: {ex.Message}");
            return (result, null);
        }

        using (doc)
        {
            ValidateSchemaElement(doc.RootElement, result);
            if (!result.IsValid)
            {
                return (result, null);
            }
        }

        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<WorkflowDefinition>(rawJson);
        }
        catch (Exception ex)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_DESERIALIZE_FAILED", ex.Message);
            return (result, null);
        }

        if (definition is null)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_NULL", "Workflow definition evaluated to null.");
            return (result, null);
        }

        ValidateDefinition(definition, result);
        return (result, definition);
    }

    public ValidationResult ValidateDefinition(WorkflowDefinition definition, ValidationResult? existingResult = null)
    {
        var result = existingResult ?? new ValidationResult();

        if (definition.SchemaVersion != IrConstants.CurrentSchemaVersion)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_VERSION_MISMATCH", $"Unsupported schemaVersion '{definition.SchemaVersion}'. Expected '{IrConstants.CurrentSchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_WORKFLOW_ID_MISSING", "workflowId is required.");
        }
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_NAME_MISSING", "Workflow name is required.");
        }
        if (definition.Version <= 0)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_VERSION_NUMBER_INVALID", "version must be >= 1.");
        }
        if (definition.Root is null)
        {
            result.AddIssue(ValidationStage.ControlFlowStructure, "FLOW_ROOT_MISSING", "Root node is required.");
            return result;
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var scopeOutputs = new Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>>(StringComparer.Ordinal);

        ValidateNodeRecursive(definition.Root, definition.Policy, nodeIds, scopeOutputs, isInsideLoop: false, result);
        return result;
    }

    private static void ValidateSchemaElement(JsonElement root, ValidationResult result)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_ROOT_NOT_OBJECT", "Root JSON element must be an object.");
            return;
        }

        string[] requiredProps = { "schemaVersion", "workflowId", "name", "version", "policy", "root" };
        foreach (string req in requiredProps)
        {
            if (!root.TryGetProperty(req, out _))
            {
                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_MISSING_PROPERTY", $"Missing required property '{req}'.");
            }
        }
    }

    private void ValidateNodeRecursive(
        WorkflowNode node,
        PolicyDeclaration policy,
        HashSet<string> seenNodeIds,
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> visibleOutputs,
        bool isInsideLoop,
        ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(node.NodeId))
        {
            result.AddIssue(ValidationStage.ControlFlowStructure, "NODE_ID_EMPTY", "nodeId cannot be empty.");
        }
        else if (!seenNodeIds.Add(node.NodeId))
        {
            result.AddIssue(ValidationStage.ControlFlowStructure, "NODE_ID_DUPLICATE", $"Duplicate nodeId '{node.NodeId}'.", node.NodeId);
        }

        switch (node.Type)
        {
            case IrConstants.NodeTypeSequence:
                if (node.Children is null || node.Children.Count == 0)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "SEQUENCE_EMPTY", "Sequence node must contain at least one child node.", node.NodeId);
                    return;
                }
                foreach (var child in node.Children)
                {
                    ValidateNodeRecursive(child, policy, seenNodeIds, visibleOutputs, isInsideLoop, result);
                }
                break;

            case IrConstants.NodeTypeIf:
                if (node.Condition?.LeftRef is null)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "IF_CONDITION_MISSING", "If node requires a valid condition.leftRef.", node.NodeId);
                }
                else
                {
                    ValidateDataReference(node.Condition.LeftRef, visibleOutputs, node.NodeId, result);
                }

                if (node.ThenChildren is null || node.ThenChildren.Count == 0)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "IF_THEN_EMPTY", "If node must have non-empty thenChildren.", node.NodeId);
                    return;
                }

                var thenScope = CloneScope(visibleOutputs);
                foreach (var child in node.ThenChildren)
                {
                    ValidateNodeRecursive(child, policy, seenNodeIds, thenScope, isInsideLoop, result);
                }

                var elseScope = CloneScope(visibleOutputs);
                if (node.ElseChildren is { Count: > 0 })
                {
                    foreach (var child in node.ElseChildren)
                    {
                        ValidateNodeRecursive(child, policy, seenNodeIds, elseScope, isInsideLoop, result);
                    }
                }

                MergeBranchScopes(visibleOutputs, thenScope, elseScope, hasElse: node.ElseChildren is { Count: > 0 });
                break;

            case IrConstants.NodeTypeForEach:
                if (node.CollectionRef is null)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "FOREACH_COLLECTION_MISSING", "ForEach node requires collectionRef.", node.NodeId);
                }
                else
                {
                    ValidateDataReference(node.CollectionRef, visibleOutputs, node.NodeId, result);
                    if (node.CollectionRef.ExpectedType != IrConstants.TypeArrayString)
                    {
                        result.AddIssue(ValidationStage.DataReferencesAndScope, "FOREACH_COLLECTION_TYPE", "ForEach collectionRef must have expectedType 'Array<String>'.", node.NodeId);
                    }
                }

                if (string.IsNullOrWhiteSpace(node.ItemVariableName))
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "FOREACH_ITEM_VAR_MISSING", "ForEach requires itemVariableName.", node.NodeId);
                }

                if (node.BodyChildren is null || node.BodyChildren.Count == 0)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "FOREACH_BODY_EMPTY", "ForEach requires non-empty bodyChildren.", node.NodeId);
                    return;
                }

                // ループ内部の変数は外部スコープに漏洩させない（仕様4.5）
                var loopScope = CloneScope(visibleOutputs);
                if (!string.IsNullOrWhiteSpace(node.NodeId) && !string.IsNullOrWhiteSpace(node.ItemVariableName))
                {
                    loopScope[node.NodeId] = new Dictionary<string, (string Type, bool IsConditional)>(StringComparer.Ordinal)
                    {
                        [node.ItemVariableName] = (IrConstants.TypeString, false)
                    };
                }
                foreach (var child in node.BodyChildren)
                {
                    ValidateNodeRecursive(child, policy, seenNodeIds, loopScope, isInsideLoop: true, result);
                }
                break;

            case IrConstants.NodeTypeWhile:
                if (node.Condition?.LeftRef is null)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "WHILE_CONDITION_MISSING", "While node requires condition.leftRef.", node.NodeId);
                }
                else
                {
                    ValidateDataReference(node.Condition.LeftRef, visibleOutputs, node.NodeId, result);
                }

                if (!node.MaxIterations.HasValue || node.MaxIterations.Value <= 0 || node.MaxIterations.Value > 10000)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "WHILE_MAX_ITERATIONS_INVALID", "While node requires maxIterations between 1 and 10000.", node.NodeId);
                }

                if (node.BodyChildren is null || node.BodyChildren.Count == 0)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "WHILE_BODY_EMPTY", "While node requires non-empty bodyChildren.", node.NodeId);
                    return;
                }

                var whileScope = CloneScope(visibleOutputs);
                foreach (var child in node.BodyChildren)
                {
                    ValidateNodeRecursive(child, policy, seenNodeIds, whileScope, isInsideLoop: true, result);
                }
                break;

            case IrConstants.NodeTypeAction:
                ValidateActionNode(node, policy, visibleOutputs, result);
                break;

            default:
                result.AddIssue(ValidationStage.ControlFlowStructure, "UNKNOWN_NODE_TYPE", $"Unknown node type '{node.Type}'.", node.NodeId);
                break;
        }
    }

    private static void ValidateActionNode(
        WorkflowNode node,
        PolicyDeclaration policy,
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> visibleOutputs,
        ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(node.ActionType))
        {
            result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "ACTION_TYPE_MISSING", "Action node requires actionType.", node.NodeId);
            return;
        }

        if (!ActionCatalog.TryGetContract(node.ActionType, out var contract))
        {
            result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "UNKNOWN_ACTION_TYPE", $"Unregistered actionType '{node.ActionType}'.", node.NodeId);
            return;
        }

        if (contract.DeclaredSideEffects.HasFlag(SideEffectKind.Unknown))
        {
            result.AddIssue(ValidationStage.SideEffectsRiskAndPolicy, "SIDE_EFFECT_UNKNOWN", "Actions with unknown side effects cannot be executed.", node.NodeId);
        }

        if (!node.Args.HasValue || node.Args.Value.ValueKind != JsonValueKind.Object)
        {
            result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "ACTION_ARGS_MISSING", $"Action '{node.ActionType}' requires an args object.", node.NodeId);
            return;
        }

        JsonElement argsElement = node.Args.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var args = argsElement.Deserialize<ChromeNavigateArgs>();
                if (args is null || string.IsNullOrWhiteSpace(args.Url) || !Uri.TryCreate(args.Url, UriKind.Absolute, out Uri? uri))
                {
                    result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "CHROME_NAV_URL_INVALID", "Chrome.Navigate requires a valid absolute URL.", node.NodeId);
                }
                else if (!policy.AllowedOrigins.Any(o => o.Matches(uri)))
                {
                    result.AddIssue(ValidationStage.TargetAndAllowedScope, "CHROME_ORIGIN_NOT_ALLOWED", $"URL origin '{uri.Scheme}://{uri.Authority}' is not in policy allowedOrigins.", node.NodeId);
                }
                break;
            }
            case IrConstants.ActionChromeClick:
            {
                var args = argsElement.Deserialize<ChromeClickArgs>();
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                break;
            }
            case IrConstants.ActionChromeFill:
            {
                var args = argsElement.Deserialize<ChromeFillArgs>();
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                if (args is null || (string.IsNullOrEmpty(args.Value) && args.ValueRef is null))
                {
                    result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "CHROME_FILL_VALUE_MISSING", "Chrome.Fill requires either value or valueRef.", node.NodeId);
                }
                if (args?.ValueRef is not null)
                {
                    ValidateDataReference(args.ValueRef, visibleOutputs, node.NodeId, result);
                    if (args.ValueRef.ExpectedType != IrConstants.TypeString)
                    {
                        result.AddIssue(ValidationStage.DataReferencesAndScope, "CHROME_FILL_REF_TYPE", "Chrome.Fill valueRef must be of type String.", node.NodeId);
                    }
                }
                break;
            }
            case IrConstants.ActionChromeSubmit:
            {
                var args = argsElement.Deserialize<ChromeSubmitArgs>();
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                if (args is null || string.IsNullOrWhiteSpace(args.ExpectedSuccessSelector))
                {
                    result.AddIssue(ValidationStage.PreAndPostConditions, "CHROME_SUBMIT_POSTCONDITION_MISSING", "Chrome.Submit requires expectedSuccessSelector for postcondition verification.", node.NodeId);
                }
                if (args is not null && !string.IsNullOrWhiteSpace(args.DestinationUrl))
                {
                    if (!Uri.TryCreate(args.DestinationUrl, UriKind.Absolute, out Uri? destUri) || !policy.AllowedOrigins.Any(o => o.Matches(destUri)))
                    {
                        result.AddIssue(ValidationStage.TargetAndAllowedScope, "CHROME_SUBMIT_ORIGIN_NOT_ALLOWED", "Chrome.Submit destinationUrl is outside allowedOrigins.", node.NodeId);
                    }
                }
                break;
            }
            case IrConstants.ActionExcelReadRange:
            {
                var args = argsElement.Deserialize<ExcelReadRangeArgs>();
                ValidateExcelTarget(args?.WorkbookPath, args?.SheetName, args?.RangeAddress, node.NodeId, result);
                break;
            }
            case IrConstants.ActionExcelWriteRange:
            {
                var args = argsElement.Deserialize<ExcelWriteRangeArgs>();
                ValidateExcelTarget(args?.WorkbookPath, args?.SheetName, args?.RangeAddress, node.NodeId, result);
                bool hasStaticValues = args?.Values is { Count: > 0 } && args.Values.All(r => r.Count > 0);
                if (!hasStaticValues && args?.ValuesRef is null)
                {
                    result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "EXCEL_WRITE_VALUES_MISSING", "Excel.WriteRange requires non-empty values or valuesRef.", node.NodeId);
                }
                if (args?.ValuesRef is not null)
                {
                    ValidateDataReference(args.ValuesRef, visibleOutputs, node.NodeId, result);
                    if (args.ValuesRef.ExpectedType is not (IrConstants.TypeArrayString or IrConstants.TypeTable2D))
                    {
                        result.AddIssue(ValidationStage.DataReferencesAndScope, "EXCEL_WRITE_REF_TYPE_MISMATCH", "Excel.WriteRange valuesRef must be Array<String> or Table2D.", node.NodeId);
                    }
                }
                break;
            }
        }

        var outputsForNode = new Dictionary<string, (string Type, bool IsConditional)>(StringComparer.Ordinal);
        foreach (var kv in contract.OutputTypes)
        {
            outputsForNode[kv.Key] = (kv.Value, false);
        }
        if (node.DeclaredOutputs is not null)
        {
            foreach (var kv in node.DeclaredOutputs)
            {
                if (!contract.OutputTypes.TryGetValue(kv.Key, out string? contractType) || contractType != kv.Value)
                {
                    result.AddIssue(ValidationStage.DataReferencesAndScope, "DECLARED_OUTPUT_INVALID", $"Declared output '{kv.Key}:{kv.Value}' does not match contract for '{node.ActionType}'.", node.NodeId);
                }
            }
        }
        visibleOutputs[node.NodeId] = outputsForNode;
    }

    private static void ValidateLocator(SemanticLocator? locator, PolicyDeclaration policy, string nodeId, ValidationResult result)
    {
        if (locator is null)
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "LOCATOR_MISSING", "Chrome action requires a target locator.", nodeId);
            return;
        }
        if (locator.UsesCoordinatesOnly())
        {
            if (!policy.AllowCoordinateClick)
            {
                result.AddIssue(ValidationStage.TargetAndAllowedScope, "COORDINATE_LOCATOR_FORBIDDEN", "Coordinate-only operation is prohibited unless policy.allowCoordinateClick is true.", nodeId);
            }
        }
        else if (!locator.HasSemanticTarget())
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "LOCATOR_AMBIGUOUS_OR_EMPTY", "Locator must specify semantic attributes (Role+AccessibleName, Label, StableId, or CssSelector).", nodeId);
        }
    }

    private static void ValidateExcelTarget(string? workbookPath, string? sheetName, string? rangeAddress, string nodeId, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(workbookPath) || workbookPath.Equals("ActiveWorkbook", StringComparison.OrdinalIgnoreCase))
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "EXCEL_WORKBOOK_IMPLICIT_OR_EMPTY", "Excel action requires an explicit workbookPath; implicit ActiveWorkbook is prohibited.", nodeId);
        }
        if (string.IsNullOrWhiteSpace(sheetName) || sheetName.Equals("ActiveSheet", StringComparison.OrdinalIgnoreCase))
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "EXCEL_SHEET_IMPLICIT_OR_EMPTY", "Excel action requires an explicit sheetName; implicit ActiveSheet is prohibited.", nodeId);
        }
        if (string.IsNullOrWhiteSpace(rangeAddress) || !_excelRangeRegex.IsMatch(rangeAddress))
        {
            result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "EXCEL_RANGE_INVALID", $"Explicit A1-style rangeAddress is required (got '{rangeAddress}').", nodeId);
        }
    }

    private static void ValidateDataReference(
        DataReference reference,
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> visibleOutputs,
        string currentNodeId,
        ValidationResult result)
    {
        if (!visibleOutputs.TryGetValue(reference.SourceNodeId, out var nodeOutputs))
        {
            result.AddIssue(ValidationStage.DataReferencesAndScope, "REF_SOURCE_UNREACHABLE_OR_UNDEFINED", $"Referenced sourceNodeId '{reference.SourceNodeId}' is not defined or not reachable in current scope.", currentNodeId);
            return;
        }

        if (!nodeOutputs.TryGetValue(reference.OutputName, out var outputMeta))
        {
            result.AddIssue(ValidationStage.DataReferencesAndScope, "REF_OUTPUT_NAME_NOT_FOUND", $"Output '{reference.OutputName}' does not exist on node '{reference.SourceNodeId}'.", currentNodeId);
            return;
        }

        if (!string.Equals(outputMeta.Type, reference.ExpectedType, StringComparison.Ordinal))
        {
            result.AddIssue(ValidationStage.DataReferencesAndScope, "REF_TYPE_INCOMPATIBLE", $"Reference type mismatch: '{reference.SourceNodeId}.{reference.OutputName}' produces '{outputMeta.Type}', but '{reference.ExpectedType}' was expected.", currentNodeId);
        }

        if (outputMeta.IsConditional && !reference.IsOptional)
        {
            result.AddIssue(ValidationStage.DataReferencesAndScope, "REF_CONDITIONAL_NOT_OPTIONAL", $"Output '{reference.SourceNodeId}.{reference.OutputName}' is not guaranteed on all branch paths and must be marked isOptional=true.", currentNodeId);
        }
    }

    private static Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> CloneScope(
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> source)
    {
        var clone = new Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>>(StringComparer.Ordinal);
        foreach (var kv in source)
        {
            clone[kv.Key] = new Dictionary<string, (string Type, bool IsConditional)>(kv.Value, StringComparer.Ordinal);
        }
        return clone;
    }

    private static void MergeBranchScopes(
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> parentScope,
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> thenScope,
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> elseScope,
        bool hasElse)
    {
        var allNodeIds = thenScope.Keys.Union(elseScope.Keys, StringComparer.Ordinal).ToList();
        foreach (string nodeId in allNodeIds)
        {
            if (parentScope.ContainsKey(nodeId))
            {
                continue;
            }

            bool inThen = thenScope.TryGetValue(nodeId, out var thenOutputs);
            Dictionary<string, (string Type, bool IsConditional)>? elseOutputs = null;
            bool inElse = hasElse && elseScope.TryGetValue(nodeId, out elseOutputs);

            var mergedOutputs = new Dictionary<string, (string Type, bool IsConditional)>(StringComparer.Ordinal);
            if (inThen && thenOutputs is not null)
            {
                foreach (var kv in thenOutputs)
                {
                    bool guaranteedInBoth = inElse && elseOutputs is not null && elseOutputs.ContainsKey(kv.Key);
                    mergedOutputs[kv.Key] = (kv.Value.Type, IsConditional: !guaranteedInBoth || kv.Value.IsConditional);
                }
            }
            if (inElse && elseOutputs is not null)
            {
                foreach (var kv in elseOutputs)
                {
                    if (!mergedOutputs.ContainsKey(kv.Key))
                    {
                        mergedOutputs[kv.Key] = (kv.Value.Type, IsConditional: true);
                    }
                }
            }

            parentScope[nodeId] = mergedOutputs;
        }
    }
}

public sealed class PolicyEngine
{
    private readonly IrValidator _validator;

    public PolicyEngine(IrValidator validator)
    {
        _validator = validator;
    }

    public (ValidationResult Validation, ExecutionPlan? Plan) BuildAndEvaluatePlan(
        string versionId,
        WorkflowDefinition definition)
    {
        var validation = _validator.ValidateDefinition(definition);
        if (!validation.IsValid)
        {
            return (validation, null);
        }

        var plannedActions = new List<PlannedActionItem>();
        CollectPlannedActions(definition.Root!, definition.Policy, plannedActions, validation);

        if (!validation.IsValid)
        {
            return (validation, null);
        }

        RiskLevel maxRisk = plannedActions.Count > 0
            ? plannedActions.Max(a => a.EvaluatedRisk)
            : RiskLevel.Low;

        string planHash = ComputePlanHash(versionId, definition, plannedActions);

        var plan = new ExecutionPlan
        {
            VersionId = versionId,
            WorkflowId = definition.WorkflowId,
            VersionNumber = definition.Version,
            PlanHash = planHash,
            Definition = definition,
            PlannedActions = plannedActions,
            MaxRiskLevel = maxRisk
        };

        return (validation, plan);
    }

    private static void CollectPlannedActions(
        WorkflowNode node,
        PolicyDeclaration policy,
        List<PlannedActionItem> items,
        ValidationResult validation)
    {
        switch (node.Type)
        {
            case IrConstants.NodeTypeSequence:
                foreach (var child in node.Children!)
                {
                    CollectPlannedActions(child, policy, items, validation);
                }
                break;
            case IrConstants.NodeTypeIf:
                foreach (var child in node.ThenChildren!)
                {
                    CollectPlannedActions(child, policy, items, validation);
                }
                if (node.ElseChildren is not null)
                {
                    foreach (var child in node.ElseChildren)
                    {
                        CollectPlannedActions(child, policy, items, validation);
                    }
                }
                break;
            case IrConstants.NodeTypeForEach:
            case IrConstants.NodeTypeWhile:
                foreach (var child in node.BodyChildren!)
                {
                    CollectPlannedActions(child, policy, items, validation);
                }
                break;
            case IrConstants.NodeTypeAction:
                var contract = ActionCatalog.All[node.ActionType!];
                var (sideEffects, risk, targetSummary) = EvaluateActionRiskAndTarget(node, contract, policy);
                if (risk == RiskLevel.Prohibited)
                {
                    validation.AddIssue(
                        ValidationStage.SideEffectsRiskAndPolicy,
                        "POLICY_PROHIBITED_OPERATION",
                        $"Action '{node.NodeId}' ({node.ActionType}) is evaluated as Prohibited on target '{targetSummary}'.",
                        node.NodeId);
                }

                items.Add(new PlannedActionItem
                {
                    NodeId = node.NodeId,
                    ActionType = node.ActionType!,
                    TargetSummary = targetSummary,
                    SideEffects = sideEffects,
                    EvaluatedRisk = risk,
                    RequiresIndividualApproval = risk >= RiskLevel.High
                });
                break;
        }
    }

    private static (SideEffectKind SideEffects, RiskLevel Risk, string TargetSummary) EvaluateActionRiskAndTarget(
        WorkflowNode node,
        ActionContract contract,
        PolicyDeclaration policy)
    {
        SideEffectKind sideEffects = contract.DeclaredSideEffects;
        RiskLevel risk = contract.BaseRiskLevel;
        string targetSummary = node.ActionType!;

        JsonElement argsElement = node.Args!.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var args = argsElement.Deserialize<ChromeNavigateArgs>()!;
                targetSummary = args.Url;
                if (Uri.TryCreate(args.Url, UriKind.Absolute, out Uri? uri) && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    // 非暗号化HTTP通信はリスクをMediumに引き上げる
                    risk = (RiskLevel)Math.Max((int)risk, (int)RiskLevel.Medium);
                }
                break;
            }
            case IrConstants.ActionChromeClick:
            {
                var args = argsElement.Deserialize<ChromeClickArgs>()!;
                targetSummary = args.Locator?.AccessibleName ?? args.Locator?.Label ?? args.Locator?.StableId ?? args.Locator?.CssSelector ?? "Coordinates";
                if (args.IsDestructive)
                {
                    sideEffects |= SideEffectKind.Destructive;
                    risk = RiskLevel.Prohibited;
                }
                else if (args.Locator?.UsesCoordinatesOnly() == true)
                {
                    risk = RiskLevel.High;
                }
                break;
            }
            case IrConstants.ActionChromeFill:
            {
                var args = argsElement.Deserialize<ChromeFillArgs>()!;
                targetSummary = args.Locator?.Label ?? args.Locator?.AccessibleName ?? args.Locator?.StableId ?? "Input";
                if (args.IsSensitive)
                {
                    risk = RiskLevel.High;
                }
                break;
            }
            case IrConstants.ActionChromeSubmit:
            {
                var args = argsElement.Deserialize<ChromeSubmitArgs>()!;
                targetSummary = args.DestinationUrl;
                risk = RiskLevel.High;
                break;
            }
            case IrConstants.ActionExcelReadRange:
            {
                var args = argsElement.Deserialize<ExcelReadRangeArgs>()!;
                targetSummary = $"{args.WorkbookPath}!{args.SheetName}!{args.RangeAddress}";
                if (!IsPathAllowed(args.WorkbookPath, policy.AllowedExcelDirectories))
                {
                    risk = RiskLevel.Prohibited;
                }
                break;
            }
            case IrConstants.ActionExcelWriteRange:
            {
                var args = argsElement.Deserialize<ExcelWriteRangeArgs>()!;
                targetSummary = $"{args.WorkbookPath}!{args.SheetName}!{args.RangeAddress}";
                if (!IsPathAllowed(args.WorkbookPath, policy.AllowedExcelDirectories))
                {
                    risk = RiskLevel.Prohibited;
                }
                else if (args.AllowOverwrite)
                {
                    risk = RiskLevel.High;
                }
                break;
            }
        }

        return (sideEffects, risk, targetSummary);
    }

    private static bool IsPathAllowed(string workbookPath, List<string> allowedDirectories)
    {
        if (allowedDirectories.Count == 0)
        {
            return true;
        }
        try
        {
            string fullPath = Path.GetFullPath(workbookPath);
            foreach (string dir in allowedDirectories)
            {
                string fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputePlanHash(string versionId, WorkflowDefinition definition, List<PlannedActionItem> actions)
    {
        var sb = new StringBuilder();
        sb.Append(versionId).Append('|').Append(definition.WorkflowId).Append('|').Append(definition.Version).Append('|');
        foreach (var origin in definition.Policy.AllowedOrigins.OrderBy(o => o.Host).ThenBy(o => o.Port))
        {
            sb.Append(origin.Scheme).Append("://").Append(origin.Host).Append(':').Append(origin.Port).Append(';');
        }
        foreach (var act in actions)
        {
            sb.Append(act.NodeId).Append(':')
              .Append(act.ActionType).Append(':')
              .Append(act.TargetSummary).Append(':')
              .Append((int)act.SideEffects).Append(':')
              .Append((int)act.EvaluatedRisk).Append('|');
        }

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }
}

public sealed class ApprovalService
{
    public (bool IsApproved, string Reason) VerifyApprovalForPlan(ExecutionPlan plan, ApprovalRecord? approval)
    {
        if (plan.PlannedActions.Any(a => a.EvaluatedRisk == RiskLevel.Prohibited))
        {
            return (false, "Prohibited operations cannot be executed even with approval.");
        }

        if (approval is null)
        {
            return (false, "Workflow execution requires explicit prior approval.");
        }

        if (approval.Decision != ApprovalDecision.Approved)
        {
            return (false, "The workflow execution plan was rejected.");
        }

        if (!string.Equals(approval.VersionId, plan.VersionId, StringComparison.Ordinal))
        {
            return (false, $"Approval version mismatch (approved '{approval.VersionId}', current '{plan.VersionId}').");
        }

        if (!string.Equals(approval.PlanHash, plan.PlanHash, StringComparison.Ordinal))
        {
            return (false, "Execution plan has changed since approval. Re-approval is required.");
        }

        var requiredIndividualNodes = plan.PlannedActions
            .Where(a => a.RequiresIndividualApproval)
            .Select(a => a.NodeId)
            .ToList();

        foreach (string highRiskNodeId in requiredIndividualNodes)
        {
            if (!approval.ApprovedHighRiskNodeIds.Contains(highRiskNodeId, StringComparer.Ordinal))
            {
                return (false, $"High-risk action node '{highRiskNodeId}' requires explicit individual approval.");
            }
        }

        return (true, "Approved");
    }
}
