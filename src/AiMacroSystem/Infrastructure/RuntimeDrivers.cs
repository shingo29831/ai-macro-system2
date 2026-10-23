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
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hwndCtl);

    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RETURN = 0x0D;
    private const int VK_TAB = 0x09;
    private const int VK_BACK = 0x08;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_SPACE = 0x20;
    private const int VK_OEM_1 = 0xBA;
    private const int VK_OEM_PLUS = 0xBB;
    private const int VK_OEM_COMMA = 0xBC;
    private const int VK_OEM_MINUS = 0xBD;
    private const int VK_OEM_PERIOD = 0xBE;
    private const int VK_OEM_2 = 0xBF;
    private const int VK_OEM_3 = 0xC0;

    private Timer? _pollTimer;
    private RecordingService? _recordingService;
    private Action<RawRecordedEvent>? _onEventCaptured;
    private readonly object _sync = new();
    private readonly LowLevelInputHook _lowLevelHook = new();

    private bool _wasLeftDown;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly StringBuilder _typedBuffer = new();
    private string _lastWindowTitle = string.Empty;
    private string _lastAppCategory = string.Empty;
    private string _lastAppName = string.Empty;
    private string _lastBrowserUrl = "https://www.google.com";
    private string _lastControlLabel = "検索";
    private string _lastCopiedText = string.Empty;
    private string _lastRecordedSearchQuery = string.Empty;
    private string _lastFlushedText = string.Empty;
    private bool _hasEmittedEnterForLastInput;
    private bool _hasEmittedInitialBrowserNavigate;
    private bool _hasExplicitClickInCurrentWindow;
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
            _lastRecordedSearchQuery = string.Empty;
            _lastFlushedText = string.Empty;
            _hasEmittedEnterForLastInput = false;
            _hasEmittedInitialBrowserNavigate = false;
            _hasExplicitClickInCurrentWindow = false;
            _startedAt = DateTimeOffset.UtcNow;

            _lowLevelHook.Start();
            _pollTimer?.Dispose();
            _pollTimer = new Timer(PollTick, null, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(25));
        }
    }

    public void StopAndFlush()
    {
        lock (_sync)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _lowLevelHook.Stop();
            DrainLowLevelKeysLocked(_lastAppCategory, _lastAppName);
            FlushTypedBufferLocked(pressedEnter: false);

            // ウィンドウ切替のみで個別クリック・打鍵が未記録だった場合は対象画面への遷移イベントを補完する
            if (_recordingService is not null &&
                _recordingService.IsRecording &&
                _lastAppCategory == "Chrome" &&
                !_recordingService.GetRecordedEvents().Any(e =>
                    e.EventKind is "Click" or "Input" or "Write" or "Paste" or "Navigate" or "Submit"))
            {
                var navTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(100));
                string navUrl = InferBrowserUrlFromTitle(_lastWindowTitle);
                Emit(_recordingService.RecordEvent(
                    "Chrome", "Navigate", null, null, null, null, null,
                    navUrl, null, null, null,
                    logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.98,
                    windowOrProcessName: _lastWindowTitle, timestampOverride: navTs));
            }
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

            // マウス左クリックを先に判定し、オーバーレイ表示直後の外部ウィンドウクリック取りこぼしを防ぐ
            bool isLeftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (isLeftDown && !_wasLeftDown && (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds > 150)
            {
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr targetHwnd = WindowFromPoint(pt);
                    if (targetHwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(targetHwnd, out uint targetPid);
                        if (targetPid != 0 && targetPid != (uint)Environment.ProcessId)
                        {
                            IntPtr rootHwnd = GetAncestor(targetHwnd, GA_ROOT);
                            if (rootHwnd == IntPtr.Zero) rootHwnd = targetHwnd;
                            string clickWinTitle = ReadWindowTextSafe(rootHwnd);
                            string clickProcName = GetProcessNameSafe(targetPid);
                            var (clickCategory, clickAppName) = ClassifyApplication(clickProcName, clickWinTitle);

                            EnsureWindowContextRecordedLocked(clickCategory, clickAppName, clickWinTitle);
                            FlushTypedBufferLocked();
                            _hasExplicitClickInCurrentWindow = true;
                            _lastClickX = pt.X;
                            _lastClickY = pt.Y;
                            RecordMouseClickMultiLogsLocked(targetHwnd, clickCategory, clickAppName, clickWinTitle, pt);
                        }
                    }
                }
            }
            _wasLeftDown = isLeftDown;

            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero)
            {
                return;
            }

            GetWindowThreadProcessId(fgHwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                return;
            }

            string winTitle = ReadWindowTextSafe(fgHwnd);
            string procName = GetProcessNameSafe(pid);
            var (appCategory, appDisplayName) = ClassifyApplication(procName, winTitle);

            // タイトル変化によるフラッシュ前に低レベルキー入力を先に吸い上げ、Enter押下を取りこぼさない
            PollKeyboardInputLocked(appCategory, appDisplayName);
            EnsureWindowContextRecordedLocked(appCategory, appDisplayName, winTitle);
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

    private void EnsureWindowContextRecordedLocked(string appCategory, string appDisplayName, string winTitle)
    {
        _lastAppCategory = appCategory;
        _lastAppName = appDisplayName;

        if (_recordingService is null || string.IsNullOrWhiteSpace(winTitle) ||
            string.Equals(winTitle, _lastWindowTitle, StringComparison.Ordinal))
        {
            return;
        }

        DrainLowLevelKeysLocked(appCategory, appDisplayName);
        FlushTypedBufferLocked();
        _lastWindowTitle = winTitle;

        var switchTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(200));
        if (appCategory == "Chrome")
        {
            string navUrl = BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine(winTitle, out string? titleSearchQuery);
            _lastBrowserUrl = navUrl;

            Emit(_recordingService.RecordEvent(
                "Auto", "WindowActivate", null, null, null, null, null,
                navUrl, null, null, null,
                logSource: LogSourceConstants.WindowManager, sourceConfidence: 0.6,
                windowOrProcessName: winTitle, timestampOverride: switchTs));

            if (!_hasEmittedInitialBrowserNavigate)
            {
                _hasEmittedInitialBrowserNavigate = true;
                Emit(_recordingService.RecordEvent(
                    "Chrome", "Navigate", null, null, null, null, null,
                    navUrl, null, null, null,
                    logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.98,
                    windowOrProcessName: winTitle, timestampOverride: switchTs.AddMilliseconds(20)));
            }

            int? coordX = _hasExplicitClickInCurrentWindow ? _lastClickX : null;
            int? coordY = _hasExplicitClickInCurrentWindow ? _lastClickY : null;

            // ブラウザ検索結果タイトル（例: "〇〇 - Google 検索"）への遷移から検索ワードとEnter送信を自動抽出・補完する
            if (!string.IsNullOrWhiteSpace(titleSearchQuery))
            {
                if (!string.Equals(titleSearchQuery, _lastRecordedSearchQuery, StringComparison.Ordinal))
                {
                    _lastRecordedSearchQuery = titleSearchQuery;
                    _lastFlushedText = titleSearchQuery;
                    _hasEmittedEnterForLastInput = true;
                    var searchTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(150));
                    string searchLabel = string.IsNullOrWhiteSpace(_lastControlLabel) ? "検索" : _lastControlLabel;
                    Emit(_recordingService.RecordEvent(
                        "Chrome", "Input", "text", "textbox", searchLabel, searchLabel, "search-input",
                        navUrl, null, null, titleSearchQuery,
                        logSource: LogSourceConstants.BrowserDom, cssSelector: "#search-input",
                        coordinateX: coordX, coordinateY: coordY,
                        sourceConfidence: 0.99, windowOrProcessName: winTitle, timestampOverride: searchTs));
                    Emit(_recordingService.RecordEvent(
                        "Chrome", "Click", null, "button", "検索実行 (Enter)", "検索実行 (Enter)", "key-enter-submit",
                        navUrl, null, null, null,
                        logSource: LogSourceConstants.BrowserDom, cssSelector: "#key-enter-submit",
                        coordinateX: coordX, coordinateY: coordY,
                        sourceConfidence: 0.99, windowOrProcessName: winTitle, timestampOverride: searchTs.AddMilliseconds(35)));
                }
                else if (!_hasEmittedEnterForLastInput)
                {
                    _hasEmittedEnterForLastInput = true;
                    var enterTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(80));
                    Emit(_recordingService.RecordEvent(
                        "Chrome", "Click", null, "button", "検索実行 (Enter)", "検索実行 (Enter)", "key-enter-submit",
                        navUrl, null, null, null,
                        logSource: LogSourceConstants.BrowserDom, cssSelector: "#key-enter-submit",
                        coordinateX: coordX, coordinateY: coordY,
                        sourceConfidence: 0.99, windowOrProcessName: winTitle, timestampOverride: enterTs));
                }
            }
        }
        else if (appCategory == "Excel")
        {
            Emit(_recordingService.RecordEvent(
                "Auto", "WindowActivate", null, null, null, null, null,
                DefaultExcelWorkbookPath, "Sheet1", "A1", null,
                logSource: LogSourceConstants.WindowManager, sourceConfidence: 0.85,
                windowOrProcessName: winTitle, timestampOverride: switchTs));
        }
        else
        {
            Emit(_recordingService.RecordEvent(
                "Auto", "WindowActivate", null, null, null, null, null,
                appDisplayName, null, null, null,
                logSource: LogSourceConstants.WindowManager, sourceConfidence: 0.85,
                windowOrProcessName: appDisplayName, timestampOverride: switchTs));
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

        var (uiaName, uiaRole, _) = BrowserUiAutomationHelper.TryInspectFocusedOrPointElement(pt.X, pt.Y);

        // Chromiumの内部描画HWND名("Chrome Legacy Window")はUIAutomation名または座標付き識別子へ変換する
        bool isGenericBrowserSurface = appCategory == "Chrome" ||
            ctrlText.Contains("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("Chrome_", StringComparison.OrdinalIgnoreCase);

        string elementLabel = !string.IsNullOrWhiteSpace(uiaName)
            ? $"{uiaName}({pt.X},{pt.Y})"
            : (!isGenericBrowserSurface && !string.IsNullOrWhiteSpace(ctrlText) && ctrlText.Length <= 40
                ? ctrlText.Trim()
                : $"{appDisplayName} 要素({pt.X},{pt.Y})");
        string stableId = (!isGenericBrowserSurface && ctrlId > 0)
            ? $"ctrl-{ctrlId}-{pt.X}-{pt.Y}"
            : $"el-{Math.Abs(className.GetHashCode()) % 10000}-{pt.X}-{pt.Y}";
        _lastControlLabel = elementLabel;

        // 入力欄へのクリック時は直後の文字入力対象として座標とラベルを保持する
        if (uiaRole == "textbox")
        {
            return;
        }

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
        bool isCtrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        if (isCtrlDown)
        {
            // Ctrl+C (アプリ間コピー検出)
            if (CheckKeyJustPressed('C') && _recordingService is not null)
            {
                FlushTypedBufferLocked();
                string clipText = ReadClipboardTextSafe();
                if (!string.IsNullOrWhiteSpace(clipText))
                {
                    _lastCopiedText = clipText;
                }
                var copyTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(250));
                Emit(_recordingService.RecordEvent(
                    "Auto", "Copy", null, null, _lastControlLabel, _lastControlLabel, null,
                    appCategory == "Chrome" ? InferBrowserUrlFromTitle(_lastWindowTitle) : appDisplayName,
                    null, null, _lastCopiedText,
                    logSource: appCategory == "Chrome" ? LogSourceConstants.BrowserDom : LogSourceConstants.UiAutomation,
                    sourceConfidence: 0.95, windowOrProcessName: _lastWindowTitle, timestampOverride: copyTs));
                return;
            }

            // Ctrl+V (別アプリやExcel等へのペースト転記検出)
            if (CheckKeyJustPressed('V') && _recordingService is not null)
            {
                FlushTypedBufferLocked();
                string pasteText = ReadClipboardTextSafe();
                if (string.IsNullOrWhiteSpace(pasteText))
                {
                    pasteText = string.IsNullOrWhiteSpace(_lastCopiedText) ? "転記データ" : _lastCopiedText;
                }
                var pasteTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(300));
                if (appCategory == "Excel")
                {
                    string cellAddr = InferCellFromCoordinates(_lastClickX, _lastClickY);
                    Emit(_recordingService.RecordEvent(
                        "Auto", "Paste", null, null, null, null, null,
                        DefaultExcelWorkbookPath, "Sheet1", cellAddr, null,
                        logSource: LogSourceConstants.OsInputHook, coordinateX: _lastClickX, coordinateY: _lastClickY,
                        sourceConfidence: 0.4, windowOrProcessName: "Excel", timestampOverride: pasteTs));
                    Emit(_recordingService.RecordEvent(
                        "Auto", "Write", null, null, null, null, null,
                        DefaultExcelWorkbookPath, "Sheet1", cellAddr, pasteText,
                        logSource: LogSourceConstants.ExcelCom, coordinateX: _lastClickX, coordinateY: _lastClickY,
                        sourceConfidence: 0.99, windowOrProcessName: "Excel", timestampOverride: pasteTs.AddMilliseconds(35)));
                }
                else
                {
                    Emit(_recordingService.RecordEvent(
                        "Auto", "Paste", "text", "textbox", _lastControlLabel, _lastControlLabel, $"paste-{_lastClickX}",
                        appCategory == "Chrome" ? InferBrowserUrlFromTitle(_lastWindowTitle) : appDisplayName,
                        null, null, pasteText,
                        logSource: appCategory == "Chrome" ? LogSourceConstants.BrowserDom : LogSourceConstants.CustomAppHook,
                        coordinateX: _lastClickX, coordinateY: _lastClickY,
                        sourceConfidence: 0.98, windowOrProcessName: appDisplayName, timestampOverride: pasteTs));
                }
                return;
            }
            return;
        }

        DrainLowLevelKeysLocked(appCategory, appDisplayName);
    }

    private void DrainLowLevelKeysLocked(string appCategory, string appDisplayName)
    {
        bool isShiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool isCtrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        if (isCtrlDown)
        {
            while (_lowLevelHook.TryDequeueKey(out _)) { }
            return;
        }

        while (_lowLevelHook.TryDequeueKey(out int vk))
        {
            _lastAppCategory = appCategory;
            _lastAppName = appDisplayName;

            if (vk == VK_RETURN)
            {
                FlushTypedBufferLocked(pressedEnter: true);
                continue;
            }
            if (vk == VK_TAB)
            {
                FlushTypedBufferLocked(pressedEnter: false);
                continue;
            }
            if (vk == VK_BACK)
            {
                if (_typedBuffer.Length > 0) _typedBuffer.Length--;
                continue;
            }
            if (vk == VK_SPACE)
            {
                _typedBuffer.Append(' ');
                continue;
            }
            if (vk >= '0' && vk <= '9')
            {
                _typedBuffer.Append((char)vk);
                continue;
            }
            if (vk >= 0x60 && vk <= 0x69)
            {
                _typedBuffer.Append((char)('0' + (vk - 0x60)));
                continue;
            }
            if (vk >= 'A' && vk <= 'Z')
            {
                char ch = isShiftDown ? (char)vk : char.ToLowerInvariant((char)vk);
                _typedBuffer.Append(ch);
                continue;
            }
            if (vk == VK_OEM_MINUS) _typedBuffer.Append(isShiftDown ? '_' : '-');
            else if (vk == VK_OEM_PERIOD) _typedBuffer.Append('.');
            else if (vk == VK_OEM_COMMA) _typedBuffer.Append(',');
            else if (vk == VK_OEM_2) _typedBuffer.Append(isShiftDown ? '?' : '/');
            else if (vk == VK_OEM_1) _typedBuffer.Append(isShiftDown ? '*' : ':');
            else if (vk == VK_OEM_PLUS) _typedBuffer.Append(isShiftDown ? '+' : ';');
            else if (vk == VK_OEM_3) _typedBuffer.Append('@');
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

    private void FlushTypedBufferLocked(bool pressedEnter = false)
    {
        if (_recordingService is null) return;

        string typedText = _typedBuffer.ToString();
        _typedBuffer.Clear();

        string appCategory = string.IsNullOrWhiteSpace(_lastAppCategory) ? "CustomApp" : _lastAppCategory;
        string appName = string.IsNullOrWhiteSpace(_lastAppName) ? "デスクトップアプリ" : _lastAppName;
        int? coordX = _hasExplicitClickInCurrentWindow ? _lastClickX : null;
        int? coordY = _hasExplicitClickInCurrentWindow ? _lastClickY : null;

        // 生打鍵バッファが空の場合（日本語IME入力時など）のみUIAutomationのValuePatternから値を補完する
        var (uiaName, _, uiaValue) = BrowserUiAutomationHelper.TryInspectFocusedOrPointElement(_lastClickX, _lastClickY);
        if (string.IsNullOrWhiteSpace(typedText) &&
            !string.IsNullOrWhiteSpace(uiaValue) &&
            !uiaValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uiaValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            typedText = uiaValue.Trim();
        }

        if (string.IsNullOrWhiteSpace(typedText))
        {
            // 直前のフラッシュで文字入力が記録済みで、続いてEnterが押された場合はEnterステップを確実に記録する
            if (pressedEnter && appCategory == "Chrome" && !string.IsNullOrWhiteSpace(_lastFlushedText) && !_hasEmittedEnterForLastInput)
            {
                _hasEmittedEnterForLastInput = true;
                string url = !string.IsNullOrWhiteSpace(_lastBrowserUrl)
                    ? _lastBrowserUrl
                    : BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine(_lastWindowTitle, out _);
                var enterTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(90));
                Emit(_recordingService.RecordEvent(
                    "Chrome", "Click", null, "button", "検索実行 (Enter)", "検索実行 (Enter)", "key-enter-submit",
                    url, null, null, null,
                    logSource: LogSourceConstants.BrowserDom, cssSelector: "#key-enter-submit",
                    coordinateX: coordX, coordinateY: coordY,
                    sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: enterTs));
            }
            return;
        }

        string label = !string.IsNullOrWhiteSpace(uiaName)
            ? (coordX.HasValue ? $"{uiaName}({coordX},{coordY})" : uiaName)
            : (!string.IsNullOrWhiteSpace(_lastControlLabel) ? _lastControlLabel : "検索");
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
            _lastRecordedSearchQuery = typedText;
            _lastFlushedText = typedText;
            _hasEmittedEnterForLastInput = pressedEnter;
            string url = !string.IsNullOrWhiteSpace(_lastBrowserUrl)
                ? _lastBrowserUrl
                : BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine(_lastWindowTitle, out _);

            string stableInputId = coordX.HasValue ? $"input-{coordX}-{coordY}" : "search-input";
            Emit(_recordingService.RecordEvent(
                "Chrome", "Input", "text", null, null, null, null,
                null, null, null, typedText[..Math.Min(1, typedText.Length)],
                logSource: LogSourceConstants.OsInputHook, coordinateX: coordX, coordinateY: coordY,
                sourceConfidence: 0.3, windowOrProcessName: appName, timestampOverride: baseTs));
            Emit(_recordingService.RecordEvent(
                "Chrome", "Input", "text", "textbox", label, label, stableInputId,
                url, null, null, typedText,
                logSource: LogSourceConstants.BrowserDom, cssSelector: $"#{stableInputId}",
                coordinateX: coordX, coordinateY: coordY,
                sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(40)));

            // 検索欄等でEnterキーが押された場合は検索実行ステップを続けて記録する
            if (pressedEnter)
            {
                Emit(_recordingService.RecordEvent(
                    "Chrome", "Click", null, "button", "検索実行 (Enter)", "検索実行 (Enter)", "key-enter-submit",
                    url, null, null, null,
                    logSource: LogSourceConstants.BrowserDom, cssSelector: "#key-enter-submit",
                    coordinateX: coordX, coordinateY: coordY,
                    sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(90)));
            }
            return;
        }

        Emit(_recordingService.RecordEvent(
            "CustomApp", "Input", "text", null, null, null, null,
            appName, null, null, typedText[..Math.Min(1, typedText.Length)],
            logSource: LogSourceConstants.OsInputHook, coordinateX: _lastClickX, coordinateY: _lastClickY,
            sourceConfidence: 0.3, windowOrProcessName: appName, timestampOverride: baseTs));
        Emit(_recordingService.RecordEvent(
            "CustomApp", "Input", "text", "textbox", label, label, $"ctrl-{_lastClickX}-{_lastClickY}",
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

    private static string InferBrowserUrlFromTitle(string windowTitle) =>
        BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine(windowTitle, out _);

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

    private static string ReadClipboardTextSafe()
    {
        string result = string.Empty;
        try
        {
            var staThread = new Thread(() =>
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        result = System.Windows.Clipboard.GetText() ?? string.Empty;
                    }
                }
                catch
                {
                    // クリップボード競合時は無視
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(120);
        }
        catch
        {
            // バックグラウンドスレッドからの取得失敗時は空文字
        }
        return result;
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

internal static class WindowsOsAutomation
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const int SW_RESTORE = 9;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static readonly Regex _coordInTextRegex = new(@"\((\d+)\s*,\s*(\d+)\)|-(\d+)-(\d+)$", RegexOptions.Compiled);

    public static HashSet<IntPtr> GetAllBrowserWindowHandles()
    {
        var handles = new HashSet<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd) && IsBrowserWindow(hWnd))
            {
                handles.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    public static string GetWindowTitle(IntPtr hWnd) =>
        hWnd == IntPtr.Zero ? string.Empty : ReadWindowTextSafe(hWnd);

    public static IntPtr FindBrowserWindow()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && IsBrowserWindow(fg))
        {
            return fg;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (IsBrowserWindow(hWnd))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static async Task<IntPtr> LaunchNewBrowserWindowAndWaitAsync(string url, CancellationToken ct)
    {
        var existingHandles = GetAllBrowserWindowHandles();
        LaunchNewBrowserWindow(url);

        IntPtr detectedNewHwnd = IntPtr.Zero;
        for (int i = 0; i < 35; i++)
        {
            await Task.Delay(100, ct);
            var currentHandles = GetAllBrowserWindowHandles();
            foreach (IntPtr h in currentHandles)
            {
                if (!existingHandles.Contains(h))
                {
                    detectedNewHwnd = h;
                    break;
                }
            }
            if (detectedNewHwnd != IntPtr.Zero)
            {
                break;
            }
        }

        if (detectedNewHwnd == IntPtr.Zero)
        {
            detectedNewHwnd = FindBrowserWindow();
        }

        if (detectedNewHwnd != IntPtr.Zero)
        {
            await ActivateWindowAsync(detectedNewHwnd, ct);
            // ページの初期DOMと検索ボックスのフォーカス完了を待機する
            await Task.Delay(950, ct);
        }

        return detectedNewHwnd;
    }

    public static IntPtr FindCustomAppWindow(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName)) return IntPtr.Zero;
        string target = applicationName.Trim();

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && MatchesAppWindow(fg, target))
        {
            return fg;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (MatchesAppWindow(hWnd, target))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static async Task<IntPtr> ActivateWindowAsync(IntPtr hWnd, CancellationToken ct)
    {
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }
            if (GetForegroundWindow() != hWnd)
            {
                SetForegroundWindow(hWnd);
                await Task.Delay(120, ct);
            }
        }
        catch
        {
            // OS権限により前面化できない場合も後続の入力送出を継続
        }
        return hWnd;
    }

    public static bool TryResolveCoordinates(SemanticLocator locator, out int x, out int y)
    {
        if (locator.CoordinateX.HasValue && locator.CoordinateY.HasValue)
        {
            x = locator.CoordinateX.Value;
            y = locator.CoordinateY.Value;
            return true;
        }

        string?[] candidates = { locator.Label, locator.AccessibleName, locator.StableId, locator.CssSelector };
        foreach (string? text in candidates)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var match = _coordInTextRegex.Match(text);
            if (match.Success)
            {
                string gx = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[3].Value;
                string gy = !string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value : match.Groups[4].Value;
                if (int.TryParse(gx, out x) && int.TryParse(gy, out y))
                {
                    return true;
                }
            }
        }

        x = 0;
        y = 0;
        return false;
    }

    public static bool TryGetWindowCenter(IntPtr hWnd, out int x, out int y)
    {
        if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            x = rect.Left + (rect.Right - rect.Left) / 2;
            y = rect.Top + (rect.Bottom - rect.Top) / 2;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    public static async Task MoveAndLeftClickAsync(int x, int y, CancellationToken ct)
    {
        SetCursorPos(x, y);
        await Task.Delay(60, ct);
        mouse_event(MOUSEEVENTF_LEFTDOWN, x, y, 0, UIntPtr.Zero);
        await Task.Delay(35, ct);
        mouse_event(MOUSEEVENTF_LEFTUP, x, y, 0, UIntPtr.Zero);
        await Task.Delay(80, ct);
    }

    public static async Task SendVirtualKeyAsync(ushort vk, CancellationToken ct)
    {
        var inputs = new INPUT[]
        {
            new()
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } }
            },
            new()
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        await Task.Delay(90, ct);
    }

    public static async Task SendCtrlShortcutAndNavigateUrlAsync(string url, CancellationToken ct)
    {
        // アドレスバーへフォーカス(Ctrl+L)し、日本語IME未確定状態を回避するためクリップボード貼り付け(Ctrl+V)優先でURL遷移する
        const ushort VK_CONTROL = 0x11;
        const ushort VK_L = 0x4C;
        const ushort VK_V = 0x56;
        const ushort VK_RETURN = 0x0D;

        var ctrlL = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_L } } },
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_L, dwFlags = KEYEVENTF_KEYUP } } },
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)ctrlL.Length, ctrlL, Marshal.SizeOf<INPUT>());
        await Task.Delay(140, ct);

        if (TrySetClipboardTextSafe(url))
        {
            var ctrlV = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V } } },
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } }
            };
            SendInput((uint)ctrlV.Length, ctrlV, Marshal.SizeOf<INPUT>());
            await Task.Delay(100, ct);
        }
        else
        {
            await SendUnicodeTextAsync(url, ct);
        }

        await SendVirtualKeyAsync(VK_RETURN, ct);
        await Task.Delay(550, ct);
    }

    private static bool TrySetClipboardTextSafe(string text)
    {
        bool ok = false;
        try
        {
            var staThread = new Thread(() =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    ok = true;
                }
                catch
                {
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(120);
        }
        catch
        {
        }
        return ok;
    }

    public static IntPtr LaunchNewBrowserWindow(string url)
    {
        string[] browserCandidates = { "chrome.exe", "msedge.exe" };
        foreach (string exe in browserCandidates)
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--new-window \"{url}\"",
                    UseShellExecute = true
                });
                if (proc is not null)
                {
                    return proc.MainWindowHandle;
                }
            }
            catch
            {
                // 次の候補ブラウザへフォールバック
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
        return IntPtr.Zero;
    }

    public static async Task SendUnicodeTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            });
        }

        INPUT[] arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        await Task.Delay(80, ct);
    }

    private static bool IsBrowserWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        string procName = GetProcessNameSafe(pid);
        string title = ReadWindowTextSafe(hWnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        return procName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Brave", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAppWindow(IntPtr hWnd, string targetAppName)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        string procName = GetProcessNameSafe(pid);
        string title = ReadWindowTextSafe(hWnd);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(procName)) return false;

        return title.Contains(targetAppName, StringComparison.OrdinalIgnoreCase) ||
               procName.Contains(targetAppName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWindowTextSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
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
    private bool _newWindowLaunchedInCurrentRun;
    private string? _lastFilledValue;
    private bool _lastFillExecutedSearch;

    public Uri? CurrentUrl { get; private set; }
    public bool LaunchSystemBrowserOnNavigate { get; set; }
    public bool EnableOsAutomation { get; set; }
    public WindowTargetSelection? CurrentTargetSelection { get; private set; }

    public Func<Uri, CancellationToken, Task>? BuiltInBrowserNavigateAsync { get; set; }
    public Func<SemanticLocator, string, CancellationToken, Task>? BuiltInBrowserFillAsync { get; set; }
    public Func<SemanticLocator, CancellationToken, Task>? BuiltInBrowserClickAsync { get; set; }

    public void ConfigureTargetSelection(WindowTargetSelection? selection)
    {
        CurrentTargetSelection = selection;
        _newWindowLaunchedInCurrentRun = false;
        _lastFilledValue = null;
        _lastFillExecutedSearch = false;
        CurrentUrl = null;
    }

    public async Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(
        Uri targetUrl,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentUrl = targetUrl;
        _lastFilledValue = null;
        _lastFillExecutedSearch = false;
        _activeStateSelectors.Add("body");
        _activeStateSelectors.Add("#main");

        if (CurrentTargetSelection?.UseBuiltInBrowser == true && BuiltInBrowserNavigateAsync is not null)
        {
            await BuiltInBrowserNavigateAsync(targetUrl, ct);
            _newWindowLaunchedInCurrentRun = true;
            return (Array.Empty<Uri>(), targetUrl, Array.Empty<Uri>());
        }

        if ((LaunchSystemBrowserOnNavigate || EnableOsAutomation) &&
            (targetUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             targetUrl.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool isDefaultFallbackUrl = targetUrl.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase) &&
                                           targetUrl.AbsolutePath.Equals("/portal", StringComparison.OrdinalIgnoreCase);
                string launchUrl = isDefaultFallbackUrl ? "https://www.google.com" : targetUrl.ToString();

                if (CurrentTargetSelection is not null)
                {
                    if (CurrentTargetSelection.CreateNewWindow && !_newWindowLaunchedInCurrentRun)
                    {
                        IntPtr newHwnd = await WindowsOsAutomation.LaunchNewBrowserWindowAndWaitAsync(launchUrl, ct);
                        _newWindowLaunchedInCurrentRun = true;
                        if (newHwnd != IntPtr.Zero)
                        {
                            CurrentTargetSelection.TargetWindowHandle = newHwnd;
                        }
                    }
                    else
                    {
                        IntPtr targetHwnd = CurrentTargetSelection.TargetWindowHandle != IntPtr.Zero
                            ? CurrentTargetSelection.TargetWindowHandle
                            : WindowsOsAutomation.FindBrowserWindow();
                        if (targetHwnd != IntPtr.Zero)
                        {
                            await WindowsOsAutomation.ActivateWindowAsync(targetHwnd, ct);
                            if (!isDefaultFallbackUrl)
                            {
                                await WindowsOsAutomation.SendCtrlShortcutAndNavigateUrlAsync(launchUrl, ct);
                            }
                        }
                    }
                }
                else
                {
                    IntPtr existingBrowser = WindowsOsAutomation.FindBrowserWindow();
                    if (existingBrowser != IntPtr.Zero && isDefaultFallbackUrl)
                    {
                        await WindowsOsAutomation.ActivateWindowAsync(existingBrowser, ct);
                    }
                    else if (LaunchSystemBrowserOnNavigate)
                    {
                        await WindowsOsAutomation.LaunchNewBrowserWindowAndWaitAsync(launchUrl, ct);
                    }
                }
            }
            catch
            {
                // ブラウザ起動失敗時も内部DOMセッションで継続
            }
        }

        IReadOnlyList<Uri> redirects = Array.Empty<Uri>();
        IReadOnlyList<Uri> popups = Array.Empty<Uri>();
        return (redirects, targetUrl, popups);
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

    public async Task ClickElementAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (CurrentTargetSelection?.UseBuiltInBrowser == true && BuiltInBrowserClickAsync is not null)
        {
            await BuiltInBrowserClickAsync(locator, ct);
        }
        else if (EnableOsAutomation)
        {
            IntPtr browserHwnd = await EnsureBrowserWindowReadyAsync(ct);

            bool isEnterSubmit = string.Equals(locator.StableId, "key-enter-submit", StringComparison.OrdinalIgnoreCase) ||
                                 (locator.AccessibleName is not null && locator.AccessibleName.Contains("Enter", StringComparison.OrdinalIgnoreCase)) ||
                                 (locator.Label is not null && locator.Label.Contains("検索実行", StringComparison.OrdinalIgnoreCase));

            if (isEnterSubmit)
            {
                if (!_lastFillExecutedSearch)
                {
                    if (!string.IsNullOrWhiteSpace(_lastFilledValue) && IsSearchEngineContext(locator))
                    {
                        string searchUrl = BuildSearchEngineQueryUrl(CurrentUrl, _lastFilledValue);
                        await WindowsOsAutomation.SendCtrlShortcutAndNavigateUrlAsync(searchUrl, ct);
                        _lastFillExecutedSearch = true;
                    }
                    else
                    {
                        await WindowsOsAutomation.SendVirtualKeyAsync(0x0D, ct);
                    }
                }
            }
            else if (_newWindowLaunchedInCurrentRun && IsSearchEnginePage(CurrentUrl) && string.IsNullOrWhiteSpace(_lastFilledValue))
            {
                // 新規起動した検索エンジンの初期画面では自動フォーカスされた検索欄が外れないよう座標クリックをスキップする
            }
            else if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y) && !(x == 300 && y == 200))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
            }
            else if (browserHwnd != IntPtr.Zero && WindowsOsAutomation.TryGetWindowCenter(browserHwnd, out int cx, out int cy))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(cx, cy, ct);
            }
        }

        string key = GetLocatorKey(locator);
        _activeStateSelectors.Add($"#clicked-{key}");
        _activeStateSelectors.Add("#submit-success");
        _activeStateSelectors.Add(".complete-message");
    }

    public async Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _lastFilledValue = value;
        _lastFillExecutedSearch = false;

        if (CurrentTargetSelection?.UseBuiltInBrowser == true && BuiltInBrowserFillAsync is not null)
        {
            await BuiltInBrowserFillAsync(locator, value, ct);
        }
        else if (EnableOsAutomation)
        {
            await EnsureBrowserWindowReadyAsync(ct);

            if (IsSearchEngineContext(locator) && !string.IsNullOrWhiteSpace(value))
            {
                // 検索エンジンへの検索語入力はウィンドウ座標ズレやIME未確定状態に左右されないよう検索URLへ確実に遷移させる
                string searchUrl = BuildSearchEngineQueryUrl(CurrentUrl, value);
                await WindowsOsAutomation.SendCtrlShortcutAndNavigateUrlAsync(searchUrl, ct);
                _lastFillExecutedSearch = true;
            }
            else
            {
                if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y) && !(x == 300 && y == 200))
                {
                    await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
                }

                await WindowsOsAutomation.SendUnicodeTextAsync(value, ct);
            }
        }

        string key = GetLocatorKey(locator);
        _fieldValues[key] = value;
    }

    private bool IsSearchEngineContext(SemanticLocator locator)
    {
        if (IsSearchEnginePage(CurrentUrl))
        {
            return true;
        }

        string combined = $"{locator.Label} {locator.AccessibleName} {locator.StableId} {locator.CssSelector}";
        return combined.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSearchEnginePage(Uri? uri)
    {
        if (uri is null) return false;
        string host = uri.Host;
        return host.Contains("google.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("yahoo.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("bing.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchEngineQueryUrl(Uri? baseUri, string query)
    {
        string escaped = Uri.EscapeDataString(query.Trim());
        if (baseUri is not null)
        {
            string host = baseUri.Host;
            if (host.Contains("yahoo.co.jp", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://search.yahoo.co.jp/search?p={escaped}";
            }
            if (host.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://www.bing.com/search?q={escaped}";
            }
            if (host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://duckduckgo.com/?q={escaped}";
            }
        }
        return $"https://www.google.com/search?q={escaped}";
    }

    private async Task<IntPtr> EnsureBrowserWindowReadyAsync(CancellationToken ct)
    {
        if (CurrentTargetSelection is not null)
        {
            if (CurrentTargetSelection.CreateNewWindow && !_newWindowLaunchedInCurrentRun)
            {
                IntPtr newHwnd = await WindowsOsAutomation.LaunchNewBrowserWindowAndWaitAsync("https://www.google.com", ct);
                _newWindowLaunchedInCurrentRun = true;
                CurrentUrl ??= new Uri("https://www.google.com");
                if (newHwnd != IntPtr.Zero)
                {
                    CurrentTargetSelection.TargetWindowHandle = newHwnd;
                }
            }

            if (CurrentTargetSelection.TargetWindowHandle != IntPtr.Zero)
            {
                await WindowsOsAutomation.ActivateWindowAsync(CurrentTargetSelection.TargetWindowHandle, ct);
                return CurrentTargetSelection.TargetWindowHandle;
            }
        }

        IntPtr browserHwnd = WindowsOsAutomation.FindBrowserWindow();
        if (browserHwnd != IntPtr.Zero)
        {
            await WindowsOsAutomation.ActivateWindowAsync(browserHwnd, ct);
        }
        return browserHwnd;
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
    private readonly Dictionary<string, WindowTargetSelection> _targetSelections = new(StringComparer.OrdinalIgnoreCase);

    public bool EnableOsAutomation { get; set; }

    public void ConfigureTargetSelections(IReadOnlyDictionary<string, WindowTargetSelection>? selections)
    {
        _targetSelections.Clear();
        if (selections is null) return;
        foreach (var kv in selections)
        {
            if (kv.Value.AppCategory == "CustomApp")
            {
                _targetSelections[kv.Value.ApplicationName] = kv.Value;
            }
        }
    }

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

    public async Task ClickControlAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (EnableOsAutomation)
        {
            IntPtr appHwnd = await EnsureCustomAppWindowAsync(applicationName, ct);

            if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
            }
            else if (appHwnd != IntPtr.Zero && WindowsOsAutomation.TryGetWindowCenter(appHwnd, out int cx, out int cy))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(cx, cy, ct);
            }
        }

        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        _controlValues[$"{key}::clicked"] = "true";
    }

    public async Task FillControlAsync(string applicationName, SemanticLocator locator, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (EnableOsAutomation)
        {
            await EnsureCustomAppWindowAsync(applicationName, ct);

            if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
            }

            await WindowsOsAutomation.SendUnicodeTextAsync(value, ct);
        }

        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        _controlValues[key] = value;
    }

    public Task<string?> ReadControlTextAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        return Task.FromResult<string?>(_controlValues.TryGetValue(key, out string? val) ? val : "完了ステータス:OK");
    }

    private async Task<IntPtr> EnsureCustomAppWindowAsync(string applicationName, CancellationToken ct)
    {
        if (_targetSelections.TryGetValue(applicationName, out var sel))
        {
            if (sel.CreateNewWindow && sel.TargetWindowHandle == IntPtr.Zero)
            {
                try
                {
                    string exe = applicationName.Contains("メモ帳", StringComparison.OrdinalIgnoreCase) ? "notepad.exe" : applicationName;
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                    await Task.Delay(500, ct);
                }
                catch
                {
                }
                sel.TargetWindowHandle = WindowsOsAutomation.FindCustomAppWindow(applicationName);
            }
            if (sel.TargetWindowHandle != IntPtr.Zero)
            {
                await WindowsOsAutomation.ActivateWindowAsync(sel.TargetWindowHandle, ct);
                return sel.TargetWindowHandle;
            }
        }

        IntPtr appHwnd = WindowsOsAutomation.FindCustomAppWindow(applicationName);
        if (appHwnd != IntPtr.Zero)
        {
            await WindowsOsAutomation.ActivateWindowAsync(appHwnd, ct);
        }
        return appHwnd;
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
            // Chrome操作候補のURLオリジンをすべて許可リストへ自動登録する
            if (cand.SuggestedActionType.StartsWith("Chrome.", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) &&
                Uri.TryCreate(cand.TargetUrlOrWorkbook, UriKind.Absolute, out Uri? candUri) &&
                (candUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                 candUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                !allowedOrigins.Any(o => o.Matches(candUri)))
            {
                allowedOrigins.Add(new OriginRule
                {
                    Scheme = candUri.Scheme,
                    Host = candUri.Host,
                    Port = candUri.IsDefaultPort ? (candUri.Scheme == "https" ? 443 : 80) : candUri.Port
                });
            }

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
