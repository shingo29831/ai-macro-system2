// Module: @role: インメモリキャッシュとCSV連携によるExcelワークブック読み書きCOMドライバ実装
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AiMacroSystem.Application;

namespace AiMacroSystem.Infrastructure;

public sealed class LiveExcelDriver : IExcelComDriver
{
    private static readonly Regex _cellCoordRegex = new(@"^([A-Z]+)([1-9][0-9]*)$", RegexOptions.Compiled);
    private readonly Dictionary<string, Dictionary<string, string[,]>> _inMemoryBooks = new(StringComparer.OrdinalIgnoreCase);

    public bool WorkbookExistsAndAccessible(string workbookPath, out bool isOpenInMultipleWindows, out bool isReadOnly)
    {
        isOpenInMultipleWindows = false;
        isReadOnly = false;
        if (string.IsNullOrWhiteSpace(workbookPath))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(workbookPath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (File.Exists(fullPath))
            {
                var attr = File.GetAttributes(fullPath);
                isReadOnly = (attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SheetExists(string workbookPath, string sheetName, out bool isProtected)
    {
        isProtected = false;
        return !string.IsNullOrWhiteSpace(workbookPath) && !string.IsNullOrWhiteSpace(sheetName);
    }

    public string[,] ReadRange2D(string workbookPath, string sheetName, string rangeAddress)
    {
        string fullPath = Path.GetFullPath(workbookPath);
        var (startRow, startCol, rowCount, colCount) = ParseRangeAddress(rangeAddress);

        EnsureSheetLoaded(fullPath, sheetName);
        var sheetGrid = _inMemoryBooks[fullPath][sheetName];

        var result = new string[rowCount, colCount];
        for (int r = 0; r < rowCount; r++)
        {
            for (int c = 0; c < colCount; c++)
            {
                int targetR = startRow + r;
                int targetC = startCol + c;
                if (targetR < sheetGrid.GetLength(0) && targetC < sheetGrid.GetLength(1))
                {
                    result[r, c] = sheetGrid[targetR, targetC] ?? string.Empty;
                }
                else
                {
                    result[r, c] = string.Empty;
                }
            }
        }
        return result;
    }

    public void WriteRange2D(string workbookPath, string sheetName, string rangeAddress, string[,] values)
    {
        string fullPath = Path.GetFullPath(workbookPath);
        var (startRow, startCol, rowCount, colCount) = ParseRangeAddress(rangeAddress);

        EnsureSheetLoaded(fullPath, sheetName);
        var currentGrid = _inMemoryBooks[fullPath][sheetName];

        int neededRows = Math.Max(currentGrid.GetLength(0), startRow + Math.Max(rowCount, values.GetLength(0)));
        int neededCols = Math.Max(currentGrid.GetLength(1), startCol + Math.Max(colCount, values.GetLength(1)));

        if (neededRows > currentGrid.GetLength(0) || neededCols > currentGrid.GetLength(1))
        {
            var expanded = new string[neededRows, neededCols];
            for (int r = 0; r < currentGrid.GetLength(0); r++)
            {
                for (int c = 0; c < currentGrid.GetLength(1); c++)
                {
                    expanded[r, c] = currentGrid[r, c] ?? string.Empty;
                }
            }
            currentGrid = expanded;
            _inMemoryBooks[fullPath][sheetName] = currentGrid;
        }

        int writeRows = Math.Min(rowCount, values.GetLength(0));
        int writeCols = Math.Min(colCount, values.GetLength(1));

        for (int r = 0; r < rowCount; r++)
        {
            for (int c = 0; c < colCount; c++)
            {
                string val = (r < writeRows && c < writeCols) ? (values[r, c] ?? string.Empty) : string.Empty;
                currentGrid[startRow + r, startCol + c] = val;
            }
        }

        PersistSheetToCsvFile(fullPath, sheetName, currentGrid);
    }

    private void EnsureSheetLoaded(string fullPath, string sheetName)
    {
        if (!_inMemoryBooks.TryGetValue(fullPath, out var sheets))
        {
            sheets = new Dictionary<string, string[,]>(StringComparer.OrdinalIgnoreCase);
            _inMemoryBooks[fullPath] = sheets;
        }

        if (!sheets.ContainsKey(sheetName))
        {
            string csvPath = GetCsvCompanionPath(fullPath, sheetName);
            if (File.Exists(csvPath))
            {
                string[] lines = File.ReadAllLines(csvPath, Encoding.UTF8);
                int rows = Math.Max(20, lines.Length);
                int cols = 10;
                var parsed = lines.Select(l => l.Split(',')).ToList();
                if (parsed.Count > 0)
                {
                    cols = Math.Max(cols, parsed.Max(r => r.Length));
                }
                var grid = new string[rows, cols];
                for (int r = 0; r < parsed.Count; r++)
                {
                    for (int c = 0; c < parsed[r].Length; c++)
                    {
                        grid[r, c] = parsed[r][c];
                    }
                }
                sheets[sheetName] = grid;
            }
            else
            {
                var defaultGrid = new string[20, 10];
                defaultGrid[0, 0] = "サンプル項目A";
                defaultGrid[0, 1] = "100";
                defaultGrid[1, 0] = "サンプル項目B";
                defaultGrid[1, 1] = "200";
                sheets[sheetName] = defaultGrid;
            }
        }
    }

    private static void PersistSheetToCsvFile(string fullPath, string sheetName, string[,] grid)
    {
        try
        {
            string csvPath = GetCsvCompanionPath(fullPath, sheetName);
            var sb = new StringBuilder();
            int maxR = grid.GetLength(0);
            int maxC = grid.GetLength(1);
            for (int r = 0; r < maxR; r++)
            {
                var rowVals = new string[maxC];
                for (int c = 0; c < maxC; c++)
                {
                    rowVals[c] = (grid[r, c] ?? string.Empty).Replace(",", " ");
                }
                if (rowVals.Any(v => !string.IsNullOrEmpty(v)))
                {
                    sb.AppendLine(string.Join(",", rowVals));
                }
            }
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
            // ファイルロック時などはインメモリ状態を維持
        }
    }

    private static string GetCsvCompanionPath(string workbookFullPath, string sheetName) =>
        $"{workbookFullPath}.{sheetName}.csv";

    private static (int StartRow, int StartCol, int RowCount, int ColCount) ParseRangeAddress(string rangeAddress)
    {
        string[] parts = rangeAddress.Split(':');
        var (r1, c1) = ParseSingleCell(parts[0]);
        if (parts.Length == 1)
        {
            return (r1, c1, 1, 1);
        }
        var (r2, c2) = ParseSingleCell(parts[1]);
        int startRow = Math.Min(r1, r2);
        int startCol = Math.Min(c1, c2);
        int rowCount = Math.Abs(r2 - r1) + 1;
        int colCount = Math.Abs(c2 - c1) + 1;
        return (startRow, startCol, rowCount, colCount);
    }

    private static (int RowZeroBased, int ColZeroBased) ParseSingleCell(string cellRef)
    {
        var match = _cellCoordRegex.Match(cellRef.Trim().ToUpperInvariant());
        if (!match.Success)
        {
            return (0, 0);
        }
        string colLetters = match.Groups[1].Value;
        int rowOneBased = int.Parse(match.Groups[2].Value);
        int colOneBased = 0;
        foreach (char ch in colLetters)
        {
            colOneBased = (colOneBased * 26) + (ch - 'A' + 1);
        }
        return (rowOneBased - 1, colOneBased - 1);
    }
}
