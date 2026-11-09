// Module: @role: 制御フロー構造（Sequence/If/ForEach/While）およびデータ参照スコープ解決・分岐マージ検証
using System;
using System.Collections.Generic;
using System.Linq;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed partial class IrValidator
{
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
