// Module: @role: MainViewModelのシミュレーション記録・デモ用操作ログ生成を担当するPartialクラス
using System;
using System.IO;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    public void RecordCrossAppBrowserToExcelLogs()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTime = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));

        // 1. ブラウザ起動・遷移（WindowManagerのウィンドウ名からChromeを自動判定 + BrowserDomログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "WindowActivate", null, null, null, null,
            "https://example.com/portal", null, null, null, false, baseTime,
            LogSourceConstants.WindowManager, null, null, null, 0.6, "業務ポータル - Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Navigate", null, null, null, null,
            "https://example.com/portal", null, null, null, false, baseTime.AddMilliseconds(40),
            LogSourceConstants.BrowserDom, null, null, null, 0.98, "業務ポータル - Google Chrome"));

        // 2. ブラウザ担当者名入力（OsInputHookの途中打鍵 + UiAutomation + BrowserDom確定ログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Input", null, null, null, null,
            null, null, null, "山田", false, baseTime.AddSeconds(1),
            LogSourceConstants.OsInputHook, null, 320, 240, 0.3, "業務ポータル - Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Input", "textbox", "担当者名", null, null,
            null, null, null, "山田 太郎", false, baseTime.AddSeconds(1).AddMilliseconds(150),
            LogSourceConstants.UiAutomation, null, 320, 240, 0.88, "業務ポータル - Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Input", "textbox", "担当者名", "担当者名", "input-staff-name",
            "https://example.com/portal", null, null, "山田 太郎", false, baseTime.AddSeconds(1).AddMilliseconds(220),
            LogSourceConstants.BrowserDom, "#input-staff-name", 320, 240, 0.99, "業務ポータル - Google Chrome"));

        // 3. 照会ボタンクリック（OsInputHook座標ログ + BrowserDomセマンティックログ）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Click", null, null, null, null,
            null, null, null, null, false, baseTime.AddSeconds(2),
            LogSourceConstants.OsInputHook, null, 450, 310, 0.25, "業務ポータル - Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Click", "button", "照会する", "照会する", "btn-inquiry",
            "https://example.com/portal", null, null, null, false, baseTime.AddSeconds(2).AddMilliseconds(40),
            LogSourceConstants.BrowserDom, "#btn-inquiry", 450, 310, 0.99, "業務ポータル - Google Chrome"));

        // 4. ブラウザ上の照会結果をコピーしてExcelへ移動・貼り付け（アプリ横断データ転記）
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Copy", "table", "照会結果テーブル", "照会結果", "tbl-result",
            "https://example.com/portal", null, null, "担当者,結果\n山田 太郎,正常完了", false, baseTime.AddSeconds(3),
            LogSourceConstants.BrowserDom, "#tbl-result", 480, 360, 0.97, "業務ポータル - Google Chrome"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "WindowActivate", null, null, null, null,
            StepExcelPathInput, "Sheet1", "A1:B2", null, false, baseTime.AddSeconds(4),
            LogSourceConstants.WindowManager, null, null, null, 0.8, "AiMacroSample.xlsx - Excel"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "SelectionChange", null, null, null, null,
            StepExcelPathInput, "Sheet1", "A1:B2", null, false, baseTime.AddSeconds(4).AddMilliseconds(40),
            LogSourceConstants.ExcelCom, null, null, null, 0.85, "AiMacroSample.xlsx - Excel"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Paste", null, null, null, null,
            null, null, null, null, false, baseTime.AddSeconds(4).AddMilliseconds(90),
            LogSourceConstants.OsInputHook, null, 200, 180, 0.4, "AiMacroSample.xlsx - Excel"));
        RecordAndTrack(new RawRecordedEvent(
            $"evt-{Guid.NewGuid():N}", "Auto", "Write", null, null, null, null,
            StepExcelPathInput, "Sheet1", "A1:B2", null, false, baseTime.AddSeconds(4).AddMilliseconds(130),
            LogSourceConstants.ExcelCom, null, 200, 180, 0.99, "AiMacroSample.xlsx - Excel"));
    }

    private void RecordAndTrack(RawRecordedEvent ev)
    {
        var recorded = _recordingService.RecordMultiSourceBatch(new[] { ev });
        foreach (var item in recorded)
        {
            RecordedEvents.Add(item);
        }
    }

    public void RecordSimulatedWebOpen()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        var evNoise = _recordingService.RecordEvent(
            "Chrome", "WindowActivate", null, null, null, null, null,
            StepUrlInput, null, null, null,
            logSource: LogSourceConstants.WindowManager, sourceConfidence: 0.5, timestampOverride: baseTs);
        var evDom = _recordingService.RecordEvent(
            "Chrome", "Navigate", null, null, null, null, null,
            StepUrlInput, null, null, null,
            logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.99, timestampOverride: baseTs.AddMilliseconds(30));

        RecordedEvents.Add(evNoise);
        RecordedEvents.Add(evDom);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): Webページ遷移 ({StepUrlInput})";
        StatusBanner = $"🔴 Webページ移動（{evDom.UrlOrWorkbookPath}）の複数ログを記録しました。";
    }

    public void RecordSimulatedWebInput(bool asPassword)
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        string targetName = asPassword ? "Password" : StepElementNameInput;
        string targetLabel = asPassword ? "パスワード" : StepElementNameInput;
        string rawVal = asPassword ? "SecretPass123!" : StepFillValueInput;

        var evHook = _recordingService.RecordEvent(
            "Chrome", "Input", asPassword ? "password" : "text", null, null, null, null,
            null, null, null, rawVal[..Math.Min(2, rawVal.Length)],
            logSource: LogSourceConstants.OsInputHook, coordinateX: 300, coordinateY: 220,
            sourceConfidence: 0.3, timestampOverride: baseTs);
        var evUia = _recordingService.RecordEvent(
            "Chrome", "Input", asPassword ? "password" : "text", "textbox", targetName, null, null,
            StepUrlInput, null, null, rawVal,
            logSource: LogSourceConstants.UiAutomation, coordinateX: 300, coordinateY: 220,
            sourceConfidence: 0.88, timestampOverride: baseTs.AddMilliseconds(60));
        var evDom = _recordingService.RecordEvent(
            "Chrome", "Input", asPassword ? "password" : "text", "textbox", targetName, targetLabel, $"field-{targetName}",
            StepUrlInput, null, null, rawVal,
            logSource: LogSourceConstants.BrowserDom, cssSelector: $"#field-{targetName}",
            sourceConfidence: 0.99, timestampOverride: baseTs.AddMilliseconds(110));

        RecordedEvents.Add(evHook);
        RecordedEvents.Add(evUia);
        RecordedEvents.Add(evDom);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): Web文字入力 ({targetLabel})";
        StatusBanner = asPassword
            ? "🔒 パスワード入力を検知したため、値を自動的に [REDACTED] にマスキング保護して記録しました。"
            : $"🔴 文字入力（{evDom.TargetLabel}: {evDom.CapturedValue}）の複数ログを記録しました。";
    }

    public void RecordSimulatedExcelWrite()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        var evSelect = _recordingService.RecordEvent(
            "Excel", "SelectionChange", null, null, null, null, null,
            StepExcelPathInput, StepExcelSheetInput, StepExcelRangeInput, null,
            logSource: LogSourceConstants.ExcelCom, sourceConfidence: 0.7, timestampOverride: baseTs);
        var evWrite = _recordingService.RecordEvent(
            "Excel", "Write", null, null, null, null, null,
            StepExcelPathInput, StepExcelSheetInput, StepExcelRangeInput, StepExcelWriteTextInput,
            logSource: LogSourceConstants.ExcelCom, sourceConfidence: 0.99, timestampOverride: baseTs.AddMilliseconds(50));

        RecordedEvents.Add(evSelect);
        RecordedEvents.Add(evWrite);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): Excel書込 ({StepExcelSheetInput}!{StepExcelRangeInput})";
        StatusBanner = $"🔴 Excel書き込み（{evWrite.SheetName}!{evWrite.RangeAddress}）の複数ログを記録しました。";
    }

    public void RecordSimulatedCustomAppAction()
    {
        if (!_recordingService.IsRecording) _recordingService.StartRecording();
        var baseTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(500));
        string appName = string.IsNullOrWhiteSpace(StepCustomAppNameInput) ? "基幹業務システム" : StepCustomAppNameInput.Trim();
        string controlName = string.IsNullOrWhiteSpace(StepElementNameInput) ? "受注番号" : StepElementNameInput.Trim();
        string fillVal = string.IsNullOrWhiteSpace(StepFillValueInput) ? "ORD-2026-001" : StepFillValueInput;

        // 複数ログソース（OsInputHook + UiAutomation + CustomAppHook）から独自アプリ操作を記録
        var evHook = _recordingService.RecordEvent(
            "CustomApp", "Input", null, null, null, null, null,
            appName, null, null, fillVal[..Math.Min(3, fillVal.Length)],
            logSource: LogSourceConstants.OsInputHook, coordinateX: 280, coordinateY: 190,
            sourceConfidence: 0.3, windowOrProcessName: appName, timestampOverride: baseTs);
        var evUia = _recordingService.RecordEvent(
            "CustomApp", "Input", "text", "textbox", controlName, controlName, null,
            appName, null, null, fillVal,
            logSource: LogSourceConstants.UiAutomation, coordinateX: 280, coordinateY: 190,
            sourceConfidence: 0.90, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(80));
        var evApp = _recordingService.RecordEvent(
            "CustomApp", "Input", "text", "textbox", controlName, controlName, $"ctrl-{controlName}",
            appName, null, null, fillVal,
            logSource: LogSourceConstants.CustomAppHook,
            sourceConfidence: 0.99, windowOrProcessName: appName, timestampOverride: baseTs.AddMilliseconds(120));

        RecordedEvents.Add(evHook);
        RecordedEvents.Add(evUia);
        RecordedEvents.Add(evApp);
        RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): 独自アプリ操作 ({appName})";
        StatusBanner = $"🔴 独自アプリ「{appName}」の複数ログ（OSフック・UIAutomation・アプリログ）を記録しました。";
    }
}
