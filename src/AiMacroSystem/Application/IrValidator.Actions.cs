// Module: @role: 各種アクションノード引数（Chrome・Excel・CustomApp）の型・ターゲット・ポリシー妥当性検証
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed partial class IrValidator
{
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
}
