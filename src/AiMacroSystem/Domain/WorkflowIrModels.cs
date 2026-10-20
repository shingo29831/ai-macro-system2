// Module: @role: Workflow IR v0.7 の構造化ノード・型付き引数・データ参照・ポリシー宣言モデル
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiMacroSystem.Domain;

public static class IrConstants
{
    public const string CurrentSchemaVersion = "0.7";

    public const string NodeTypeSequence = "Sequence";
    public const string NodeTypeIf = "If";
    public const string NodeTypeForEach = "ForEach";
    public const string NodeTypeWhile = "While";
    public const string NodeTypeAction = "Action";

    public const string ActionChromeNavigate = "Chrome.Navigate";
    public const string ActionChromeClick = "Chrome.Click";
    public const string ActionChromeFill = "Chrome.Fill";
    public const string ActionChromeSubmit = "Chrome.Submit";
    public const string ActionExcelReadRange = "Excel.ReadRange";
    public const string ActionExcelWriteRange = "Excel.WriteRange";

    public const string TypeString = "String";
    public const string TypeNumber = "Number";
    public const string TypeBoolean = "Boolean";
    public const string TypeArrayString = "Array<String>";
    public const string TypeTable2D = "Table2D";
}

public sealed class WorkflowDefinition
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = IrConstants.CurrentSchemaVersion;

    [JsonPropertyName("workflowId")]
    public string WorkflowId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("policy")]
    public PolicyDeclaration Policy { get; set; } = new();

    [JsonPropertyName("root")]
    public WorkflowNode? Root { get; set; }
}

public sealed class PolicyDeclaration
{
    [JsonPropertyName("allowedOrigins")]
    public List<OriginRule> AllowedOrigins { get; set; } = new();

    [JsonPropertyName("allowCoordinateClick")]
    public bool AllowCoordinateClick { get; set; }

    [JsonPropertyName("allowExternalAi")]
    public bool AllowExternalAi { get; set; }

    [JsonPropertyName("allowedExcelDirectories")]
    public List<string> AllowedExcelDirectories { get; set; } = new();
}

public sealed class OriginRule
{
    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = "https";

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 443;

    public bool Matches(Uri uri)
    {
        int uriPort = uri.IsDefaultPort
            ? (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : uri.Port;
        return string.Equals(Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Host, uri.Host, StringComparison.OrdinalIgnoreCase)
            && Port == uriPort;
    }
}

public sealed class DataReference
{
    [JsonPropertyName("sourceNodeId")]
    public string SourceNodeId { get; set; } = string.Empty;

    [JsonPropertyName("outputName")]
    public string OutputName { get; set; } = string.Empty;

    [JsonPropertyName("expectedType")]
    public string ExpectedType { get; set; } = IrConstants.TypeString;

    [JsonPropertyName("isOptional")]
    public bool IsOptional { get; set; }
}

public sealed class ConditionExpression
{
    [JsonPropertyName("leftRef")]
    public DataReference? LeftRef { get; set; }

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "Equals";

    [JsonPropertyName("rightLiteral")]
    public string? RightLiteral { get; set; }
}

public sealed class WorkflowNode
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("actionType")]
    public string? ActionType { get; set; }

    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }

    [JsonPropertyName("children")]
    public List<WorkflowNode>? Children { get; set; }

    [JsonPropertyName("condition")]
    public ConditionExpression? Condition { get; set; }

    [JsonPropertyName("thenChildren")]
    public List<WorkflowNode>? ThenChildren { get; set; }

    [JsonPropertyName("elseChildren")]
    public List<WorkflowNode>? ElseChildren { get; set; }

    [JsonPropertyName("collectionRef")]
    public DataReference? CollectionRef { get; set; }

    [JsonPropertyName("itemVariableName")]
    public string? ItemVariableName { get; set; }

    [JsonPropertyName("bodyChildren")]
    public List<WorkflowNode>? BodyChildren { get; set; }

    [JsonPropertyName("maxIterations")]
    public int? MaxIterations { get; set; }

    [JsonPropertyName("declaredOutputs")]
    public Dictionary<string, string>? DeclaredOutputs { get; set; }

    [JsonPropertyName("sourceCandidateIds")]
    public List<string>? SourceCandidateIds { get; set; }
}

public sealed class SemanticLocator
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("accessibleName")]
    public string? AccessibleName { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("stableId")]
    public string? StableId { get; set; }

    [JsonPropertyName("cssSelector")]
    public string? CssSelector { get; set; }

    [JsonPropertyName("coordinateX")]
    public int? CoordinateX { get; set; }

    [JsonPropertyName("coordinateY")]
    public int? CoordinateY { get; set; }

    public bool HasSemanticTarget() =>
        (!string.IsNullOrWhiteSpace(Role) && !string.IsNullOrWhiteSpace(AccessibleName))
        || !string.IsNullOrWhiteSpace(Label)
        || !string.IsNullOrWhiteSpace(StableId)
        || !string.IsNullOrWhiteSpace(CssSelector);

    public bool UsesCoordinatesOnly() =>
        !HasSemanticTarget() && CoordinateX.HasValue && CoordinateY.HasValue;
}

public sealed class ChromeNavigateArgs
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("expectedFinalUrlPrefix")]
    public string? ExpectedFinalUrlPrefix { get; set; }
}

public sealed class ChromeClickArgs
{
    [JsonPropertyName("locator")]
    public SemanticLocator? Locator { get; set; }

    [JsonPropertyName("expectedStateSelector")]
    public string? ExpectedStateSelector { get; set; }

    [JsonPropertyName("isDestructive")]
    public bool IsDestructive { get; set; }
}

public sealed class ChromeFillArgs
{
    [JsonPropertyName("locator")]
    public SemanticLocator? Locator { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("valueRef")]
    public DataReference? ValueRef { get; set; }

    [JsonPropertyName("isSensitive")]
    public bool IsSensitive { get; set; }
}

public sealed class ChromeSubmitArgs
{
    [JsonPropertyName("locator")]
    public SemanticLocator? Locator { get; set; }

    [JsonPropertyName("expectedSuccessSelector")]
    public string ExpectedSuccessSelector { get; set; } = string.Empty;

    [JsonPropertyName("destinationUrl")]
    public string DestinationUrl { get; set; } = string.Empty;
}

public sealed class ExcelReadRangeArgs
{
    [JsonPropertyName("workbookPath")]
    public string WorkbookPath { get; set; } = string.Empty;

    [JsonPropertyName("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    [JsonPropertyName("rangeAddress")]
    public string RangeAddress { get; set; } = string.Empty;
}

public sealed class ExcelWriteRangeArgs
{
    [JsonPropertyName("workbookPath")]
    public string WorkbookPath { get; set; } = string.Empty;

    [JsonPropertyName("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    [JsonPropertyName("rangeAddress")]
    public string RangeAddress { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<List<string>>? Values { get; set; }

    [JsonPropertyName("valuesRef")]
    public DataReference? ValuesRef { get; set; }

    [JsonPropertyName("allowOverwrite")]
    public bool AllowOverwrite { get; set; }
}
