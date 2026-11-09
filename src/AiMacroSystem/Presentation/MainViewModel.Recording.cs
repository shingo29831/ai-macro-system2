// Module: @role: MainViewModelの実環境操作ログ記録およびAIマクロ生成オーケストレーションを担当するPartialクラス
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    public void StartEasyRecording()
    {
        _recordingService.StartRecording();
        RecordedEvents.Clear();
        RecordingProgressText = "記録中: 操作ログ（ブラウザ / Excel / 独自アプリ）を収集しています...";
        IsRecordingMacro = true;
        _liveLogCollector?.Start(_recordingService, ev =>
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                RecordedEvents.Add(ev);
                RecordingProgressText = $"記録中 ({RecordedEvents.Count} ログ): [{ev.ApplicationKind}] {ev.EventKind} ({ev.TargetLabel ?? ev.UrlOrWorkbookPath ?? ev.LogSource})";
            });
        });
        StatusBanner = "🔴 操作記録を開始しました。画面上部の「記録中」バーから記録終了すると自動でマクロに変換されます。";
    }

    public void StopEasyRecording()
    {
        StopEasyRecordingAndGenerateAsync().GetAwaiter().GetResult();
    }

    public async Task StopEasyRecordingAndGenerateAsync()
    {
        try
        {
            _liveLogCollector?.StopAndFlush();

            if (_recordingService.GetRecordedEvents().Count == 0)
            {
                RecordCrossAppBrowserToExcelLogs();
                RecordSimulatedCustomAppAction();
            }

            _recordingService.StopRecording();
            await GenerateMacroFromRecordingViaAiAsync();
            IsEditingMode = true;
            VisualBlocksUpdated?.Invoke();
            StatusBanner = $"✨ 複数アプリ（ブラウザ・Excel・独自アプリ）のログから {EasySteps.Count} ブロックのマクロを自動生成しました！「💾 マクロを保存」で保存できます。";
        }
        finally
        {
            IsRecordingMacro = false;
        }
    }

    public async Task GenerateMacroFromRecordingViaAiAsync()
    {
        var rawList = _recordingService.GetRecordedEvents();
        if (rawList.Count == 0)
        {
            StatusBanner = "⚠️ 先に操作を記録してください。";
            return;
        }

        var candidates = _normalizationService.Normalize(rawList);
        var policy = new PolicyDeclaration
        {
            AllowedOrigins = new List<OriginRule> { new() { Scheme = "https", Host = "example.com", Port = 443 } },
            AllowExternalAi = AllowExternalAiPolicy
        };

        if (_aiOrchestrator is not null)
        {
            var outcome = await _aiOrchestrator.GenerateAndValidateIrAsync(candidates, policy, AllowExternalAiPolicy, default);
            RefreshAllListsFromDatabase();

            if (outcome.IsAccepted && outcome.RawJson is not null)
            {
                EditorIrJson = outcome.RawJson;
                _currentSavedVersionId = null;
                _currentApprovalId = null;
                if (outcome.Definition is not null)
                {
                    WorkflowIdInput = outcome.Definition.WorkflowId;
                    WorkflowNameInput = outcome.Definition.Name;
                }
                RebuildEasyStepsFromIrJson(outcome.RawJson);
                ValidateCurrentEditorIr();
                IsEditingMode = true;
                VisualBlocksUpdated?.Invoke();
                StatusBanner = $"✨ 複数ログ（{rawList.Count}件）から {EasySteps.Count} 個の操作ブロックを抽出し、マクロ化しました！";
                return;
            }
            StatusBanner = $"⚠️ AI生成結果が安全基準でブロックされました: {outcome.ErrorReason}";
            return;
        }

        GenerateMacroFromRecordingSynchronously();
        IsEditingMode = true;
        VisualBlocksUpdated?.Invoke();
    }
}
