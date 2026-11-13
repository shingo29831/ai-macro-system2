// Module: @role: 複数ログソースからのノイズ除去・時間窓相関クラスタリング・信頼度スコアリングによる汎用高精度マクロ抽出エンジン
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;


public sealed partial class MultiLogExtractionEngine
{
    private static readonly HashSet<string> _noiseEventKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "MouseMove", "Hover", "MouseOver", "MouseEnter", "MouseLeave",
        "MouseDown", "MouseUp", "KeyDown", "KeyUp",
        "Focus", "Blur", "GotFocus", "LostFocus",
        "WindowFocus", "WindowActivate", "WindowActivated",
        "Scroll", "Heartbeat", "TelemetryPing", "SelectionChange", "SheetActivate",
        "Copy", "ClipboardCopy", "ClipboardUpdate"
    };

    private readonly MultiLogExtractionOptions _options;

    public MultiLogExtractionEngine(MultiLogExtractionOptions? options = null)
    {
        _options = options ?? new MultiLogExtractionOptions();
    }

    public IReadOnlyList<NormalizedActionCandidate> ExtractCandidates(IReadOnlyList<RawRecordedEvent> rawEvents)
    {
        var (candidates, _) = ExtractCandidatesWithSummary(rawEvents);
        return candidates;
    }

    public (IReadOnlyList<NormalizedActionCandidate> Candidates, LogFilterSummary Summary) ExtractCandidatesWithSummary(
        IReadOnlyList<RawRecordedEvent> rawEvents)
    {
        if (rawEvents.Count == 0)
        {
            var emptySummary = new LogFilterSummary(
                Array.Empty<RawRecordedEvent>(),
                Array.Empty<NormalizedActionCandidate>(),
                0,
                0,
                0,
                0);
            return (Array.Empty<NormalizedActionCandidate>(), emptySummary);
        }

        int originalTokens = TokenEstimator.EstimateTotalTokens(rawEvents);
        var (cleanedEvents, preFilteredNoiseCount) = LogFilterService.FilterNoiseRawEvents(
            rawEvents,
            _options.IncrementalInputDebounceWindow);

        // 元の出現順序を保ちつつタイムスタンプ順に整列
        var orderedEvents = cleanedEvents
            .Select((ev, index) => (Event: ev, Index: index))
            .OrderBy(x => x.Event.Timestamp)
            .ThenBy(x => x.Index)
            .Select(x => x.Event)
            .ToList();

        var context = new ActiveSessionContext();
        var clusters = new List<EventCluster>();

        foreach (var ev in orderedEvents)
        {
            if (IsNoiseOrContextOnlyEvent(ev))
            {
                context.AbsorbContextFrom(ev);
                continue;
            }

            string appCategory = ResolveApplicationCategory(ev, context);
            string intentKind = NormalizeIntentKind(ev.EventKind, appCategory);
            var (enrichedEvent, transferSourceIds) = context.EnrichEventAndConsumeTransfer(ev, appCategory, intentKind);
            context.AbsorbContextFrom(enrichedEvent);

            if (TryMergeIntoRecentCluster(clusters, enrichedEvent, appCategory, intentKind, transferSourceIds))
            {
                continue;
            }

            clusters.Add(new EventCluster(appCategory, intentKind, enrichedEvent, transferSourceIds));
        }

        var results = new List<NormalizedActionCandidate>(clusters.Count);
        foreach (var cluster in clusters)
        {
            results.Add(SynthesizeCandidate(cluster));
        }

        var usedRawIds = results
            .SelectMany(c => c.SourceRawEventIds)
            .ToHashSet(StringComparer.Ordinal);
        int totalFilteredOut = Math.Max(preFilteredNoiseCount, rawEvents.Count - usedRawIds.Count);
        int candidateTokens = TokenEstimator.EstimateTotalTokens(results);

        var summary = new LogFilterSummary(
            KeptRawEvents: cleanedEvents,
            KeptCandidates: results,
            OriginalRawEventCount: rawEvents.Count,
            FilteredOutEventCount: totalFilteredOut,
            OriginalEstimatedTokens: originalTokens,
            FilteredEstimatedTokens: candidateTokens);

        return (results, summary);
    }

    internal static bool IsNoiseOrContextOnlyEvent(RawRecordedEvent ev) =>
        string.IsNullOrWhiteSpace(ev.EventKind) || _noiseEventKinds.Contains(ev.EventKind.Trim());

    private static string ResolveApplicationCategory(RawRecordedEvent ev, ActiveSessionContext context)
    {
        string rawApp = (ev.ApplicationKind ?? string.Empty).Trim();

        if (rawApp.Equals("Chrome", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("Browser", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("Edge", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("Web", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals(LogSourceConstants.BrowserDom, StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (rawApp.Equals("Excel", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("Spreadsheet", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals(LogSourceConstants.ExcelCom, StringComparison.OrdinalIgnoreCase))
        {
            return "Excel";
        }

        if (rawApp.Equals("CustomApp", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("DesktopApp", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("NativeApp", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals("ErpApp", StringComparison.OrdinalIgnoreCase) ||
            rawApp.Equals(LogSourceConstants.CustomAppHook, StringComparison.OrdinalIgnoreCase))
        {
            return "CustomApp";
        }

        // アプリ種別が "Auto" や未指定、またはOS横断フックの場合はURL・ブック・シート・ウィンドウ名・セレクターから自動判定する
        if (!string.IsNullOrWhiteSpace(ev.UrlOrWorkbookPath))
        {
            if (Uri.TryCreate(ev.UrlOrWorkbookPath, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                return "Chrome";
            }

            string ext = Path.GetExtension(ev.UrlOrWorkbookPath);
            if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return "Excel";
            }
        }

        if (!string.IsNullOrWhiteSpace(ev.SheetName) || !string.IsNullOrWhiteSpace(ev.RangeAddress))
        {
            return "Excel";
        }

        if (!string.IsNullOrWhiteSpace(ev.CssSelector))
        {
            return "Chrome";
        }

        if (!string.IsNullOrWhiteSpace(ev.WindowOrProcessName))
        {
            string detectedFromWin = ClassifyWindowOrProcessName(ev.WindowOrProcessName);
            if (!string.IsNullOrWhiteSpace(detectedFromWin))
            {
                return detectedFromWin;
            }
        }

        return !string.IsNullOrWhiteSpace(context.LastAppCategory) ? context.LastAppCategory : "CustomApp";
    }

    public static string ClassifyWindowOrProcessName(string? windowOrProcessName)
    {
        if (string.IsNullOrWhiteSpace(windowOrProcessName))
        {
            return string.Empty;
        }

        string text = windowOrProcessName.Trim();
        if (text.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ブラウザ", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (text.Contains("excel", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return "Excel";
        }

        return "CustomApp";
    }

    private static string NormalizeIntentKind(string eventKind, string appCategory)
    {
        string k = (eventKind ?? string.Empty).Trim();

        if (k.Equals("Navigate", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("OpenUrl", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("PageLoad", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("GoTo", StringComparison.OrdinalIgnoreCase))
        {
            return "Navigate";
        }

        if (k.Equals("Submit", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("FormSubmit", StringComparison.OrdinalIgnoreCase))
        {
            return "Submit";
        }

        if (k.Equals("Input", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Fill", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Type", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("KeyPress", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("SetText", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Change", StringComparison.OrdinalIgnoreCase))
        {
            return appCategory == "Excel" ? "Write" : "Input";
        }

        if (k.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("WriteRange", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("SetCell", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("EditCell", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Paste", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("PasteRange", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("ClipboardPaste", StringComparison.OrdinalIgnoreCase))
        {
            return appCategory == "Excel" ? "Write" : "Input";
        }

        if (k.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("ReadRange", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("CopyRange", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("ReadText", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("GetText", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Extract", StringComparison.OrdinalIgnoreCase))
        {
            return "Read";
        }

        return "Click";
    }

    private bool TryMergeIntoRecentCluster(
        List<EventCluster> clusters,
        RawRecordedEvent current,
        string currentAppCategory,
        string currentIntentKind,
        IReadOnlyList<string> transferSourceIds)
    {
        // 複数ログソースの非同期到着・微小な時刻前後に対応するため時間窓内の直近クラスタを後方探索する
        for (int i = clusters.Count - 1; i >= 0; i--)
        {
            var candidateCluster = clusters[i];
            TimeSpan age = (current.Timestamp - candidateCluster.LastTimestamp).Duration();
            if (age > _options.IncrementalInputDebounceWindow)
            {
                break;
            }

            if (TryMergeIntoCluster(candidateCluster, current, currentAppCategory, currentIntentKind, transferSourceIds))
            {
                return true;
            }
        }
        return false;
    }

    private bool TryMergeIntoCluster(
        EventCluster prev,
        RawRecordedEvent current,
        string currentAppCategory,
        string currentIntentKind,
        IReadOnlyList<string> transferSourceIds)
    {
        if (!string.Equals(prev.AppCategory, currentAppCategory, StringComparison.Ordinal))
        {
            return false;
        }

        TimeSpan elapsed = (current.Timestamp - prev.LastTimestamp).Duration();

        // 1. 同一入力ターゲットへの連続打鍵・段階的入力・上書き編集を最終確定値へ畳み込む
        if (prev.IntentKind == "Input" && currentIntentKind == "Input" &&
            elapsed <= _options.IncrementalInputDebounceWindow &&
            AreTargetsCompatibleForMerge(prev, current, requireExplicitMatchWhenBothHaveIdentity: true))
        {
            prev.AddEvent(current, currentIntentKind, transferSourceIds);
            return true;
        }

        // 2. Excelの同一ブック・シート・セル範囲への連続書き込みログを統合する
        if (prev.IntentKind == "Write" && currentIntentKind == "Write" &&
            elapsed <= _options.IncrementalInputDebounceWindow &&
            AreExcelTargetsCompatible(prev, current))
        {
            prev.AddEvent(current, currentIntentKind, transferSourceIds);
            return true;
        }

        // 3. 複数ログソース間（例: OsInputHook + UiAutomation + BrowserDom/CustomAppHook）の同一操作を統合する
        bool compatibleIntents = string.Equals(prev.IntentKind, currentIntentKind, StringComparison.Ordinal)
            || (prev.IntentKind == "Click" && currentIntentKind == "Submit")
            || (prev.IntentKind == "Submit" && currentIntentKind == "Click");

        if (compatibleIntents && elapsed <= _options.CrossSourceCorrelationWindow)
        {
            bool differentLogSources = !prev.Events.Any(e =>
                string.Equals(e.LogSource, current.LogSource, StringComparison.OrdinalIgnoreCase));

            bool isDuplicateChatter = elapsed <= _options.DuplicateClickSuppressionWindow &&
                AreTargetsExplicitlyIdentical(prev, current);

            if ((differentLogSources || isDuplicateChatter) &&
                AreTargetsCompatibleForMerge(prev, current, requireExplicitMatchWhenBothHaveIdentity: false))
            {
                string upgradedIntent = (prev.IntentKind == "Submit" || currentIntentKind == "Submit")
                    ? "Submit"
                    : prev.IntentKind;
                prev.AddEvent(current, upgradedIntent, transferSourceIds);
                return true;
            }
        }

        return false;
    }

    private static bool AreExcelTargetsCompatible(EventCluster cluster, RawRecordedEvent current)
    {
        string? clusterSheet = cluster.Events.Select(e => e.SheetName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        string? clusterRange = cluster.Events.Select(e => e.RangeAddress).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

        bool sheetCompatible = string.IsNullOrWhiteSpace(clusterSheet) ||
            string.IsNullOrWhiteSpace(current.SheetName) ||
            string.Equals(clusterSheet, current.SheetName, StringComparison.OrdinalIgnoreCase);

        bool rangeCompatible = string.IsNullOrWhiteSpace(clusterRange) ||
            string.IsNullOrWhiteSpace(current.RangeAddress) ||
            string.Equals(clusterRange, current.RangeAddress, StringComparison.OrdinalIgnoreCase);

        return sheetCompatible && rangeCompatible;
    }

    private static bool AreTargetsExplicitlyIdentical(EventCluster cluster, RawRecordedEvent current)
    {
        if (cluster.AppCategory == "Excel")
        {
            return AreExcelTargetsCompatible(cluster, current);
        }

        if (cluster.IntentKind == "Navigate")
        {
            string? prevUrl = cluster.Events.Select(e => e.UrlOrWorkbookPath).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            return !string.IsNullOrWhiteSpace(prevUrl) &&
                   string.Equals(prevUrl, current.UrlOrWorkbookPath, StringComparison.OrdinalIgnoreCase);
        }

        return MatchesAnySemanticIdentifier(cluster, current);
    }

    private bool AreTargetsCompatibleForMerge(
        EventCluster cluster,
        RawRecordedEvent current,
        bool requireExplicitMatchWhenBothHaveIdentity)
    {
        if (cluster.IntentKind == "Navigate")
        {
            string? prevUrl = cluster.Events.Select(e => e.UrlOrWorkbookPath).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            return !string.IsNullOrWhiteSpace(prevUrl) &&
                   !string.IsNullOrWhiteSpace(current.UrlOrWorkbookPath) &&
                   string.Equals(prevUrl, current.UrlOrWorkbookPath, StringComparison.OrdinalIgnoreCase);
        }

        if (cluster.AppCategory == "Excel")
        {
            return AreExcelTargetsCompatible(cluster, current);
        }

        // 双方が明確に離れたスクリーン座標を持つ場合は別箇所への操作として分離する
        if (current.CoordinateX.HasValue && current.CoordinateY.HasValue)
        {
            var prevCoordEvents = cluster.Events.Where(e => e.CoordinateX.HasValue && e.CoordinateY.HasValue).ToList();
            if (prevCoordEvents.Any(prev =>
                Math.Abs(prev.CoordinateX!.Value - current.CoordinateX.Value) > 30 ||
                Math.Abs(prev.CoordinateY!.Value - current.CoordinateY.Value) > 30))
            {
                return false;
            }
        }

        if (!AreInputValuesCompatibleForMerge(cluster, current))
        {
            return false;
        }

        bool clusterHasSemanticId = cluster.Events.Any(HasAnySemanticIdentity);
        bool currentHasSemanticId = HasAnySemanticIdentity(current);

        if (!clusterHasSemanticId || !currentHasSemanticId)
        {
            TimeSpan elapsed = (current.Timestamp - cluster.LastTimestamp).Duration();
            if (elapsed > _options.CrossSourceCorrelationWindow)
            {
                return false;
            }

            // 同一ログソースが既に存在する場合は別操作イベントとして分離する
            bool sameSourceExists = cluster.Events.Any(e =>
                string.Equals(e.LogSource, current.LogSource, StringComparison.OrdinalIgnoreCase));
            if (sameSourceExists)
            {
                return false;
            }

            if (!clusterHasSemanticId && !currentHasSemanticId)
            {
                if (!current.CoordinateX.HasValue || !current.CoordinateY.HasValue)
                {
                    return false;
                }

                return cluster.Events.Any(e =>
                    e.CoordinateX.HasValue && e.CoordinateY.HasValue &&
                    Math.Abs(e.CoordinateX.Value - current.CoordinateX.Value) <= 30 &&
                    Math.Abs(e.CoordinateY.Value - current.CoordinateY.Value) <= 30);
            }

            // 既に高信頼ソースで確定済みのクラスタに後続の意味的識別子なしイベントを混入させない
            if (clusterHasSemanticId && !currentHasSemanticId &&
                cluster.Events.Any(e =>
                    string.Equals(e.LogSource, LogSourceConstants.BrowserDom, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.LogSource, LogSourceConstants.CustomAppHook, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        }

        if (MatchesAnySemanticIdentifier(cluster, current))
        {
            return true;
        }

        return !requireExplicitMatchWhenBothHaveIdentity &&
               AreRolesCompatible(
                   cluster.Events.Select(e => e.TargetRole).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)),
                   current.TargetRole) &&
               !HasConflictingNamedIdentifiers(cluster, current);
    }

    private static bool AreInputValuesCompatibleForMerge(EventCluster cluster, RawRecordedEvent current)
    {
        if (string.IsNullOrEmpty(current.CapturedValue))
        {
            return true;
        }

        var prevValues = cluster.Events
            .Select(e => e.CapturedValue)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToList();

        if (prevValues.Count == 0)
        {
            return true;
        }

        string lastVal = prevValues[^1];
        string currVal = current.CapturedValue;

        return string.Equals(lastVal, currVal, StringComparison.Ordinal) ||
               currVal.StartsWith(lastVal, StringComparison.Ordinal) ||
               lastVal.StartsWith(currVal, StringComparison.Ordinal);
    }

    private static bool AreRolesCompatible(string? roleA, string? roleB)
    {
        if (string.IsNullOrWhiteSpace(roleA) || string.IsNullOrWhiteSpace(roleB)) return true;
        if (string.Equals(roleA, roleB, StringComparison.OrdinalIgnoreCase)) return true;

        bool isTextA = roleA.Equals("textbox", StringComparison.OrdinalIgnoreCase) ||
                       roleA.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                       roleA.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                       roleA.Equals("input", StringComparison.OrdinalIgnoreCase);
        bool isTextB = roleB.Equals("textbox", StringComparison.OrdinalIgnoreCase) ||
                       roleB.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                       roleB.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                       roleB.Equals("input", StringComparison.OrdinalIgnoreCase);

        return isTextA && isTextB;
    }

    internal static bool HasAnySemanticIdentity(RawRecordedEvent ev) =>
        !string.IsNullOrWhiteSpace(ev.TargetName) ||
        !string.IsNullOrWhiteSpace(ev.TargetLabel) ||
        !string.IsNullOrWhiteSpace(ev.TargetStableId) ||
        !string.IsNullOrWhiteSpace(ev.CssSelector);

    private static bool MatchesAnySemanticIdentifier(EventCluster cluster, RawRecordedEvent current)
    {
        var currentNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(current.TargetName)) currentNames.Add(current.TargetName);
        if (!string.IsNullOrWhiteSpace(current.TargetLabel)) currentNames.Add(current.TargetLabel);

        foreach (var prev in cluster.Events)
        {
            if (!string.IsNullOrWhiteSpace(prev.TargetStableId) &&
                string.Equals(prev.TargetStableId, current.TargetStableId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(prev.CssSelector) &&
                string.Equals(prev.CssSelector, current.CssSelector, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prevNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(prev.TargetName)) prevNames.Add(prev.TargetName);
            if (!string.IsNullOrWhiteSpace(prev.TargetLabel)) prevNames.Add(prev.TargetLabel);

            foreach (string pn in prevNames)
            {
                foreach (string cn in currentNames)
                {
                    if (string.Equals(pn, cn, StringComparison.OrdinalIgnoreCase) ||
                        pn.Contains(cn, StringComparison.OrdinalIgnoreCase) ||
                        cn.Contains(pn, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool HasConflictingNamedIdentifiers(EventCluster cluster, RawRecordedEvent current)
    {
        string? clusterName = cluster.Events.Select(e => e.TargetName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (!string.IsNullOrWhiteSpace(clusterName) &&
            !string.IsNullOrWhiteSpace(current.TargetName) &&
            !string.Equals(clusterName, current.TargetName, StringComparison.OrdinalIgnoreCase) &&
            !clusterName.Contains(current.TargetName, StringComparison.OrdinalIgnoreCase) &&
            !current.TargetName.Contains(clusterName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? clusterLabel = cluster.Events.Select(e => e.TargetLabel).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (!string.IsNullOrWhiteSpace(clusterLabel) &&
            !string.IsNullOrWhiteSpace(current.TargetLabel) &&
            !string.Equals(clusterLabel, current.TargetLabel, StringComparison.OrdinalIgnoreCase) &&
            !clusterLabel.Contains(current.TargetLabel, StringComparison.OrdinalIgnoreCase) &&
            !current.TargetLabel.Contains(clusterLabel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? clusterId = cluster.Events.Select(e => e.TargetStableId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (!string.IsNullOrWhiteSpace(clusterId) &&
            !string.IsNullOrWhiteSpace(current.TargetStableId) &&
            !string.Equals(clusterId, current.TargetStableId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
