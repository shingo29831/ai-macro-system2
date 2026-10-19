namespace AI.MacroSystem.Domain.Workflow.Actions;

using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Expressions;

public sealed class DelayParameters : ActionParameters
{
    [JsonPropertyName("milliseconds")]
    public required int Milliseconds { get; init; }
}

public sealed class LogParameters : ActionParameters
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("level")]
    public string Level { get; init; } = "Info";
}

public sealed class WebNavigateParameters : ActionParameters
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

public sealed class ExcelReadCellParameters : ActionParameters
{
    [JsonPropertyName("workbookPath")]
    public required string WorkbookPath { get; init; }

    [JsonPropertyName("sheetName")]
    public required string SheetName { get; init; }

    [JsonPropertyName("cellAddress")]
    public required string CellAddress { get; init; }

    [JsonPropertyName("outputVariable")]
    public required string OutputVariable { get; init; }
}

public sealed class ExcelWriteCellParameters : ActionParameters
{
    [JsonPropertyName("workbookPath")]
    public required string WorkbookPath { get; init; }

    [JsonPropertyName("sheetName")]
    public required string SheetName { get; init; }

    [JsonPropertyName("cellAddress")]
    public required string CellAddress { get; init; }

    [JsonPropertyName("value")]
    public required Expression Value { get; init; }
}
