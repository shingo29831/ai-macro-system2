// Module: @role: Excel操作の実行・対象検証・COM読み戻し照合アダプタ
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

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
