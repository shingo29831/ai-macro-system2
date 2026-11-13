// Module: @role: 低レベルキーボード打鍵・マウスクリックおよびクリップボード連携操作の抽出
using System;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed partial class WindowsLiveLogMonitor
{
    private void RecordMouseClickMultiLogsLocked(
        IntPtr targetHwnd,
        string appCategory,
        string appDisplayName,
        string winTitle,
        POINT pt)
    {
        if (_recordingService is null) return;

        // 新規クリックでターゲット情報が上書きされる前に未確定の打鍵バッファを直前ターゲットへフラッシュする
        DrainLowLevelKeysLocked(_lastAppCategory, _lastAppName);
        if (_typedBuffer.Length > 0)
        {
            FlushTypedBufferLocked(pressedEnter: false);
        }

        string ctrlText = ReadWindowTextSafe(targetHwnd);
        string className = ReadClassNameSafe(targetHwnd);
        int ctrlId = GetDlgCtrlID(targetHwnd);

        var (uiaName, uiaRole, _) = BrowserUiAutomationHelper.TryInspectFocusedOrPointElement(pt.X, pt.Y);

        // Chromiumの描画HWND名("Chrome Legacy Window")はUIAutomation名または座標付き識別子へ変換
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

        _lastAppCategory = appCategory;
        _lastAppName = appDisplayName;
        if (!string.IsNullOrWhiteSpace(winTitle))
        {
            _lastWindowTitle = winTitle;
        }
        if (appCategory == "Chrome")
        {
            string inferredUrl = InferBrowserUrlFromTitle(winTitle);
            if (!string.IsNullOrWhiteSpace(inferredUrl))
            {
                _lastBrowserUrl = inferredUrl;
            }
        }
        _lastClickX = pt.X;
        _lastClickY = pt.Y;
        _hasExplicitClickInCurrentWindow = true;
        _lastControlLabel = elementLabel;
        _hasEmittedEnterForLastInput = false;

        // 入力欄クリック時は直後の文字入力先としてターゲット情報を保持しフォーカスコンテキストを記録する
        if (uiaRole == "textbox")
        {
            var focusTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(50));
            string targetContext = appCategory == "Chrome"
                ? (!string.IsNullOrWhiteSpace(_lastBrowserUrl) ? _lastBrowserUrl : InferBrowserUrlFromTitle(winTitle))
                : appDisplayName;
            Emit(_recordingService.RecordEvent(
                appCategory, "Focus", "text", "textbox", elementLabel, elementLabel, stableId,
                targetContext, null, null, null,
                logSource: LogSourceConstants.UiAutomation,
                cssSelector: appCategory == "Chrome" ? $"#{stableId}" : null,
                coordinateX: pt.X, coordinateY: pt.Y,
                sourceConfidence: 0.90, windowOrProcessName: appDisplayName, timestampOverride: focusTs));
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

        // 日本語IME等で生打鍵バッファが空の時のみUIAutomationから補完
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
            // 入力記録直後にEnterが押された場合は検索・送信ステップを補完
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

            // 検索欄等でEnterキー押下時の実行ステップ
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
}
