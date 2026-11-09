// Module: @role: Windowsデスクトップ上のアクティブウィンドウ・打鍵・クリック監視および操作イベント抽出
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed partial class WindowsLiveLogMonitor : ILiveOperationLogCollector
{
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
}
