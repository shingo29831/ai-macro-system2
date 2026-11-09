// Module: @role: セキュリティポリシー適合性検証・リスク評価・実行計画ハッシュ計算
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

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
                var (sideEffects, risk, targetSummary, inputSummary) = EvaluateActionRiskAndTarget(node, contract, policy);
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
                    InputSummary = inputSummary,
                    SideEffects = sideEffects,
                    EvaluatedRisk = risk,
                    RequiresIndividualApproval = risk >= RiskLevel.High
                });
                break;
        }
    }

    public (bool IsValid, string FailureReason) VerifyRuntimeNodeMatchesApprovedPlan(
        WorkflowNode node,
        PolicyDeclaration policy,
        ExecutionPlan approvedPlan)
    {
        var plannedItem = approvedPlan.PlannedActions.FirstOrDefault(p => string.Equals(p.NodeId, node.NodeId, StringComparison.Ordinal));
        if (plannedItem is null || string.IsNullOrWhiteSpace(node.ActionType) ||
            !string.Equals(plannedItem.ActionType, node.ActionType, StringComparison.Ordinal))
        {
            return (false, "Runtime action node or actionType does not match the approved execution plan.");
        }

        if (!ActionCatalog.TryGetContract(node.ActionType, out var contract) || !node.Args.HasValue)
        {
            return (false, "Runtime action contract or arguments are missing.");
        }

        var (sideEffects, risk, targetSummary, inputSummary) = EvaluateActionRiskAndTarget(node, contract, policy);
        if (risk == RiskLevel.Prohibited)
        {
            return (false, $"Runtime action '{node.NodeId}' is Prohibited by policy.");
        }

        if (sideEffects != plannedItem.SideEffects ||
            risk != plannedItem.EvaluatedRisk ||
            !string.Equals(targetSummary, plannedItem.TargetSummary, StringComparison.Ordinal) ||
            !string.Equals(inputSummary, plannedItem.InputSummary, StringComparison.Ordinal))
        {
            return (false, $"Runtime target, arguments, side effects, or risk level for '{node.NodeId}' diverged from approved plan.");
        }

        return (true, string.Empty);
    }

    internal static (SideEffectKind SideEffects, RiskLevel Risk, string TargetSummary, string InputSummary) EvaluateActionRiskAndTarget(
        WorkflowNode node,
        ActionContract contract,
        PolicyDeclaration policy)
    {
        SideEffectKind sideEffects = contract.DeclaredSideEffects;
        RiskLevel risk = contract.BaseRiskLevel;
        string targetSummary = node.ActionType!;
        string inputSummary = "-";

        JsonElement argsElement = node.Args!.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var args = argsElement.Deserialize<ChromeNavigateArgs>()!;
                targetSummary = args.Url;
                inputSummary = args.Url;
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
                inputSummary = args.IsDestructive ? "Destructive Click" : "Click";
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
                inputSummary = args.IsSensitive
                    ? SensitiveDataProtector.RedactedPlaceholder
                    : (args.ValueRef is not null ? $"Ref:{args.ValueRef.SourceNodeId}.{args.ValueRef.OutputName}" : (args.Value ?? string.Empty));
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
                inputSummary = $"Submit -> {args.ExpectedSuccessSelector}";
                risk = RiskLevel.High;
                break;
            }
            case IrConstants.ActionExcelReadRange:
            {
                var args = argsElement.Deserialize<ExcelReadRangeArgs>()!;
                targetSummary = $"{args.WorkbookPath}!{args.SheetName}!{args.RangeAddress}";
                inputSummary = $"Read {args.RangeAddress}";
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
                inputSummary = args.ValuesRef is not null
                    ? $"Ref:{args.ValuesRef.SourceNodeId}.{args.ValuesRef.OutputName}"
                    : $"{args.Values?.Count ?? 0}行データ書き込み";
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
            case IrConstants.ActionCustomAppClick:
            {
                var args = argsElement.Deserialize<CustomAppClickArgs>()!;
                string ctrl = args.Locator?.AccessibleName ?? args.Locator?.Label ?? args.Locator?.StableId ?? "Control";
                targetSummary = $"{args.ApplicationName} [{ctrl}]";
                inputSummary = args.IsDestructive ? "Destructive Click" : "Click";
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
            case IrConstants.ActionCustomAppFill:
            {
                var args = argsElement.Deserialize<CustomAppFillArgs>()!;
                string ctrl = args.Locator?.Label ?? args.Locator?.AccessibleName ?? args.Locator?.StableId ?? "Input";
                targetSummary = $"{args.ApplicationName} [{ctrl}]";
                inputSummary = args.IsSensitive
                    ? SensitiveDataProtector.RedactedPlaceholder
                    : (args.ValueRef is not null ? $"Ref:{args.ValueRef.SourceNodeId}.{args.ValueRef.OutputName}" : (args.Value ?? string.Empty));
                if (args.IsSensitive)
                {
                    risk = RiskLevel.High;
                }
                break;
            }
            case IrConstants.ActionCustomAppReadText:
            {
                var args = argsElement.Deserialize<CustomAppReadTextArgs>()!;
                string ctrl = args.Locator?.Label ?? args.Locator?.AccessibleName ?? args.Locator?.StableId ?? "Control";
                targetSummary = $"{args.ApplicationName} [{ctrl}]";
                inputSummary = $"ReadText {ctrl}";
                break;
            }
        }

        return (sideEffects, risk, targetSummary, inputSummary);
    }

    public static bool IsPathAllowed(string workbookPath, IReadOnlyList<string> allowedDirectories)
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
        sb.Append(definition.Policy.AllowCoordinateClick).Append('|')
          .Append(definition.Policy.AllowExternalAi).Append('|');
        foreach (string dir in definition.Policy.AllowedExcelDirectories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(dir).Append(';');
        }
        foreach (var act in actions)
        {
            sb.Append(act.NodeId).Append(':')
              .Append(act.ActionType).Append(':')
              .Append(act.TargetSummary).Append(':')
              .Append(act.InputSummary).Append(':')
              .Append((int)act.SideEffects).Append(':')
              .Append((int)act.EvaluatedRisk).Append('|');
        }
        sb.Append(JsonSerializer.Serialize(definition.Root));

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }
}
