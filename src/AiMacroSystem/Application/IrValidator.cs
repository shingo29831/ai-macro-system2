// Module: @role: Workflow IR の8段階静的検証・スキーマ検証・データ参照スコープ解決
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed class IrValidator
{
    private static readonly Regex _excelRangeRegex = new(@"^[A-Z]+[1-9][0-9]*(:[A-Z]+[1-9][0-9]*)?$", RegexOptions.Compiled);
    private static readonly Regex _singleCellRegex = new(@"^([A-Z]+)([1-9][0-9]*)$", RegexOptions.Compiled);

    private static readonly HashSet<string> _allowedRootProps = new(StringComparer.Ordinal)
    {
        "schemaVersion", "workflowId", "name", "version", "policy", "root"
    };

    private static readonly HashSet<string> _requiredPolicyProps = new(StringComparer.Ordinal)
    {
        "allowedOrigins", "allowCoordinateClick", "allowExternalAi"
    };

    private static readonly HashSet<string> _allowedPolicyProps = new(StringComparer.Ordinal)
    {
        "allowedOrigins", "allowCoordinateClick", "allowExternalAi", "allowedExcelDirectories"
    };

    private static readonly HashSet<string> _allowedOriginProps = new(StringComparer.Ordinal)
    {
        "scheme", "host", "port"
    };

    private static readonly HashSet<string> _allowedDataRefProps = new(StringComparer.Ordinal)
    {
        "sourceNodeId", "outputName", "expectedType", "isOptional"
    };

    private static readonly HashSet<string> _allowedConditionProps = new(StringComparer.Ordinal)
    {
        "leftRef", "operator", "rightLiteral"
    };

    private static readonly HashSet<string> _allowedNodeProps = new(StringComparer.Ordinal)
    {
        "nodeId", "type", "actionType", "args", "children", "condition",
        "thenChildren", "elseChildren", "collectionRef", "itemVariableName",
        "bodyChildren", "maxIterations", "declaredOutputs", "sourceCandidateIds"
    };

    private static readonly HashSet<string> _allowedConditionOperators = new(StringComparer.Ordinal)
    {
        "Equals", "NotEquals", "NotEmpty"
    };

    public static bool TryGetRangeDimensions(string rangeAddress, out int rows, out int cols)
    {
        rows = 0;
        cols = 0;
        if (string.IsNullOrWhiteSpace(rangeAddress) || !_excelRangeRegex.IsMatch(rangeAddress))
        {
            return false;
        }

        string[] parts = rangeAddress.Split(':');
        if (!TryParseCellCoord(parts[0], out int r1, out int c1))
        {
            return false;
        }
        if (parts.Length == 1)
        {
            rows = 1;
            cols = 1;
            return true;
        }
        if (!TryParseCellCoord(parts[1], out int r2, out int c2))
        {
            return false;
        }
        rows = Math.Abs(r2 - r1) + 1;
        cols = Math.Abs(c2 - c1) + 1;
        return true;
    }

    private static bool TryParseCellCoord(string cell, out int row, out int col)
    {
        row = 0;
        col = 0;
        var m = _singleCellRegex.Match(cell.Trim().ToUpperInvariant());
        if (!m.Success)
        {
            return false;
        }
        row = int.Parse(m.Groups[2].Value);
        foreach (char ch in m.Groups[1].Value)
        {
            col = (col * 26) + (ch - 'A' + 1);
        }
        return true;
    }

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

        foreach (var origin in definition.Policy.AllowedOrigins)
        {
            if (!string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(origin.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                result.AddIssue(ValidationStage.JsonSchema, "ORIGIN_SCHEME_INVALID", $"Origin scheme '{origin.Scheme}' must be 'https' or 'http'.");
            }
            if (string.IsNullOrWhiteSpace(origin.Host))
            {
                result.AddIssue(ValidationStage.JsonSchema, "ORIGIN_HOST_EMPTY", "Origin host cannot be empty.");
            }
            if (origin.Port is < 1 or > 65535)
            {
                result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "ORIGIN_PORT_OUT_OF_RANGE", $"Origin port '{origin.Port}' must be between 1 and 65535.");
            }
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var scopeOutputs = new Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>>(StringComparer.Ordinal);

        ValidateNodeRecursive(definition.Root, definition.Policy, nodeIds, scopeOutputs, isInsideLoop: false, result);

        // 仕様7の8段階検証順序（構文→Schema→引数型・値域→参照・スコープ→制御フロー→前提・事後条件→対象・許可範囲→副作用・リスク・ポリシー）に整列する
        if (result.Issues.Count > 1)
        {
            var ordered = result.Issues.OrderBy(i => (int)i.Stage).ToList();
            result.Issues.Clear();
            result.Issues.AddRange(ordered);
        }

        return result;
    }

    private static void ValidateSchemaElement(JsonElement root, ValidationResult result)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_ROOT_NOT_OBJECT", "Root JSON element must be an object.");
            return;
        }

        foreach (string req in _allowedRootProps)
        {
            if (!root.TryGetProperty(req, out _))
            {
                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_MISSING_PROPERTY", $"Missing required property '{req}'.");
            }
        }

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!_allowedRootProps.Contains(prop.Name))
            {
                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_ADDITIONAL_PROPERTY", $"Unknown root property '{prop.Name}' is not allowed by schema.");
            }
        }

        if (root.TryGetProperty("policy", out JsonElement policyElem))
        {
            if (policyElem.ValueKind != JsonValueKind.Object)
            {
                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_POLICY_NOT_OBJECT", "policy must be a JSON object.");
            }
            else
            {
                foreach (string req in _requiredPolicyProps)
                {
                    if (!policyElem.TryGetProperty(req, out _))
                    {
                        result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_POLICY_MISSING_PROPERTY", $"Missing required policy property '{req}'.");
                    }
                }
                foreach (JsonProperty prop in policyElem.EnumerateObject())
                {
                    if (!_allowedPolicyProps.Contains(prop.Name))
                    {
                        result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_POLICY_ADDITIONAL_PROPERTY", $"Unknown policy property '{prop.Name}' is not allowed by schema.");
                    }
                }
                if (policyElem.TryGetProperty("allowedOrigins", out JsonElement originsElem) && originsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement originElem in originsElem.EnumerateArray())
                    {
                        if (originElem.ValueKind != JsonValueKind.Object)
                        {
                            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_ORIGIN_NOT_OBJECT", "OriginRule must be a JSON object.");
                            continue;
                        }
                        foreach (string req in _allowedOriginProps)
                        {
                            if (!originElem.TryGetProperty(req, out _))
                            {
                                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_ORIGIN_MISSING_PROPERTY", $"Missing required originRule property '{req}'.");
                            }
                        }
                        foreach (JsonProperty prop in originElem.EnumerateObject())
                        {
                            if (!_allowedOriginProps.Contains(prop.Name))
                            {
                                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_ORIGIN_ADDITIONAL_PROPERTY", $"Unknown originRule property '{prop.Name}' is not allowed by schema.");
                            }
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("root", out JsonElement rootNodeElem))
        {
            ValidateNodeSchemaElementRecursive(rootNodeElem, result);
        }
    }

    private static void ValidateNodeSchemaElementRecursive(JsonElement nodeElem, ValidationResult result)
    {
        if (nodeElem.ValueKind != JsonValueKind.Object)
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_NODE_NOT_OBJECT", "Workflow node must be a JSON object.");
            return;
        }

        if (!nodeElem.TryGetProperty("nodeId", out _))
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_NODE_MISSING_ID", "Workflow node is missing required property 'nodeId'.");
        }
        if (!nodeElem.TryGetProperty("type", out _))
        {
            result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_NODE_MISSING_TYPE", "Workflow node is missing required property 'type'.");
        }

        foreach (JsonProperty prop in nodeElem.EnumerateObject())
        {
            if (!_allowedNodeProps.Contains(prop.Name))
            {
                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_NODE_ADDITIONAL_PROPERTY", $"Unknown workflowNode property '{prop.Name}' is not allowed by schema.");
            }
        }

        if (nodeElem.TryGetProperty("collectionRef", out JsonElement colRefElem) && colRefElem.ValueKind == JsonValueKind.Object)
        {
            ValidateDataRefSchemaElement(colRefElem, result);
        }

        if (nodeElem.TryGetProperty("condition", out JsonElement condElem) && condElem.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in condElem.EnumerateObject())
            {
                if (!_allowedConditionProps.Contains(prop.Name))
                {
                    result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_CONDITION_ADDITIONAL_PROPERTY", $"Unknown conditionExpression property '{prop.Name}' is not allowed by schema.");
                }
            }
            if (condElem.TryGetProperty("leftRef", out JsonElement leftRefElem) && leftRefElem.ValueKind == JsonValueKind.Object)
            {
                ValidateDataRefSchemaElement(leftRefElem, result);
            }
        }

        string[] childArrayNames = { "children", "thenChildren", "elseChildren", "bodyChildren" };
        foreach (string arrName in childArrayNames)
        {
            if (nodeElem.TryGetProperty(arrName, out JsonElement arrElem) && arrElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in arrElem.EnumerateArray())
                {
                    ValidateNodeSchemaElementRecursive(child, result);
                }
            }
        }
    }

    private static void ValidateDataRefSchemaElement(JsonElement refElem, ValidationResult result)
    {
        foreach (JsonProperty prop in refElem.EnumerateObject())
        {
            if (!_allowedDataRefProps.Contains(prop.Name))
            {
                result.AddIssue(ValidationStage.JsonSchema, "SCHEMA_DATAREF_ADDITIONAL_PROPERTY", $"Unknown dataReference property '{prop.Name}' is not allowed by schema.");
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
                if (node.ActionType is not null || node.Args.HasValue || node.Condition is not null ||
                    node.ThenChildren is not null || node.ElseChildren is not null ||
                    node.CollectionRef is not null || node.ItemVariableName is not null ||
                    node.BodyChildren is not null || node.MaxIterations.HasValue)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "SEQUENCE_FORBIDDEN_FIELDS", "Sequence node contains fields not permitted for Sequence.", node.NodeId);
                }
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
                if (node.ActionType is not null || node.Args.HasValue || node.Children is not null ||
                    node.CollectionRef is not null || node.ItemVariableName is not null ||
                    node.BodyChildren is not null || node.MaxIterations.HasValue)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "IF_FORBIDDEN_FIELDS", "If node contains fields not permitted for If.", node.NodeId);
                }
                ValidateConditionExpression(node.Condition, visibleOutputs, node.NodeId, "IF", result);

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
                if (node.ActionType is not null || node.Args.HasValue || node.Children is not null ||
                    node.Condition is not null || node.ThenChildren is not null || node.ElseChildren is not null ||
                    node.MaxIterations.HasValue)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "FOREACH_FORBIDDEN_FIELDS", "ForEach node contains fields not permitted for ForEach.", node.NodeId);
                }
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
                if (node.ActionType is not null || node.Args.HasValue || node.Children is not null ||
                    node.ThenChildren is not null || node.ElseChildren is not null ||
                    node.CollectionRef is not null || node.ItemVariableName is not null)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "WHILE_FORBIDDEN_FIELDS", "While node contains fields not permitted for While.", node.NodeId);
                }
                ValidateConditionExpression(node.Condition, visibleOutputs, node.NodeId, "WHILE", result);

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
                if (node.Children is not null || node.Condition is not null ||
                    node.ThenChildren is not null || node.ElseChildren is not null ||
                    node.CollectionRef is not null || node.ItemVariableName is not null ||
                    node.BodyChildren is not null || node.MaxIterations.HasValue)
                {
                    result.AddIssue(ValidationStage.ControlFlowStructure, "ACTION_FORBIDDEN_CHILDREN", "Action node cannot contain child nodes or branch/loop definitions.", node.NodeId);
                }
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

        bool argsDeserialized = true;
        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                if (!TryDeserializeArgs<ChromeNavigateArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
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
                if (!TryDeserializeArgs<ChromeClickArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                break;
            }
            case IrConstants.ActionChromeFill:
            {
                if (!TryDeserializeArgs<ChromeFillArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
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
                if (!TryDeserializeArgs<ChromeSubmitArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
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
                if (!TryDeserializeArgs<ExcelReadRangeArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
                ValidateExcelTarget(args?.WorkbookPath, args?.SheetName, args?.RangeAddress, policy, node.NodeId, result);
                break;
            }
            case IrConstants.ActionExcelWriteRange:
            {
                if (!TryDeserializeArgs<ExcelWriteRangeArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
                ValidateExcelTarget(args?.WorkbookPath, args?.SheetName, args?.RangeAddress, policy, node.NodeId, result);
                bool hasStaticValues = args?.Values is { Count: > 0 } && args.Values.All(r => r.Count > 0);
                if (!hasStaticValues && args?.ValuesRef is null)
                {
                    result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "EXCEL_WRITE_VALUES_MISSING", "Excel.WriteRange requires non-empty values or valuesRef.", node.NodeId);
                }
                if (hasStaticValues && !string.IsNullOrWhiteSpace(args?.RangeAddress) &&
                    TryGetRangeDimensions(args.RangeAddress, out int expectedRows, out int expectedCols))
                {
                    int actualRows = args.Values!.Count;
                    int actualCols = args.Values.Max(r => r.Count);
                    if (actualRows != expectedRows || actualCols != expectedCols)
                    {
                        result.AddIssue(
                            ValidationStage.ArgumentTypesAndRanges,
                            "EXCEL_WRITE_SIZE_MISMATCH",
                            $"Excel.WriteRange values size ({actualRows}x{actualCols}) does not match rangeAddress '{args.RangeAddress}' ({expectedRows}x{expectedCols}).",
                            node.NodeId);
                    }
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
            case IrConstants.ActionCustomAppClick:
            {
                if (!TryDeserializeArgs<CustomAppClickArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
                if (args is null || string.IsNullOrWhiteSpace(args.ApplicationName))
                {
                    result.AddIssue(ValidationStage.TargetAndAllowedScope, "CUSTOM_APP_NAME_MISSING", "CustomApp.Click requires an explicit applicationName.", node.NodeId);
                }
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                break;
            }
            case IrConstants.ActionCustomAppFill:
            {
                if (!TryDeserializeArgs<CustomAppFillArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
                if (args is null || string.IsNullOrWhiteSpace(args.ApplicationName))
                {
                    result.AddIssue(ValidationStage.TargetAndAllowedScope, "CUSTOM_APP_NAME_MISSING", "CustomApp.Fill requires an explicit applicationName.", node.NodeId);
                }
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                if (args is null || (string.IsNullOrEmpty(args.Value) && args.ValueRef is null))
                {
                    result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "CUSTOM_APP_FILL_VALUE_MISSING", "CustomApp.Fill requires either value or valueRef.", node.NodeId);
                }
                if (args?.ValueRef is not null)
                {
                    ValidateDataReference(args.ValueRef, visibleOutputs, node.NodeId, result);
                    if (args.ValueRef.ExpectedType != IrConstants.TypeString)
                    {
                        result.AddIssue(ValidationStage.DataReferencesAndScope, "CUSTOM_APP_FILL_REF_TYPE", "CustomApp.Fill valueRef must be of type String.", node.NodeId);
                    }
                }
                break;
            }
            case IrConstants.ActionCustomAppReadText:
            {
                if (!TryDeserializeArgs<CustomAppReadTextArgs>(argsElement, node.NodeId, result, out var args))
                {
                    argsDeserialized = false;
                    break;
                }
                if (args is null || string.IsNullOrWhiteSpace(args.ApplicationName))
                {
                    result.AddIssue(ValidationStage.TargetAndAllowedScope, "CUSTOM_APP_NAME_MISSING", "CustomApp.ReadText requires an explicit applicationName.", node.NodeId);
                }
                ValidateLocator(args?.Locator, policy, node.NodeId, result);
                break;
            }
        }

        if (argsDeserialized)
        {
            var (_, evaluatedRisk, targetSummary, _) = PolicyEngine.EvaluateActionRiskAndTarget(node, contract, policy);
            if (evaluatedRisk == RiskLevel.Prohibited)
            {
                result.AddIssue(
                    ValidationStage.SideEffectsRiskAndPolicy,
                    "POLICY_PROHIBITED_OPERATION",
                    $"Action '{node.NodeId}' ({node.ActionType}) is evaluated as Prohibited on target '{targetSummary}'.",
                    node.NodeId);
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

    private static void ValidateConditionExpression(
        ConditionExpression? condition,
        Dictionary<string, Dictionary<string, (string Type, bool IsConditional)>> visibleOutputs,
        string nodeId,
        string prefix,
        ValidationResult result)
    {
        if (condition?.LeftRef is null)
        {
            result.AddIssue(ValidationStage.ControlFlowStructure, $"{prefix}_CONDITION_MISSING", $"{prefix} node requires condition.leftRef.", nodeId);
            return;
        }

        ValidateDataReference(condition.LeftRef, visibleOutputs, nodeId, result);

        if (string.IsNullOrWhiteSpace(condition.Operator) || !_allowedConditionOperators.Contains(condition.Operator))
        {
            result.AddIssue(ValidationStage.ControlFlowStructure, $"{prefix}_OPERATOR_INVALID", $"Unsupported condition operator '{condition.Operator}'.", nodeId);
        }
        else if ((condition.Operator == "Equals" || condition.Operator == "NotEquals") && condition.RightLiteral is null)
        {
            result.AddIssue(ValidationStage.ControlFlowStructure, $"{prefix}_RIGHT_LITERAL_MISSING", $"Condition operator '{condition.Operator}' requires rightLiteral.", nodeId);
        }
    }

    private static void ValidateLocator(SemanticLocator? locator, PolicyDeclaration policy, string nodeId, ValidationResult result)
    {
        if (locator is null)
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "LOCATOR_MISSING", "Chrome action requires a target locator.", nodeId);
            return;
        }
        if (locator.CoordinateX.HasValue != locator.CoordinateY.HasValue)
        {
            result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "LOCATOR_COORDINATE_INCOMPLETE", "CoordinateX and CoordinateY must both be specified together.", nodeId);
            return;
        }
        if (locator.CoordinateX.HasValue && (locator.CoordinateX.Value < 0 || locator.CoordinateY!.Value < 0))
        {
            result.AddIssue(ValidationStage.ArgumentTypesAndRanges, "LOCATOR_COORDINATE_OUT_OF_RANGE", "Coordinates must be non-negative integers.", nodeId);
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

    private static bool TryDeserializeArgs<T>(JsonElement element, string nodeId, ValidationResult result, out T? args)
    {
        try
        {
            args = element.Deserialize<T>();
            return true;
        }
        catch (JsonException ex)
        {
            args = default;
            result.AddIssue(
                ValidationStage.ArgumentTypesAndRanges,
                "ACTION_ARGS_TYPE_MISMATCH",
                $"Action arguments type mismatch: {ex.Message}",
                nodeId);
            return false;
        }
    }

    private static void ValidateExcelTarget(string? workbookPath, string? sheetName, string? rangeAddress, PolicyDeclaration policy, string nodeId, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(workbookPath) || workbookPath.Equals("ActiveWorkbook", StringComparison.OrdinalIgnoreCase))
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "EXCEL_WORKBOOK_IMPLICIT_OR_EMPTY", "Excel action requires an explicit workbookPath; implicit ActiveWorkbook is prohibited.", nodeId);
        }
        else if (!PolicyEngine.IsPathAllowed(workbookPath, policy.AllowedExcelDirectories))
        {
            result.AddIssue(ValidationStage.TargetAndAllowedScope, "EXCEL_PATH_NOT_ALLOWED", $"Excel workbook path '{workbookPath}' is outside policy allowedExcelDirectories.", nodeId);
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
