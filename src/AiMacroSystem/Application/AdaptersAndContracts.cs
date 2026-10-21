// Module: @role: ChromeおよびExcelアダプターの安全性検証・許可リスト強制・読み戻し照合契約
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

public interface IActionAdapter
{
    Task<AdapterExecutionResult> ExecuteAsync(
        WorkflowNode node,
        PolicyDeclaration policy,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        CancellationToken ct);

    Task<ExternalStateProbeResult> ProbeExternalStateAsync(WorkflowNode node, CancellationToken ct);
}

public sealed class ChromeAdapter : IActionAdapter
{
    private readonly IBrowserDriver _driver;

    public ChromeAdapter(IBrowserDriver driver)
    {
        _driver = driver;
    }

    public async Task<AdapterExecutionResult> ExecuteAsync(
        WorkflowNode node,
        PolicyDeclaration policy,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        CancellationToken ct)
    {
        JsonElement argsElement = node.Args!.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var args = argsElement.Deserialize<ChromeNavigateArgs>()!;
                if (!Uri.TryCreate(args.Url, UriKind.Absolute, out Uri? initialUri))
                {
                    return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, "Invalid URL format.");
                }
                if (!IsOriginAllowed(initialUri, policy.AllowedOrigins))
                {
                    return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Initial navigation URL '{initialUri}' is blocked by allowlist.");
                }

                try
                {
                    var navOutcome = await _driver.NavigateAsync(initialUri, ct);

                    // リダイレクト経路・最終URL・ポップアップすべてに許可リストを強制適用（仕様9.2）
                    foreach (Uri redirectUri in navOutcome.RedirectChain)
                    {
                        if (!IsOriginAllowed(redirectUri, policy.AllowedOrigins))
                        {
                            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Redirect to '{redirectUri}' blocked by allowlist.", sideEffectCommitted: true);
                        }
                    }
                    if (!IsOriginAllowed(navOutcome.FinalUrl, policy.AllowedOrigins))
                    {
                        return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Final navigation URL '{navOutcome.FinalUrl}' blocked by allowlist.", sideEffectCommitted: true);
                    }
                    foreach (Uri popupUri in navOutcome.OpenedPopups)
                    {
                        if (!IsOriginAllowed(popupUri, policy.AllowedOrigins))
                        {
                            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Popup/new tab URL '{popupUri}' blocked by allowlist.", sideEffectCommitted: true);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(args.ExpectedFinalUrlPrefix) &&
                        !navOutcome.FinalUrl.ToString().StartsWith(args.ExpectedFinalUrlPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return AdapterExecutionResult.Failed(
                            FailureClassification.PostconditionMismatch,
                            $"Final URL '{navOutcome.FinalUrl}' did not match expected prefix '{args.ExpectedFinalUrlPrefix}'.",
                            sideEffectCommitted: true);
                    }

                    return AdapterExecutionResult.Succeeded(
                        $"Navigated to {navOutcome.FinalUrl}",
                        new Dictionary<string, object?> { ["finalUrl"] = navOutcome.FinalUrl.ToString() },
                        sideEffectCommitted: false);
                }
                catch (TimeoutException ex)
                {
                    return AdapterExecutionResult.Failed(FailureClassification.TransientError, $"Navigation timed out: {ex.Message}", sideEffectCommitted: false);
                }
            }

            case IrConstants.ActionChromeClick:
            {
                var args = argsElement.Deserialize<ChromeClickArgs>()!;
                var currentUrlcheck = VerifyCurrentBrowserOrigin(policy);
                if (currentUrlcheck is not null)
                {
                    return currentUrlcheck;
                }

                var targetCheck = await EnsureUniqueTargetAsync(args.Locator!, policy, ct);
                if (targetCheck is not null)
                {
                    return targetCheck;
                }

                try
                {
                    await _driver.ClickElementAsync(args.Locator!, ct);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"Click side-effect outcome is uncertain due to runtime error: {ex.Message}");
                }

                var postNavCheck = VerifyCurrentBrowserOrigin(policy);
                if (postNavCheck is not null)
                {
                    return postNavCheck;
                }

                if (!string.IsNullOrWhiteSpace(args.ExpectedStateSelector))
                {
                    bool exists = await _driver.ElementExistsAsync(args.ExpectedStateSelector, ct);
                    if (!exists)
                    {
                        return AdapterExecutionResult.Failed(
                            FailureClassification.PostconditionMismatch,
                            $"Expected post-click element '{args.ExpectedStateSelector}' was not found.",
                            sideEffectCommitted: true);
                    }
                }

                return AdapterExecutionResult.Succeeded(
                    "Click completed and verified.",
                    new Dictionary<string, object?> { ["clicked"] = true });
            }

            case IrConstants.ActionChromeFill:
            {
                var args = argsElement.Deserialize<ChromeFillArgs>()!;
                var currentUrlCheck = VerifyCurrentBrowserOrigin(policy);
                if (currentUrlCheck is not null)
                {
                    return currentUrlCheck;
                }

                var targetCheck = await EnsureUniqueTargetAsync(args.Locator!, policy, ct);
                if (targetCheck is not null)
                {
                    return targetCheck;
                }

                string fillValue = ResolveStringArgument(args.Value, args.ValueRef, runtimeOutputs);
                try
                {
                    await _driver.FillElementAsync(args.Locator!, fillValue, ct);
                }
                catch (TimeoutException ex)
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.TransientError,
                        $"Chrome.Fill timed out before completion: {ex.Message}",
                        sideEffectCommitted: false);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"Chrome.Fill outcome is uncertain due to runtime error: {ex.Message}");
                }

                // 入力後の読み戻し検証（仕様9.4）
                string? readback;
                try
                {
                    readback = await _driver.ReadElementValueAsync(args.Locator!, ct);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"Chrome.Fill completed but readback failed: {ex.Message}");
                }

                if (!string.Equals(readback, fillValue, StringComparison.Ordinal))
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.PostconditionMismatch,
                        "Readback verification failed after Chrome.Fill.",
                        sideEffectCommitted: true);
                }

                string outputDisplay = args.IsSensitive ? "[REDACTED]" : fillValue;
                return AdapterExecutionResult.Succeeded(
                    "Input filled and verified via readback.",
                    new Dictionary<string, object?> { ["filledValue"] = outputDisplay });
            }

            case IrConstants.ActionChromeSubmit:
            {
                var args = argsElement.Deserialize<ChromeSubmitArgs>()!;
                var currentUrlCheck = VerifyCurrentBrowserOrigin(policy);
                if (currentUrlCheck is not null)
                {
                    return currentUrlCheck;
                }

                if (!string.IsNullOrWhiteSpace(args.DestinationUrl))
                {
                    if (!Uri.TryCreate(args.DestinationUrl, UriKind.Absolute, out Uri? destUri) ||
                        !IsOriginAllowed(destUri, policy.AllowedOrigins))
                    {
                        return AdapterExecutionResult.Failed(
                            FailureClassification.PolicyViolation,
                            $"Submit destination URL '{args.DestinationUrl}' is blocked by allowlist.");
                    }
                }

                var targetCheck = await EnsureUniqueTargetAsync(args.Locator!, policy, ct);
                if (targetCheck is not null)
                {
                    return targetCheck;
                }

                try
                {
                    await _driver.ClickElementAsync(args.Locator!, ct);
                    bool successConfirmed = await _driver.ElementExistsAsync(args.ExpectedSuccessSelector, ct);
                    if (!successConfirmed)
                    {
                        return AdapterExecutionResult.Unknown(
                            $"Form submitted, but expected postcondition selector '{args.ExpectedSuccessSelector}' could not be confirmed.");
                    }

                    var postSubmitOriginCheck = VerifyCurrentBrowserOrigin(policy);
                    if (postSubmitOriginCheck is not null)
                    {
                        return postSubmitOriginCheck;
                    }

                    return AdapterExecutionResult.Succeeded(
                        "Form submission verified.",
                        new Dictionary<string, object?> { ["submitted"] = true });
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"External form submission outcome is unknown: {ex.Message}");
                }
            }

            default:
                return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, $"Unsupported Chrome action '{node.ActionType}'.");
        }
    }

    public async Task<ExternalStateProbeResult> ProbeExternalStateAsync(WorkflowNode node, CancellationToken ct)
    {
        if (node.ActionType == IrConstants.ActionChromeSubmit && node.Args.HasValue)
        {
            var args = node.Args.Value.Deserialize<ChromeSubmitArgs>();
            if (args is not null && !string.IsNullOrWhiteSpace(args.ExpectedSuccessSelector))
            {
                try
                {
                    bool exists = await _driver.ElementExistsAsync(args.ExpectedSuccessSelector, ct);
                    if (exists)
                    {
                        return ExternalStateProbeResult.ConfirmedCompleted;
                    }
                }
                catch
                {
                    return ExternalStateProbeResult.OutcomeUnknown;
                }
            }
        }
        return ExternalStateProbeResult.OutcomeUnknown;
    }

    private AdapterExecutionResult? VerifyCurrentBrowserOrigin(PolicyDeclaration policy)
    {
        if (_driver.CurrentUrl is not null && !IsOriginAllowed(_driver.CurrentUrl, policy.AllowedOrigins))
        {
            return AdapterExecutionResult.Failed(
                FailureClassification.PolicyViolation,
                $"Current browser URL '{_driver.CurrentUrl}' is outside allowed origins.",
                sideEffectCommitted: true);
        }
        return null;
    }

    private async Task<AdapterExecutionResult?> EnsureUniqueTargetAsync(SemanticLocator locator, PolicyDeclaration policy, CancellationToken ct)
    {
        if (locator.UsesCoordinatesOnly() && !policy.AllowCoordinateClick)
        {
            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, "Coordinate operation is not permitted by policy.");
        }

        int count = await _driver.CountMatchingElementsAsync(locator, ct);
        if (count == 0)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetNotFound, "Target element does not exist (0 matches).");
        }
        if (count > 1)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetAmbiguous, $"Target locator is ambiguous ({count} matches found). Unconditional substitution is prohibited.");
        }
        return null;
    }

    private static bool IsOriginAllowed(Uri uri, IReadOnlyList<OriginRule> allowedOrigins) =>
        allowedOrigins.Any(rule => rule.Matches(uri));

    private static string ResolveStringArgument(
        string? literalValue,
        DataReference? reference,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        if (reference is not null)
        {
            if (runtimeOutputs.TryGetValue(reference.SourceNodeId, out var outputs) &&
                outputs.TryGetValue(reference.OutputName, out object? val) &&
                val is not null)
            {
                return val.ToString() ?? string.Empty;
            }
            if (reference.IsOptional)
            {
                return literalValue ?? string.Empty;
            }
            throw new InvalidOperationException($"Runtime reference '{reference.SourceNodeId}.{reference.OutputName}' could not be resolved.");
        }
        return literalValue ?? string.Empty;
    }
}

public sealed class ExcelAdapter : IActionAdapter
{
    private readonly IExcelComDriver _driver;

    public ExcelAdapter(IExcelComDriver driver)
    {
        _driver = driver;
    }

    public Task<AdapterExecutionResult> ExecuteAsync(
        WorkflowNode node,
        PolicyDeclaration policy,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        CancellationToken ct)
    {
        JsonElement argsElement = node.Args!.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionExcelReadRange:
            {
                var args = argsElement.Deserialize<ExcelReadRangeArgs>()!;
                var preCheck = ValidateExcelTargetState(args.WorkbookPath, args.SheetName, args.RangeAddress, policy, requireWritable: false);
                if (preCheck is not null)
                {
                    return Task.FromResult(preCheck);
                }

                try
                {
                    string[,] matrix = _driver.ReadRange2D(args.WorkbookPath, args.SheetName, args.RangeAddress);
                    var flatList = new List<string>();
                    var table = new List<List<string>>();

                    int rows = matrix.GetLength(0);
                    int cols = matrix.GetLength(1);
                    for (int r = 0; r < rows; r++)
                    {
                        var rowList = new List<string>();
                        for (int c = 0; c < cols; c++)
                        {
                            string cell = matrix[r, c] ?? string.Empty;
                            flatList.Add(cell);
                            rowList.Add(cell);
                        }
                        table.Add(rowList);
                    }

                    return Task.FromResult(AdapterExecutionResult.Succeeded(
                        $"Read {flatList.Count} cells from {args.SheetName}!{args.RangeAddress}.",
                        new Dictionary<string, object?>
                        {
                            ["values"] = flatList,
                            ["table"] = table
                        },
                        sideEffectCommitted: false));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(AdapterExecutionResult.Failed(
                        FailureClassification.TransientError,
                        $"Excel read failed: {ex.Message}",
                        sideEffectCommitted: false));
                }
            }

            case IrConstants.ActionExcelWriteRange:
            {
                var args = argsElement.Deserialize<ExcelWriteRangeArgs>()!;
                var preCheck = ValidateExcelTargetState(args.WorkbookPath, args.SheetName, args.RangeAddress, policy, requireWritable: true);
                if (preCheck is not null)
                {
                    return Task.FromResult(preCheck);
                }

                string[,] expectedMatrix = ResolveWriteMatrix(args, runtimeOutputs);
                if (IrValidator.TryGetRangeDimensions(args.RangeAddress, out int rangeRows, out int rangeCols))
                {
                    if (expectedMatrix.GetLength(0) != rangeRows || expectedMatrix.GetLength(1) != rangeCols)
                    {
                        return Task.FromResult(AdapterExecutionResult.Failed(
                            FailureClassification.ValidationFailure,
                            $"Excel write matrix dimensions ({expectedMatrix.GetLength(0)}x{expectedMatrix.GetLength(1)}) do not match target range '{args.RangeAddress}' ({rangeRows}x{rangeCols}).",
                            sideEffectCommitted: false));
                    }
                }

                try
                {
                    // Value2相当の2次元配列一括書き込み（仕様10.2）
                    _driver.WriteRange2D(args.WorkbookPath, args.SheetName, args.RangeAddress, expectedMatrix);
                }
                catch (Exception ex)
                {
                    // COM例外時は成否不明(Unknown)として扱い自動再試行を禁止（仕様10.3 / 18.5）
                    return Task.FromResult(AdapterExecutionResult.Unknown($"Excel COM exception during write: {ex.Message}"));
                }

                try
                {
                    // 書き込み後の読み戻し検証（仕様10.2 Step 6）
                    string[,] readback = _driver.ReadRange2D(args.WorkbookPath, args.SheetName, args.RangeAddress);
                    if (!MatricesEqual(expectedMatrix, readback))
                    {
                        return Task.FromResult(AdapterExecutionResult.Failed(
                            FailureClassification.PostconditionMismatch,
                            $"Excel readback mismatch on '{args.WorkbookPath}!{args.SheetName}!{args.RangeAddress}'.",
                            sideEffectCommitted: true));
                    }

                    int totalCells = expectedMatrix.GetLength(0) * expectedMatrix.GetLength(1);
                    return Task.FromResult(AdapterExecutionResult.Succeeded(
                        $"Wrote and verified {totalCells} cells in {args.SheetName}!{args.RangeAddress}.",
                        new Dictionary<string, object?> { ["writtenCells"] = totalCells }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(AdapterExecutionResult.Unknown($"Excel write completed but readback failed with COM error: {ex.Message}"));
                }
            }

            default:
                return Task.FromResult(AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, $"Unsupported Excel action '{node.ActionType}'."));
        }
    }

    public Task<ExternalStateProbeResult> ProbeExternalStateAsync(WorkflowNode node, CancellationToken ct)
    {
        if (node.ActionType == IrConstants.ActionExcelWriteRange && node.Args.HasValue)
        {
            var args = node.Args.Value.Deserialize<ExcelWriteRangeArgs>();
            if (args?.Values is { Count: > 0 })
            {
                try
                {
                    string[,] expected = To2DArray(args.Values);
                    string[,] actual = _driver.ReadRange2D(args.WorkbookPath, args.SheetName, args.RangeAddress);
                    if (MatricesEqual(expected, actual))
                    {
                        return Task.FromResult(ExternalStateProbeResult.ConfirmedCompleted);
                    }
                }
                catch
                {
                    return Task.FromResult(ExternalStateProbeResult.OutcomeUnknown);
                }
            }
        }
        return Task.FromResult(ExternalStateProbeResult.OutcomeUnknown);
    }

    private AdapterExecutionResult? ValidateExcelTargetState(
        string workbookPath,
        string sheetName,
        string rangeAddress,
        PolicyDeclaration policy,
        bool requireWritable)
    {
        if (string.IsNullOrWhiteSpace(workbookPath) || workbookPath.Equals("ActiveWorkbook", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, "Explicit workbookPath is required; ActiveWorkbook is prohibited.");
        }
        if (string.IsNullOrWhiteSpace(sheetName) || sheetName.Equals("ActiveSheet", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, "Explicit sheetName is required; ActiveSheet is prohibited.");
        }
        if (!IrValidator.TryGetRangeDimensions(rangeAddress, out _, out _))
        {
            return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, $"Invalid A1-style Excel rangeAddress '{rangeAddress}'.");
        }
        if (!PolicyEngine.IsPathAllowed(workbookPath, policy.AllowedExcelDirectories))
        {
            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Excel workbook path '{workbookPath}' is outside allowedExcelDirectories.");
        }
        if (!_driver.WorkbookExistsAndAccessible(workbookPath, out bool multipleWindows, out bool isReadOnly))
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetNotFound, $"Excel workbook '{workbookPath}' cannot be uniquely identified or accessed.");
        }
        if (multipleWindows)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetAmbiguous, $"Workbook '{workbookPath}' is open in multiple windows; operation aborted.");
        }
        if (requireWritable && isReadOnly)
        {
            return AdapterExecutionResult.Failed(FailureClassification.PreconditionFailed, $"Workbook '{workbookPath}' is read-only.");
        }
        if (!_driver.SheetExists(workbookPath, sheetName, out bool isProtected))
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetNotFound, $"Worksheet '{sheetName}' does not exist in '{workbookPath}'.");
        }
        if (requireWritable && isProtected)
        {
            return AdapterExecutionResult.Failed(FailureClassification.PreconditionFailed, $"Worksheet '{sheetName}' is protected.");
        }
        return null;
    }

    private static string[,] ResolveWriteMatrix(
        ExcelWriteRangeArgs args,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        if (args.ValuesRef is not null &&
            runtimeOutputs.TryGetValue(args.ValuesRef.SourceNodeId, out var outputs) &&
            outputs.TryGetValue(args.ValuesRef.OutputName, out object? rawVal))
        {
            if (rawVal is List<List<string>> table)
            {
                return To2DArray(table);
            }
            if (rawVal is IEnumerable<string> list)
            {
                var arr = list.ToList();
                var matrix = new string[arr.Count, 1];
                for (int i = 0; i < arr.Count; i++)
                {
                    matrix[i, 0] = arr[i];
                }
                return matrix;
            }
        }

        if (args.Values is { Count: > 0 })
        {
            return To2DArray(args.Values);
        }

        throw new InvalidOperationException("Excel.WriteRange values could not be resolved.");
    }

    private static string[,] To2DArray(List<List<string>> rows)
    {
        int rowCount = rows.Count;
        int colCount = rows.Max(r => r.Count);
        var matrix = new string[rowCount, colCount];
        for (int r = 0; r < rowCount; r++)
        {
            for (int c = 0; c < colCount; c++)
            {
                matrix[r, c] = c < rows[r].Count ? rows[r][c] : string.Empty;
            }
        }
        return matrix;
    }

    private static bool MatricesEqual(string[,] a, string[,] b)
    {
        if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1))
        {
            return false;
        }
        for (int r = 0; r < a.GetLength(0); r++)
        {
            for (int c = 0; c < a.GetLength(1); c++)
            {
                if (!string.Equals(a[r, c] ?? string.Empty, b[r, c] ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
