// Module: @role: Workflow IR の8段階静的検証エントリポイントおよび総合オーケストレーション
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed partial class IrValidator
{
    private static readonly Regex _excelRangeRegex = new(@"^[A-Z]+[1-9][0-9]*(:[A-Z]+[1-9][0-9]*)?$", RegexOptions.Compiled);
    private static readonly Regex _singleCellRegex = new(@"^([A-Z]+)([1-9][0-9]*)$", RegexOptions.Compiled);

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
}
