// Module: @role: 複数ログ抽出エンジン用の定数定義、抽出オプション設定、クラスタリングおよびセッションコンテキスト状態モデル
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public static class LogSourceConstants
{
    public const string BrowserDom = "BrowserDom";
    public const string UiAutomation = "UiAutomation";
    public const string ExcelCom = "ExcelCom";
    public const string CustomAppHook = "CustomAppHook";
    public const string OsInputHook = "OsInputHook";
    public const string WindowManager = "WindowManager";
    public const string SystemAudit = "SystemAudit";
    public const string PrimaryHook = "PrimaryHook";
}

public sealed class MultiLogExtractionOptions
{
    public TimeSpan CrossSourceCorrelationWindow { get; init; } = TimeSpan.FromMilliseconds(1800);
    public TimeSpan IncrementalInputDebounceWindow { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DuplicateClickSuppressionWindow { get; init; } = TimeSpan.FromMilliseconds(500);
}

public enum IntentPriorityKind
{
    Noise = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Essential = 4
}

public sealed class LogChunkingOptions
{
    public const int DefaultMaxTokensPerChunk = 2400;

    public int MaxTokensPerChunk { get; init; } = DefaultMaxTokensPerChunk;
    public TimeSpan IdleBoundaryWindow { get; init; } = TimeSpan.FromSeconds(30);
    public bool SplitOnApplicationBoundary { get; init; } = true;
}

public sealed record ScoredActionCandidate(
    NormalizedActionCandidate Candidate,
    int OriginalIndex,
    IntentPriorityKind PriorityKind,
    double ImportanceScore,
    int EstimatedTokens,
    bool IsEssentialIntent);

public sealed record LogChunk(
    string ChunkId,
    int SequenceIndex,
    int EstimatedTokens,
    string AppCategory,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    IReadOnlyList<string> RawEventIds,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<NormalizedActionCandidate> Candidates,
    int FilteredOutEventCount = 0);

public sealed record LogFilterSummary(
    IReadOnlyList<RawRecordedEvent> KeptRawEvents,
    IReadOnlyList<NormalizedActionCandidate> KeptCandidates,
    int OriginalRawEventCount,
    int FilteredOutEventCount,
    int OriginalEstimatedTokens,
    int FilteredEstimatedTokens);

internal sealed class EventCluster
{
    public string AppCategory { get; }
    public string IntentKind { get; private set; }
    public List<RawRecordedEvent> Events { get; } = new();
    public List<string> ExtraSourceEventIds { get; } = new();
    public DateTimeOffset LastTimestamp { get; private set; }

    public EventCluster(
        string appCategory,
        string intentKind,
        RawRecordedEvent initialEvent,
        IReadOnlyList<string>? extraSourceEventIds = null)
    {
        AppCategory = appCategory;
        IntentKind = intentKind;
        Events.Add(initialEvent);
        if (extraSourceEventIds is { Count: > 0 })
        {
            ExtraSourceEventIds.AddRange(extraSourceEventIds);
        }
        LastTimestamp = initialEvent.Timestamp;
    }

    public void AddEvent(
        RawRecordedEvent ev,
        string updatedIntentKind,
        IReadOnlyList<string>? extraSourceEventIds = null)
    {
        IntentKind = updatedIntentKind;
        Events.Add(ev);
        if (extraSourceEventIds is { Count: > 0 })
        {
            ExtraSourceEventIds.AddRange(extraSourceEventIds);
        }
        if (ev.Timestamp > LastTimestamp)
        {
            LastTimestamp = ev.Timestamp;
        }
    }
}

internal sealed class ActiveSessionContext
{
    private readonly List<string> _pendingTransferEventIds = new();

    public string? LastAppCategory { get; private set; }
    public string? LastBrowserUrl { get; private set; }
    public string? LastExcelWorkbookPath { get; private set; }
    public string? LastExcelSheetName { get; private set; }
    public string? LastExcelRangeAddress { get; private set; }
    public string? LastCustomAppName { get; private set; }
    public string? LastFocusedRole { get; private set; }
    public string? LastFocusedName { get; private set; }
    public string? LastFocusedLabel { get; private set; }
    public string? LastFocusedStableId { get; private set; }
    public string? LastClipboardValue { get; private set; }

    public void AbsorbContextFrom(RawRecordedEvent ev)
    {
        if (IsClipboardCopyEvent(ev.EventKind))
        {
            if (!string.IsNullOrWhiteSpace(ev.EventId))
            {
                _pendingTransferEventIds.Add(ev.EventId);
            }
            if (!string.IsNullOrEmpty(ev.CapturedValue))
            {
                LastClipboardValue = ev.CapturedValue;
            }
        }
        else if (!string.IsNullOrEmpty(ev.CapturedValue) &&
                 (ev.EventKind.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
                  ev.EventKind.Equals("ReadRange", StringComparison.OrdinalIgnoreCase) ||
                  ev.EventKind.Equals("ReadText", StringComparison.OrdinalIgnoreCase) ||
                  ev.EventKind.Equals("Extract", StringComparison.OrdinalIgnoreCase)))
        {
            LastClipboardValue = ev.CapturedValue;
        }

        if (!string.IsNullOrWhiteSpace(ev.WindowOrProcessName))
        {
            string winCategory = MultiLogExtractionEngine.ClassifyWindowOrProcessName(ev.WindowOrProcessName);
            if (winCategory == "Chrome")
            {
                LastAppCategory = "Chrome";
            }
            else if (winCategory == "Excel")
            {
                LastAppCategory = "Excel";
            }
            else
            {
                LastCustomAppName = ev.WindowOrProcessName;
                LastAppCategory = "CustomApp";
            }
        }

        if (!string.IsNullOrWhiteSpace(ev.UrlOrWorkbookPath))
        {
            if (Uri.TryCreate(ev.UrlOrWorkbookPath, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                LastBrowserUrl = ev.UrlOrWorkbookPath;
                LastAppCategory = "Chrome";
            }
            else if (!string.IsNullOrWhiteSpace(ev.SheetName) ||
                     ev.ApplicationKind.Equals("Excel", StringComparison.OrdinalIgnoreCase) ||
                     ev.LogSource.Equals(LogSourceConstants.ExcelCom, StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(ev.UrlOrWorkbookPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(ev.UrlOrWorkbookPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                LastExcelWorkbookPath = ev.UrlOrWorkbookPath;
                LastAppCategory = "Excel";
            }
            else if (ev.ApplicationKind.Equals("CustomApp", StringComparison.OrdinalIgnoreCase) ||
                     ev.ApplicationKind.Equals("DesktopApp", StringComparison.OrdinalIgnoreCase))
            {
                LastCustomAppName = ev.UrlOrWorkbookPath;
                LastAppCategory = "CustomApp";
            }
        }

        if (!string.IsNullOrWhiteSpace(ev.SheetName)) LastExcelSheetName = ev.SheetName;
        if (!string.IsNullOrWhiteSpace(ev.RangeAddress)) LastExcelRangeAddress = ev.RangeAddress;

        if (MultiLogExtractionEngine.IsNoiseOrContextOnlyEvent(ev))
        {
            if (!string.IsNullOrWhiteSpace(ev.TargetRole)) LastFocusedRole = ev.TargetRole;
            if (!string.IsNullOrWhiteSpace(ev.TargetName)) LastFocusedName = ev.TargetName;
            if (!string.IsNullOrWhiteSpace(ev.TargetLabel)) LastFocusedLabel = ev.TargetLabel;
            if (!string.IsNullOrWhiteSpace(ev.TargetStableId)) LastFocusedStableId = ev.TargetStableId;
        }
        else
        {
            LastFocusedRole = null;
            LastFocusedName = null;
            LastFocusedLabel = null;
            LastFocusedStableId = null;
        }
    }

    public (RawRecordedEvent Enriched, IReadOnlyList<string> TransferSourceIds) EnrichEventAndConsumeTransfer(
        RawRecordedEvent ev,
        string appCategory,
        string intentKind)
    {
        string? urlOrBook = ev.UrlOrWorkbookPath;
        string? sheet = ev.SheetName;
        string? range = ev.RangeAddress;
        string? winName = ev.WindowOrProcessName;

        if (appCategory == "Chrome" && string.IsNullOrWhiteSpace(urlOrBook) &&
            !ev.EventKind.Equals("Navigate", StringComparison.OrdinalIgnoreCase))
        {
            urlOrBook = LastBrowserUrl;
        }
        else if (appCategory == "Excel")
        {
            if (string.IsNullOrWhiteSpace(urlOrBook)) urlOrBook = LastExcelWorkbookPath;
            if (string.IsNullOrWhiteSpace(sheet)) sheet = LastExcelSheetName;
            if (string.IsNullOrWhiteSpace(range)) range = LastExcelRangeAddress;
        }
        else if (appCategory == "CustomApp")
        {
            if (string.IsNullOrWhiteSpace(winName)) winName = LastCustomAppName;
            if (string.IsNullOrWhiteSpace(urlOrBook)) urlOrBook = winName;
        }

        // OS入力フック等で意味的識別子欠落時は直前のFocus対象を補完
        bool lacksIdentity = !MultiLogExtractionEngine.HasAnySemanticIdentity(ev) && !ev.CoordinateX.HasValue;
        string? role = ev.TargetRole ?? (lacksIdentity ? LastFocusedRole : null);
        string? name = ev.TargetName ?? (lacksIdentity ? LastFocusedName : null);
        string? label = ev.TargetLabel ?? (lacksIdentity ? LastFocusedLabel : null);
        string? stableId = ev.TargetStableId ?? (lacksIdentity ? LastFocusedStableId : null);

        // アプリ間転記（コピー＆ペースト）の自動補完とソースイベントID引き継ぎ
        string? capturedValue = ev.CapturedValue;
        bool isPasteEvent = ev.EventKind.Equals("Paste", StringComparison.OrdinalIgnoreCase) ||
                            ev.EventKind.Equals("PasteRange", StringComparison.OrdinalIgnoreCase) ||
                            ev.EventKind.Equals("ClipboardPaste", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(capturedValue) &&
            !string.IsNullOrEmpty(LastClipboardValue) &&
            (isPasteEvent || (_pendingTransferEventIds.Count > 0 && intentKind is "Write" or "Input")))
        {
            capturedValue = LastClipboardValue;
        }

        IReadOnlyList<string> consumedTransferIds = Array.Empty<string>();
        if (_pendingTransferEventIds.Count > 0 &&
            (isPasteEvent || (!string.IsNullOrEmpty(LastClipboardValue) && string.Equals(capturedValue, LastClipboardValue, StringComparison.Ordinal))))
        {
            consumedTransferIds = _pendingTransferEventIds.ToList();
            _pendingTransferEventIds.Clear();
        }

        var enriched = ev with
        {
            TargetRole = role,
            TargetName = name,
            TargetLabel = label,
            TargetStableId = stableId,
            UrlOrWorkbookPath = urlOrBook,
            SheetName = sheet,
            RangeAddress = range,
            WindowOrProcessName = winName,
            CapturedValue = capturedValue
        };

        return (enriched, consumedTransferIds);
    }

    private static bool IsClipboardCopyEvent(string? eventKind) =>
        !string.IsNullOrWhiteSpace(eventKind) &&
        (eventKind.Equals("Copy", StringComparison.OrdinalIgnoreCase) ||
         eventKind.Equals("ClipboardCopy", StringComparison.OrdinalIgnoreCase) ||
         eventKind.Equals("ClipboardUpdate", StringComparison.OrdinalIgnoreCase));
}
