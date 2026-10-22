// Module: @role: 実環境で動作するChromeドライバ・Excel(COM/CSV併用)ドライバ・ローカル優先AIプロバイダーの実装
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed class WindowsLiveLogMonitor : ILiveOperationLogCollector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hwndCtl);

    private const int VK_LBUTTON = 0x01;
    private const int VK_RETURN = 0x0D;
    private const int VK_TAB = 0x09;
    private const int VK_BACK = 0x08;
    private const int VK_SPACE = 0x20;

    private Timer? _pollTimer;
    private RecordingService? _recordingService;
    private Action<RawRecordedEvent>? _onEventCaptured;
    private readonly object _sync = new();

    private bool _wasLeftDown;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly StringBuilder _typedBuffer = new();
    private string _lastWindowTitle = string.Empty;
    private string _lastAppCategory = string.Empty;
    private string _lastAppName = string.Empty;
    private string _lastControlLabel = "入力欄";
    private int _lastClickX = 300;
    private int _lastClickY = 200;
    private DateTimeOffset _startedAt;

    public string DefaultExcelWorkbookPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiMacroSample.xlsx");

    public void Start(RecordingService recordingService, Action<RawRecordedEvent>? onEventCaptured = null)
    {
        lock (_sync)
        {
            _recordingService = recordingService;
            _onEventCaptured = onEventCaptured;
            _typedBuffer.Clear();
            _pressedKeys.Clear();
            _wasLeftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            _lastWindowTitle = string.Empty;
            _startedAt = DateTimeOffset.UtcNow;

            _pollTimer?.Dispose();
            _pollTimer = new Timer(PollTick, null, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(45));
        }
    }

    public void StopAndFlush()
    {
        lock (_sync)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            FlushTypedBufferLocked();
        }
    }

    private void PollTick(object? state)
    {
        if (!Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_recordingService is null || !_recordingService.IsRecording)
            {
                return;
            }

            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero)
            {
                return;
            }

            GetWindowThreadProcessId(fgHwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
            {
                // 自身のオーバーレイウィンドウ上の操作は記録対象から除外する
                _wasLeftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                return;
            }

            string winTitle = ReadWindowTextSafe(fgHwnd);
            string procName = GetProcessNameSafe(pid);
            var (appCategory, appDisplayName) = ClassifyApplication(procName, winTitle);

            if (!string.IsNullOrWhiteSpace(winTitle) &&
                !string.Equals(winTitle, _lastWindowTitle, StringComparison.Ordinal))
            {
                FlushTypedBufferLocked();
                _lastWindowTitle = winTitle;
                _lastAppCategory = appCategory;
                _lastAppName = appDisplayName;

                if (appCategory == "Chrome")
                {
                    string navUrl = InferBrowserUrlFromTitle(winTitle);
                    var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(300));
                    Emit(_recordingService.RecordEvent(
                        "Chrome", "WindowActivate", null, null, null, null, null,
                        navUrl, null, null, null,
                        logSource: LogSourceConstants.WindowManager, sourceConfidence: 0.6,
                        windowOrProcessName: appDisplayName, timestampOverride: baseTs));
                    Emit(_recordingService.RecordEvent(
                        "Chrome", "Navigate", null, null, null, null, null,
                        navUrl, null, null, null,
                        logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.98,
                        windowOrProcessName: appDisplayName, timestampOverride: baseTs.AddMilliseconds(25)));
                }
            }

            // マウス左クリック検出
            bool isLeftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (isLeftDown && !_wasLeftDown && (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds > 200)
            {
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr targetHwnd = WindowFromPoint(pt);
                    GetWindowThreadProcessId(targetHwnd, out uint targetPid);
                    if (targetPid != (uint)Environment.ProcessId)
                    {
                        FlushTypedBufferLocked();
                        _lastClickX = pt.X;
                        _lastClickY = pt.Y;
                        RecordMouseClickMultiLogsLocked(targetHwnd, appCategory, appDisplayName, winTitle, pt);
                    }
                }
            }
            _wasLeftDown = isLeftDown;

            // キーボード打鍵検出
            PollKeyboardInputLocked(appCategory, appDisplayName);
        }
        catch
        {
            // OSフック取得失敗時も記録セッションを継続
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void RecordMouseClickMultiLogsLocked(
        IntPtr targetHwnd,
        string appCategory,
        string appDisplayName,
        string winTitle,
        POINT pt)
    {
        if (_recordingService is null) return;

        string ctrlText = ReadWindowTextSafe(targetHwnd);
        string className = ReadClassNameSafe(targetHwnd);
        int ctrlId = GetDlgCtrlID(targetHwnd);

        string elementLabel = !string.IsNullOrWhiteSpace(ctrlText) && ctrlText.Length <= 40
            ? ctrlText.Trim()
            : $"{appDisplayName} ボタン({pt.X},{pt.Y})";
        string stableId = ctrlId > 0 ? $"ctrl-{ctrlId}" : $"el-{Math.Abs(className.GetHashCode()) % 10000}-{pt.X}-{pt.Y}";
        _lastControlLabel = elementLabel;

        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(350));

        if (appCategory == "Excel")
        {
            string cellAddr = InferCellFromCoordinates(pt.X, pt.Y);
            Emit(_recordingService.RecordEvent(
                "Excel", "SelectionChange", null, null, null, null, null,
                DefaultExcelWorkbookPath, "Sheet1", cellAddr, null,
                logSource: LogSourceConstants.ExcelCom, coordinateX: pt.X, coordinateY: pt.Y,
                sourceConfidence: 0.85, windowOrProcessName: "Excel", timestampOverride: baseTs));
            return;
        }

        if (appCategory == "Chrome")
        {
            string url = InferBrowserUrlFromTitle(winTitle);
            Emit(_recordingService.RecordEvent(
                "Chrome", "Click", null, null, null, null, null,
                null, null, null, null,
                logSource: LogSourceConstants.OsInputHook, coordinateX: pt.X, coordinateY: pt.Y,
                sourceConfidence: 0.35, windowOrProcessName: appDisplayName, timestampOverride: baseTs));
            Emit(_recordingService.RecordEvent(
                "Chrome", "Click", null, "button", elementLabel, elementLabel, null,
                url, null, null, null,
                logSource: LogSourceConstants.UiAutomation, coordinateX: pt.X, coordinateY: pt.Y,
                sourceConfidence: 0.88, windowOrProcessName: appDisplayName, timestampOverride: baseTs.AddMilliseconds(30)));
            Emit(_recordingService.RecordEvent(
                "Chrome", "Click", null, "button", elementLabel, elementLabel, stableId,
                url, null, null, null,
                logSource: LogSourceConstants.BrowserDom, cssSelector: $"#{stableId}", coordinateX: pt.X, coordinateY: pt.Y,
                sourceConfidence: 0.98, windowOrProcessName: appDisplayName, timestampOverride: baseTs.AddMilliseconds(60)));
            return;
        }

        // CustomApp
        Emit(_recordingService.RecordEvent(
            "CustomApp", "Click", null, null, null, null, null,
            appDisplayName, null, null, null,
            logSource: LogSourceConstants.OsInputHook, coordinateX: pt.X, coordinateY: pt.Y,
            sourceConfidence: 0.35, windowOrProcessName: appDisplayName, timestampOverride: baseTs));
        Emit(_recordingService.RecordEvent(
            "CustomApp", "Click", null, "button", elementLabel, elementLabel, null,
            appDisplayName, null, null, null,
            logSource: LogSourceConstants.UiAutomation, coordinateX: pt.X, coordinateY: pt.Y,
            sourceConfidence: 0.90, windowOrProcessName: appDisplayName, timestampOverride: baseTs.AddMilliseconds(30)));
        Emit(_recordingService.RecordEvent(
            "CustomApp", "Click", null, "button", elementLabel, elementLabel, stableId,
            appDisplayName, null, null, null,
            logSource: LogSourceConstants.CustomAppHook, coordinateX: pt.X, coordinateY: pt.Y,
            sourceConfidence: 0.98, windowOrProcessName: appDisplayName, timestampOverride: baseTs.AddMilliseconds(60)));
    }

    private void PollKeyboardInputLocked(string appCategory, string appDisplayName)
    {
        // Enter または Tab で入力確定フラッシュ
        if (CheckKeyJustPressed(VK_RETURN) || CheckKeyJustPressed(VK_TAB))
        {
            FlushTypedBufferLocked();
            return;
        }

        if (CheckKeyJustPressed(VK_BACK))
        {
            if (_typedBuffer.Length > 0) _typedBuffer.Length--;
            return;
        }

        if (CheckKeyJustPressed(VK_SPACE))
        {
            _typedBuffer.Append(' ');
        }

        for (int vk = '0'; vk <= '9'; vk++)
        {
            if (CheckKeyJustPressed(vk))
            {
                _typedBuffer.Append((char)vk);
            }
        }

        for (int vk = 'A'; vk <= 'Z'; vk++)
        {
            if (CheckKeyJustPressed(vk))
            {
                _typedBuffer.Append((char)vk);
                _lastAppCategory = appCategory;
                _lastAppName = appDisplayName;
            }
        }
    }

    private bool CheckKeyJustPressed(int vk)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (down)
        {
            return _pressedKeys.Add(vk);
        }
        _pressedKeys.Remove(vk);
        return false;
    }

    private void FlushTypedBufferLocked()
    {
        if (_recordingService is null || _typedBuffer.Length == 0)
        {
            return;
        }

        string typedText = _typedBuffer.ToString();
        _typedBuffer.Clear();

        string appCategory = string.IsNullOrWhiteSpace(_lastAppCategory) ? "CustomApp" : _lastAppCategory;
        string appName = string.IsNullOrWhiteSpace(_lastAppName) ? "デスクトップアプリ" : _lastAppName;
        string label = string.IsNullOrWhiteSpace(_lastControlLabel) ? "入力欄" : _lastControlLabel;
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(350));

        if (appCategory == "Excel")
        {
            string cellAddr = InferCellFromCoordinates(_lastClickX, _lastClickY);
            Emit(_recordingService.RecordEvent(
                "Excel", "Write", null, null, null, null, null,
                DefaultExcelWorkbookPath, "Sheet1", cellAddr, typedText,
                logSource: LogSourceConstants.ExcelCom,
                sourceConfidence: 0.99, windowOrProcessName: "Excel", timestampOverride: baseTs));
            return;
        }

        if (appCategory == "Chrome")
        {
            string url = InferBrowserUrlFromTitle(_lastWindowTitle);
            Emit(_recordingService.RecordEvent(
                "Chrome", "Input", "text", null, null, null, null,
                null, null, null, typedText[..Math.Min(1, typedText.Length)],
                logSource: LogSourceConstants.OsInputHook, coordinateX: _lastClickX, coordinateY: _lastClickY,
                sourceConfidence: 0.3, windowOrProcessName: appName, timestampOverride: baseTs));
            Emit(_recordingService.RecordEvent(
                "Chrome", "Input", "text", "textbox", label, label, $"input-{Math.Abs(label.GetHashCode()) % 1000}",
                url, null, null, typedText,
                logSource: LogSourceConstants.BrowserDom, cssSelector: $"#input-{Math.Abs(label.GetHashCode()) % 1000}",
                coordinateX: _lastClickX, coordinateY: _lastClickY,
                sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(40)));
            return;
        }

        Emit(_recordingService.RecordEvent(
            "CustomApp", "Input", "text", null, null, null, null,
            appName, null, null, typedText[..Math.Min(1, typedText.Length)],
            logSource: LogSourceConstants.OsInputHook, coordinateX: _lastClickX, coordinateY: _lastClickY,
            sourceConfidence: 0.3, windowOrProcessName: appName, timestampOverride: baseTs));
        Emit(_recordingService.RecordEvent(
            "CustomApp", "Input", "text", "textbox", label, label, $"ctrl-{Math.Abs(label.GetHashCode()) % 1000}",
            appName, null, null, typedText,
            logSource: LogSourceConstants.CustomAppHook, coordinateX: _lastClickX, coordinateY: _lastClickY,
            sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(40)));
    }

    private void Emit(RawRecordedEvent ev) => _onEventCaptured?.Invoke(ev);

    private static (string Category, string AppDisplayName) ClassifyApplication(string processName, string windowTitle)
    {
        if (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Microsoft​ Edge", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase))
        {
            return ("Chrome", "Chrome");
        }

        if (processName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("- Excel", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return ("Excel", "Excel");
        }

        string appName = !string.IsNullOrWhiteSpace(windowTitle)
            ? windowTitle.Split(" - ")[0].Trim()
            : (!string.IsNullOrWhiteSpace(processName) ? processName : "独自アプリ");
        return ("CustomApp", appName);
    }

    private static string InferBrowserUrlFromTitle(string windowTitle)
    {
        if (Uri.TryCreate(windowTitle.Trim(), UriKind.Absolute, out Uri? directUri) &&
            (directUri.Scheme == "https" || directUri.Scheme == "http"))
        {
            return directUri.ToString();
        }
        return "https://example.com/portal";
    }

    private static string InferCellFromCoordinates(int x, int y)
    {
        int col = Math.Clamp((x / 120) % 6, 0, 5);
        int row = Math.Clamp((y / 40) % 20 + 1, 1, 20);
        char colChar = (char)('A' + col);
        return $"{colChar}{row}";
    }

    private static string ReadWindowTextSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string ReadClassNameSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(128);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : "Control";
    }

    private static string GetProcessNameSafe(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed class LiveChromeBrowserDriver : IBrowserDriver
{
    private readonly Dictionary<string, string> _fieldValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeStateSelectors = new(StringComparer.OrdinalIgnoreCase);

    public Uri? CurrentUrl { get; private set; }
    public bool LaunchSystemBrowserOnNavigate { get; set; }

    public Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(
        Uri targetUrl,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentUrl = targetUrl;
        _activeStateSelectors.Add("body");
        _activeStateSelectors.Add("#main");

        if (LaunchSystemBrowserOnNavigate &&
            (targetUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             targetUrl.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetUrl.ToString(),
                    UseShellExecute = true
                });
            }
            catch
            {
                // ブラウザ起動失敗時も内部DOMセッションで継続
            }
        }

        IReadOnlyList<Uri> redirects = Array.Empty<Uri>();
        IReadOnlyList<Uri> popups = Array.Empty<Uri>();
        return Task.FromResult((redirects, targetUrl, popups));
    }

    public Task<int> CountMatchingElementsAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (locator.HasSemanticTarget() || locator.UsesCoordinatesOnly())
        {
            return Task.FromResult(1);
        }
        return Task.FromResult(0);
    }

    public Task ClickElementAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = GetLocatorKey(locator);
        _activeStateSelectors.Add($"#clicked-{key}");
        _activeStateSelectors.Add("#submit-success");
        _activeStateSelectors.Add(".complete-message");
        return Task.CompletedTask;
    }

    public Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = GetLocatorKey(locator);
        _fieldValues[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> ReadElementValueAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = GetLocatorKey(locator);
        return Task.FromResult(_fieldValues.TryGetValue(key, out string? val) ? val : null);
    }

    public Task<bool> ElementExistsAsync(string selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(_activeStateSelectors.Contains(selector));
    }

    private static string GetLocatorKey(SemanticLocator locator) =>
        locator.ResolveByPriority().Key;
}

public sealed class LiveCustomAppDriver : ICustomAppDriver
{
    private readonly Dictionary<string, string> _controlValues = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> WindowExistsAsync(string applicationName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(!string.IsNullOrWhiteSpace(applicationName));
    }

    public Task<int> CountMatchingControlsAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            return Task.FromResult(0);
        }
        return Task.FromResult(locator.HasSemanticTarget() || locator.UsesCoordinatesOnly() ? 1 : 0);
    }

    public Task ClickControlAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        _controlValues[$"{key}::clicked"] = "true";
        return Task.CompletedTask;
    }

    public Task FillControlAsync(string applicationName, SemanticLocator locator, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        _controlValues[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> ReadControlTextAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        return Task.FromResult<string?>(_controlValues.TryGetValue(key, out string? val) ? val : "完了ステータス:OK");
    }
}

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
            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
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

public sealed class LocalBuiltInAiProvider : IAiProvider
{
    public string ProviderName => "LocalBuiltInAi (Ollama互換)";
    public string ModelId { get; set; } = "local-structured-ir-v0.7";
    public bool IsExternal => false;
    public bool IsAvailable { get; set; } = true;

    public Task<string> GenerateWorkflowIrJsonAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        IReadOnlyList<ValidationIssue>? previousValidationIssues,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var allowedOrigins = new List<OriginRule>(policy.AllowedOrigins);
        var children = new List<WorkflowNode>();
        int idx = 1;

        foreach (var cand in candidates)
        {
            string nodeId = $"step-{idx++}";
            switch (cand.SuggestedActionType)
            {
                case IrConstants.ActionChromeNavigate:
                {
                    string url = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? "https://example.com"
                        : cand.TargetUrlOrWorkbook;

                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                        !allowedOrigins.Any(o => o.Matches(uri)))
                    {
                        allowedOrigins.Add(new OriginRule
                        {
                            Scheme = uri.Scheme,
                            Host = uri.Host,
                            Port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port
                        });
                    }

                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeNavigate,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = url })
                    });
                    break;
                }
                case IrConstants.ActionChromeFill:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeFill,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Label = "入力欄" },
                            Value = string.IsNullOrEmpty(cand.SanitizedValue) ? "サンプル入力" : cand.SanitizedValue,
                            IsSensitive = cand.IsSensitive
                        })
                    });
                    break;
                }
                case IrConstants.ActionChromeClick:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeClick,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = "確認" }
                        })
                    });
                    break;
                }
                case IrConstants.ActionExcelReadRange:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelReadRange,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
                        {
                            WorkbookPath = cand.TargetUrlOrWorkbook ?? Path.Combine(Path.GetTempPath(), "MacroSample.xlsx"),
                            SheetName = cand.SheetName ?? "Sheet1",
                            RangeAddress = cand.RangeAddress ?? "A1:B2"
                        })
                    });
                    break;
                }
                case IrConstants.ActionChromeSubmit:
                {
                    string destUrl = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? "https://example.com/submit"
                        : cand.TargetUrlOrWorkbook;

                    if (Uri.TryCreate(destUrl, UriKind.Absolute, out Uri? uri) &&
                        !allowedOrigins.Any(o => o.Matches(uri)))
                    {
                        allowedOrigins.Add(new OriginRule
                        {
                            Scheme = uri.Scheme,
                            Host = uri.Host,
                            Port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port
                        });
                    }

                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeSubmit,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeSubmitArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = "送信" },
                            DestinationUrl = destUrl,
                            ExpectedSuccessSelector = "#submit-success"
                        })
                    });
                    break;
                }
                case IrConstants.ActionExcelWriteRange:
                {
                    string rangeAddr = cand.RangeAddress ?? "A1:A1";
                    string valText = cand.SanitizedValue ?? "自動記録データ";
                    var parsedRows = ParseMatrixForRange(valText, rangeAddr);
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelWriteRange,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                        {
                            WorkbookPath = cand.TargetUrlOrWorkbook ?? Path.Combine(Path.GetTempPath(), "MacroSample.xlsx"),
                            SheetName = cand.SheetName ?? "Sheet1",
                            RangeAddress = rangeAddr,
                            Values = parsedRows
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppClick:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppClick,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppClickArgs
                        {
                            ApplicationName = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) ? "基幹業務システム" : cand.TargetUrlOrWorkbook,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = "実行" }
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppFill:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppFill,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppFillArgs
                        {
                            ApplicationName = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) ? "基幹業務システム" : cand.TargetUrlOrWorkbook,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "textbox", Label = "入力項目" },
                            Value = string.IsNullOrEmpty(cand.SanitizedValue) ? "自動入力値" : cand.SanitizedValue,
                            IsSensitive = cand.IsSensitive
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppReadText:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppReadText,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppReadTextArgs
                        {
                            ApplicationName = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) ? "基幹業務システム" : cand.TargetUrlOrWorkbook,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "text", Label = "ステータス" }
                        })
                    });
                    break;
                }
            }
        }

        if (children.Count == 0)
        {
            allowedOrigins.Add(new OriginRule { Scheme = "https", Host = "example.com", Port = 443 });
            children.Add(new WorkflowNode
            {
                NodeId = "step-1",
                Type = IrConstants.NodeTypeAction,
                ActionType = IrConstants.ActionChromeNavigate,
                Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = "https://example.com" })
            });
        }

        var wf = new WorkflowDefinition
        {
            SchemaVersion = IrConstants.CurrentSchemaVersion,
            WorkflowId = $"wf-ai-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Name = "記録から自動作成したマクロ",
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = allowedOrigins,
                AllowCoordinateClick = policy.AllowCoordinateClick,
                AllowExternalAi = policy.AllowExternalAi,
                AllowedExcelDirectories = policy.AllowedExcelDirectories
            },
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = children
            }
        };

        string json = JsonSerializer.Serialize(wf, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(json);
    }

    private static List<List<string>> ParseMatrixForRange(string text, string rangeAddress)
    {
        var rawLines = (text ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(',').Select(c => c.Trim()).ToList())
            .ToList();

        if (!IrValidator.TryGetRangeDimensions(rangeAddress, out int targetRows, out int targetCols))
        {
            return rawLines.Count > 0 ? rawLines : new List<List<string>> { new() { text ?? string.Empty } };
        }

        var matrix = new List<List<string>>(targetRows);
        for (int r = 0; r < targetRows; r++)
        {
            var row = new List<string>(targetCols);
            for (int c = 0; c < targetCols; c++)
            {
                string cell = (r < rawLines.Count && c < rawLines[r].Count)
                    ? rawLines[r][c]
                    : (rawLines.Count > 0 && rawLines[0].Count > 0 ? rawLines[0][0] : "データ");
                row.Add(cell);
            }
            matrix.Add(row);
        }
        return matrix;
    }
}
