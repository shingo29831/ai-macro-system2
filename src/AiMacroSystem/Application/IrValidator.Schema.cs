// Module: @role: Workflow IR のJSONスキーマ静的検証および未定義プロパティ検出
using System;
using System.Collections.Generic;
using System.Text.Json;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed partial class IrValidator
{
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
}
