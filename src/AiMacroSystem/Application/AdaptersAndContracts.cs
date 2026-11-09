// Module: @role: アクション実行結果型・各種ドライバインターフェース・共通アダプタ契約
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed class AdapterExecutionResult
{
    public required ActionExecutionStatus Status { get; init; }
    public required FailureClassification Classification { get; init; }
    public required string Message { get; init; }
    public required bool SideEffectCommitted { get; init; }
    public IReadOnlyDictionary<string, object?> Outputs { get; init; } = new Dictionary<string, object?>();

    public static AdapterExecutionResult Succeeded(string message, IReadOnlyDictionary<string, object?> outputs, bool sideEffectCommitted = true) =>
        new()
        {
            Status = ActionExecutionStatus.Succeeded,
            Classification = FailureClassification.None,
            Message = message,
            SideEffectCommitted = sideEffectCommitted,
            Outputs = outputs
        };

    public static AdapterExecutionResult Failed(FailureClassification classification, string message, bool sideEffectCommitted = false) =>
        new()
        {
            Status = ActionExecutionStatus.Failed,
            Classification = classification,
            Message = message,
            SideEffectCommitted = sideEffectCommitted
        };

    public static AdapterExecutionResult Unknown(string message) =>
        new()
        {
            Status = ActionExecutionStatus.Unknown,
            Classification = FailureClassification.ExternalOutcomeUnknown,
            Message = message,
            SideEffectCommitted = true
        };
}

public interface IBrowserDriver
{
    Uri? CurrentUrl { get; }
    Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(Uri targetUrl, CancellationToken ct);
    Task<int> CountMatchingElementsAsync(SemanticLocator locator, CancellationToken ct);
    Task ClickElementAsync(SemanticLocator locator, CancellationToken ct);
    Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct);
    Task<string?> ReadElementValueAsync(SemanticLocator locator, CancellationToken ct);
    Task<bool> ElementExistsAsync(string selector, CancellationToken ct);
}

public interface IExcelComDriver
{
    bool WorkbookExistsAndAccessible(string workbookPath, out bool isOpenInMultipleWindows, out bool isReadOnly);
    bool SheetExists(string workbookPath, string sheetName, out bool isProtected);
    string[,] ReadRange2D(string workbookPath, string sheetName, string rangeAddress);
    void WriteRange2D(string workbookPath, string sheetName, string rangeAddress, string[,] values);
}

public interface ICustomAppDriver
{
    Task<bool> WindowExistsAsync(string applicationName, CancellationToken ct);
    Task<int> CountMatchingControlsAsync(string applicationName, SemanticLocator locator, CancellationToken ct);
    Task ClickControlAsync(string applicationName, SemanticLocator locator, CancellationToken ct);
    Task FillControlAsync(string applicationName, SemanticLocator locator, string value, CancellationToken ct);
    Task<string?> ReadControlTextAsync(string applicationName, SemanticLocator locator, CancellationToken ct);
}

public interface IActionAdapter
{
    Task<AdapterExecutionResult> ExecuteAsync(
        WorkflowNode node,
        PolicyDeclaration policy,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        CancellationToken ct);

    Task<ExternalStateProbeResult> ProbeExternalStateAsync(WorkflowNode node, CancellationToken ct);
}
