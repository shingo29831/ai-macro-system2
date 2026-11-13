// Module: @role: MainViewModelの実環境操作ログ記録およびAIマクロ生成オーケストレーションを担当するPartialクラス
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    private int _lastChunkCount;
    public int LastChunkCount
    {
        get => _lastChunkCount;
        private set { _lastChunkCount = value; OnPropertyChanged(); }
    }

    private int _lastEstimatedTokens;
    public int LastEstimatedTokens
    {
        get => _lastEstimatedTokens;
        private set { _lastEstimatedTokens = value; OnPropertyChanged(); }
    }

    private int _lastFilteredOutEventCount;
    public int LastFilteredOutEventCount
    {
        get => _lastFilteredOutEventCount;
        private set { _lastFilteredOutEventCount = value; OnPropertyChanged(); }
    }

    private string _chunkAndFilterSummaryText = string.Empty;
    public string ChunkAndFilterSummaryText
    {
        get => _chunkAndFilterSummaryText;
        private set { _chunkAndFilterSummaryText = value; OnPropertyChanged(); }
    }

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
            _recordingService.StopRecording();

            if (_recordingService.GetRecordedEvents().Count == 0)
            {
                RecordingProgressText = "操作ログが記録されていません";
                StatusBanner = "⚠️ 操作ログが記録されていません。";
                return;
            }

            await GenerateMacroFromRecordingViaAiAsync();
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
            RecordingProgressText = "操作ログが記録されていません";
            StatusBanner = "⚠️ 操作ログが記録されていません。";
            return;
        }

        var recentRawList = _logStore is not null
            ? _logStore.FilterRecentEvents(rawList)
            : rawList;

        var (candidates, chunks) = _normalizationService.NormalizeAndChunk(recentRawList);
        var filterSummary = _normalizationService.LastFilterSummary;
        LastChunkCount = Math.Max(1, chunks.Count);
        LastEstimatedTokens = filterSummary?.FilteredEstimatedTokens ?? TokenEstimator.EstimateTotalTokens(candidates);
        LastFilteredOutEventCount = filterSummary?.FilteredOutEventCount ?? 0;
        ChunkAndFilterSummaryText = $"チャンク分割: {LastChunkCount}個 / 推定トークン: {LastEstimatedTokens} / フィルタ除外: {LastFilteredOutEventCount}件";
        RecordingProgressText = $"解析完了 ({rawList.Count}件 -> 有効候補{candidates.Count}件, {ChunkAndFilterSummaryText})";

        var allowedOrigins = new List<OriginRule>();
        var allowedExcelDirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(AllowedOriginHostInput))
        {
            TryRegisterHttpOrigin(AllowedOriginHostInput, allowedOrigins);
        }
        if (!string.IsNullOrWhiteSpace(AllowedExcelDirectoryInput))
        {
            TryRegisterExcelDirectory(AllowedExcelDirectoryInput, allowedExcelDirs);
        }
        if (!string.IsNullOrWhiteSpace(StepExcelPathInput))
        {
            TryRegisterExcelDirectory(StepExcelPathInput, allowedExcelDirs);
        }

        foreach (var ev in recentRawList)
        {
            TryRegisterHttpOrigin(ev.UrlOrWorkbookPath, allowedOrigins);
            TryRegisterExcelDirectory(ev.UrlOrWorkbookPath, allowedExcelDirs);
        }

        foreach (var cand in candidates)
        {
            TryRegisterHttpOrigin(cand.TargetUrlOrWorkbook, allowedOrigins);
            TryRegisterExcelDirectory(cand.TargetUrlOrWorkbook, allowedExcelDirs);
        }

        if (_liveBrowserDriver?.CurrentUrl is not null)
        {
            TryRegisterHttpOrigin(_liveBrowserDriver.CurrentUrl.ToString(), allowedOrigins);
        }

        bool allowCoordinateClick = AllowCoordinateClickPolicy ||
            candidates.Any(c => c.Locator is not null && c.Locator.UsesCoordinatesOnly());

        var policy = new PolicyDeclaration
        {
            AllowedOrigins = allowedOrigins,
            AllowCoordinateClick = allowCoordinateClick,
            AllowExternalAi = AllowExternalAiPolicy,
            AllowedExcelDirectories = allowedExcelDirs
        };

        if (_aiOrchestrator is not null)
        {
            var outcome = await _aiOrchestrator.GenerateAndValidateIrAsync(
                candidates,
                policy,
                AllowExternalAiPolicy,
                default,
                recentRawList);
            LastChunkCount = Math.Max(LastChunkCount, outcome.TotalChunksCount);
            if (outcome.EstimatedTokens > 0) LastEstimatedTokens = outcome.EstimatedTokens;
            LastFilteredOutEventCount = Math.Max(LastFilteredOutEventCount, outcome.FilteredOutEventCount);
            ChunkAndFilterSummaryText = $"チャンク分割: {LastChunkCount}個 / 推定トークン: {LastEstimatedTokens} / フィルタ除外: {LastFilteredOutEventCount}件";

            _repository.PruneOldOperationLogs();
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
                StatusBanner = $"✨ 最近の操作ログ（{recentRawList.Count}件）から {EasySteps.Count} 個の操作ブロックを抽出し、マクロ化しました！（{ChunkAndFilterSummaryText}）";
                return;
            }

            string chunkHint = !string.IsNullOrWhiteSpace(outcome.FailedChunkId)
                ? $" [失敗チャンク: {outcome.FailedChunkId}]"
                : string.Empty;
            StatusBanner = $"⚠️ AI生成結果が安全基準でブロックされました{chunkHint}: {outcome.ErrorReason}";
            return;
        }

        GenerateMacroFromRecordingSynchronously();
        IsEditingMode = true;
        VisualBlocksUpdated?.Invoke();
    }
}
