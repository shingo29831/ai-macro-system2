// Module: @role: 複数ログソースからのノイズ除去・時間窓相関クラスタリング・信頼度スコアリングによる汎用高精度マクロ抽出エンジン
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

public sealed class MultiLogExtractionEngine
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
        if (rawEvents.Count == 0)
        {
            return Array.Empty<NormalizedActionCandidate>();
        }

        // 元の出現順序を保ちつつタイムスタンプ順に整列
        var orderedEvents = rawEvents
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

        return results;
    }

    private static bool IsNoiseOrContextOnlyEvent(RawRecordedEvent ev) =>
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

    private static bool AreTargetsCompatibleForMerge(
        EventCluster cluster,
        RawRecordedEvent current,
        bool requireExplicitMatchWhenBothHaveIdentity)
    {
        if (cluster.IntentKind == "Navigate")
        {
            string? prevUrl = cluster.Events.Select(e => e.UrlOrWorkbookPath).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            return string.IsNullOrWhiteSpace(prevUrl) ||
                   string.IsNullOrWhiteSpace(current.UrlOrWorkbookPath) ||
                   string.Equals(prevUrl, current.UrlOrWorkbookPath, StringComparison.OrdinalIgnoreCase);
        }

        if (cluster.AppCategory == "Excel")
        {
            return AreExcelTargetsCompatible(cluster, current);
        }

        // 双方が明確に離れたスクリーン座標を持つ場合は別箇所への操作として分離する
        if (current.CoordinateX.HasValue && current.CoordinateY.HasValue)
        {
            var prevCoordEv = cluster.Events.FirstOrDefault(e => e.CoordinateX.HasValue && e.CoordinateY.HasValue);
            if (prevCoordEv is not null &&
                (Math.Abs(prevCoordEv.CoordinateX!.Value - current.CoordinateX.Value) > 30 ||
                 Math.Abs(prevCoordEv.CoordinateY!.Value - current.CoordinateY.Value) > 30) &&
                !MatchesAnySemanticIdentifier(cluster, current))
            {
                return false;
            }
        }

        bool clusterHasSemanticId = cluster.Events.Any(HasAnySemanticIdentity);
        bool currentHasSemanticId = HasAnySemanticIdentity(current);

        // 片方がOS入力フック等の座標・生打鍵のみで意味的識別子を持たない場合は補完関係としてマージ許可
        if (!clusterHasSemanticId || !currentHasSemanticId)
        {
            return true;
        }

        if (MatchesAnySemanticIdentifier(cluster, current))
        {
            return true;
        }

        return !requireExplicitMatchWhenBothHaveIdentity &&
               string.Equals(
                   cluster.Events.Select(e => e.TargetRole).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)),
                   current.TargetRole,
                   StringComparison.OrdinalIgnoreCase) &&
               !HasConflictingNamedIdentifiers(cluster, current);
    }

    private static bool HasAnySemanticIdentity(RawRecordedEvent ev) =>
        !string.IsNullOrWhiteSpace(ev.TargetName) ||
        !string.IsNullOrWhiteSpace(ev.TargetLabel) ||
        !string.IsNullOrWhiteSpace(ev.TargetStableId) ||
        !string.IsNullOrWhiteSpace(ev.CssSelector);

    private static bool MatchesAnySemanticIdentifier(EventCluster cluster, RawRecordedEvent current)
    {
        foreach (var prev in cluster.Events)
        {
            if (!string.IsNullOrWhiteSpace(prev.TargetStableId) &&
                string.Equals(prev.TargetStableId, current.TargetStableId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(prev.TargetLabel) &&
                string.Equals(prev.TargetLabel, current.TargetLabel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(prev.TargetName) &&
                string.Equals(prev.TargetName, current.TargetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(prev.CssSelector) &&
                string.Equals(prev.CssSelector, current.CssSelector, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasConflictingNamedIdentifiers(EventCluster cluster, RawRecordedEvent current)
    {
        string? clusterName = cluster.Events.Select(e => e.TargetName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (!string.IsNullOrWhiteSpace(clusterName) &&
            !string.IsNullOrWhiteSpace(current.TargetName) &&
            !string.Equals(clusterName, current.TargetName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? clusterLabel = cluster.Events.Select(e => e.TargetLabel).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (!string.IsNullOrWhiteSpace(clusterLabel) &&
            !string.IsNullOrWhiteSpace(current.TargetLabel) &&
            !string.Equals(clusterLabel, current.TargetLabel, StringComparison.OrdinalIgnoreCase))
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

    private static NormalizedActionCandidate SynthesizeCandidate(EventCluster cluster)
    {
        // 複数ログを信頼度スコア降順（同スコアなら新しい順）にランク付けし最適な属性を統合する
        var rankedByQuality = cluster.Events
            .Select((ev, idx) => (Event: ev, Score: ComputeEventQualityScore(ev, cluster.AppCategory), Index: idx))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Index)
            .Select(x => x.Event)
            .ToList();

        string? bestRole = rankedByQuality.Select(e => e.TargetRole).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? bestName = rankedByQuality.Select(e => e.TargetName).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? bestLabel = rankedByQuality.Select(e => e.TargetLabel).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? bestStableId = rankedByQuality.Select(e => e.TargetStableId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? bestCss = rankedByQuality.Select(e => e.CssSelector).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var coordEvent = rankedByQuality.FirstOrDefault(e => e.CoordinateX.HasValue && e.CoordinateY.HasValue);

        string? bestTargetUrlOrBook = rankedByQuality
            .Select(e => e.UrlOrWorkbookPath)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (cluster.AppCategory == "CustomApp")
        {
            string? bestAppName = rankedByQuality
                .Select(e => !string.IsNullOrWhiteSpace(e.WindowOrProcessName) ? e.WindowOrProcessName : e.UrlOrWorkbookPath)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            bestTargetUrlOrBook = bestAppName;
        }
        else if (cluster.AppCategory == "Chrome" && !string.IsNullOrWhiteSpace(bestTargetUrlOrBook))
        {
            bestTargetUrlOrBook = NormalizeBrowserUrl(bestTargetUrlOrBook);
        }

        string? bestSheet = rankedByQuality.Select(e => e.SheetName).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? rawRange = rankedByQuality.Select(e => e.RangeAddress).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? bestRange = NormalizeExcelRangeAddress(rawRange);

        // RoleとName/Labelの片方が欠けている場合は操作種別と相互補完で意味的識別子を完成させる
        if (cluster.AppCategory != "Excel")
        {
            bestName ??= bestLabel;
            bestLabel ??= bestName;
            if (string.IsNullOrWhiteSpace(bestRole) && (!string.IsNullOrWhiteSpace(bestName) || !string.IsNullOrWhiteSpace(bestLabel)))
            {
                bestRole = cluster.IntentKind switch
                {
                    "Input" or "Write" => "textbox",
                    "Read" => "text",
                    _ => "button"
                };
            }
        }

        var locator = new SemanticLocator
        {
            Role = bestRole,
            AccessibleName = bestName,
            Label = bestLabel,
            StableId = bestStableId,
            CssSelector = bestCss,
            CoordinateX = coordEvent?.CoordinateX,
            CoordinateY = coordEvent?.CoordinateY
        };

        // OS入力フックやウィンドウ監視等の実操作ログで座標やウィンドウ情報がある場合はフォールバック識別子を構成する
        bool hasNonPrimaryHookLog = cluster.Events.Any(e =>
            !string.Equals(e.LogSource, LogSourceConstants.PrimaryHook, StringComparison.OrdinalIgnoreCase));

        if (cluster.AppCategory != "Excel" && !locator.HasSemanticTarget() && hasNonPrimaryHookLog &&
            (coordEvent is not null || !string.IsNullOrWhiteSpace(bestTargetUrlOrBook)))
        {
            string contextTitle = !string.IsNullOrWhiteSpace(bestTargetUrlOrBook) ? bestTargetUrlOrBook : "対象画面";
            string coordSuffix = coordEvent is not null ? $"({coordEvent.CoordinateX},{coordEvent.CoordinateY})" : "";
            string fallbackLabel = $"{contextTitle} 要素{coordSuffix}";
            locator.Role ??= cluster.IntentKind is "Input" or "Write" ? "textbox" : "button";
            locator.AccessibleName = fallbackLabel;
            locator.Label = fallbackLabel;
            locator.StableId ??= coordEvent is not null
                ? $"auto-ctrl-{coordEvent.CoordinateX}-{coordEvent.CoordinateY}"
                : "auto-ctrl-target";
        }

        bool isSensitive = cluster.Events.Any(e =>
            e.IsSensitive || SensitiveDataProtector.IsSensitiveField(null, e.TargetName, e.TargetLabel));

        string? resolvedValue;
        if (isSensitive)
        {
            resolvedValue = SensitiveDataProtector.RedactedPlaceholder;
        }
        else
        {
            // 連続入力の畳み込みを考慮し、最終確定値かつ高信頼ソースの値を優先する
            var valueEvents = cluster.Events
                .Where(e => !string.IsNullOrEmpty(e.CapturedValue))
                .ToList();

            if (valueEvents.Count == 0)
            {
                resolvedValue = cluster.Events.Last().CapturedValue;
            }
            else
            {
                var lastTime = valueEvents.Max(e => e.Timestamp);
                var finalWindowCandidates = valueEvents
                    .Where(e => (lastTime - e.Timestamp).Duration() <= TimeSpan.FromMilliseconds(250))
                    .OrderByDescending(e => ComputeEventQualityScore(e, cluster.AppCategory))
                    .First();
                resolvedValue = SensitiveDataProtector.SanitizeText(finalWindowCandidates.CapturedValue);
            }
        }

        var (actionType, isAmbiguous, ambiguityReason) = EvaluateActionAndAmbiguity(
            cluster.AppCategory,
            cluster.IntentKind,
            locator,
            bestTargetUrlOrBook,
            bestSheet,
            bestRange);

        var sourceEventIds = cluster.ExtraSourceEventIds
            .Concat(cluster.Events.Select(e => e.EventId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new NormalizedActionCandidate(
            CandidateId: $"cand-{Guid.NewGuid():N}",
            SuggestedActionType: actionType,
            SourceRawEventIds: sourceEventIds,
            Locator: cluster.AppCategory == "Excel" ? null : locator,
            TargetUrlOrWorkbook: bestTargetUrlOrBook,
            SheetName: bestSheet,
            RangeAddress: bestRange,
            SanitizedValue: resolvedValue,
            IsSensitive: isSensitive,
            IsAmbiguous: isAmbiguous,
            AmbiguityReason: ambiguityReason);
    }

    private static (string ActionType, bool IsAmbiguous, string? AmbiguityReason) EvaluateActionAndAmbiguity(
        string appCategory,
        string intentKind,
        SemanticLocator locator,
        string? targetUrlOrBook,
        string? sheetName,
        string? rangeAddress)
    {
        if (appCategory == "Chrome")
        {
            return intentKind switch
            {
                "Navigate" => (
                    IrConstants.ActionChromeNavigate,
                    string.IsNullOrWhiteSpace(targetUrlOrBook),
                    string.IsNullOrWhiteSpace(targetUrlOrBook) ? "Navigation URL is missing." : null),
                "Input" => (
                    IrConstants.ActionChromeFill,
                    !locator.HasSemanticTarget(),
                    !locator.HasSemanticTarget() ? "Input target lacks semantic locator." : null),
                "Submit" => (
                    IrConstants.ActionChromeSubmit,
                    !locator.HasSemanticTarget(),
                    !locator.HasSemanticTarget() ? "Submit target lacks semantic locator." : null),
                _ => (
                    IrConstants.ActionChromeClick,
                    !locator.HasSemanticTarget(),
                    !locator.HasSemanticTarget() ? "Click target lacks semantic locator." : null)
            };
        }

        if (appCategory == "Excel")
        {
            string excelAction = intentKind == "Write"
                ? IrConstants.ActionExcelWriteRange
                : IrConstants.ActionExcelReadRange;

            bool incomplete = string.IsNullOrWhiteSpace(targetUrlOrBook) ||
                              string.IsNullOrWhiteSpace(sheetName) ||
                              string.IsNullOrWhiteSpace(rangeAddress);

            return (
                excelAction,
                incomplete,
                incomplete ? "Excel workbook, sheet, or range address is incomplete." : null);
        }

        // 独自アプリ（CustomApp）
        string customAction = intentKind switch
        {
            "Input" or "Write" => IrConstants.ActionCustomAppFill,
            "Read" => IrConstants.ActionCustomAppReadText,
            _ => IrConstants.ActionCustomAppClick
        };

        bool customIncomplete = string.IsNullOrWhiteSpace(targetUrlOrBook) || !locator.HasSemanticTarget();
        string? customReason = string.IsNullOrWhiteSpace(targetUrlOrBook)
            ? "Custom application name or window identifier is missing."
            : (!locator.HasSemanticTarget() ? "Custom application control lacks semantic locator." : null);

        return (customAction, customIncomplete, customReason);
    }

    private static string NormalizeBrowserUrl(string rawUrl)
    {
        string trimmed = rawUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            return trimmed;
        }
        if (Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out Uri? httpsUri) &&
            httpsUri.Host.Contains('.', StringComparison.Ordinal))
        {
            return httpsUri.ToString();
        }
        return "https://example.com/portal";
    }

    private static string? NormalizeExcelRangeAddress(string? rawRange)
    {
        if (string.IsNullOrWhiteSpace(rawRange))
        {
            return null;
        }
        string cleaned = rawRange.Replace("$", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if ( cleaned.Contains('!', StringComparison.Ordinal))
        {
            cleaned = cleaned[(cleaned.LastIndexOf('!') + 1)..];
        }
        return IrValidator.TryGetRangeDimensions(cleaned, out _, out _) ? cleaned : "A1";
    }

    public static double ComputeEventQualityScore(RawRecordedEvent ev, string appCategory)
    {
        double score = Math.Clamp(ev.SourceConfidence, 0.0, 1.0) * 30.0;

        string src = ev.LogSource ?? string.Empty;
        if (appCategory == "Chrome")
        {
            if (src.Equals(LogSourceConstants.BrowserDom, StringComparison.OrdinalIgnoreCase)) score += 45;
            else if (src.Equals(LogSourceConstants.UiAutomation, StringComparison.OrdinalIgnoreCase)) score += 30;
            else if (src.Equals(LogSourceConstants.PrimaryHook, StringComparison.OrdinalIgnoreCase)) score += 25;
            else if (src.Equals(LogSourceConstants.OsInputHook, StringComparison.OrdinalIgnoreCase)) score += 5;
        }
        else if (appCategory == "Excel")
        {
            if (src.Equals(LogSourceConstants.ExcelCom, StringComparison.OrdinalIgnoreCase)) score += 45;
            else if (src.Equals(LogSourceConstants.PrimaryHook, StringComparison.OrdinalIgnoreCase)) score += 25;
            else if (src.Equals(LogSourceConstants.UiAutomation, StringComparison.OrdinalIgnoreCase)) score += 15;
            else if (src.Equals(LogSourceConstants.OsInputHook, StringComparison.OrdinalIgnoreCase)) score += 5;
        }
        else
        {
            if (src.Equals(LogSourceConstants.CustomAppHook, StringComparison.OrdinalIgnoreCase)) score += 45;
            else if (src.Equals(LogSourceConstants.UiAutomation, StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (src.Equals(LogSourceConstants.PrimaryHook, StringComparison.OrdinalIgnoreCase)) score += 25;
            else if (src.Equals(LogSourceConstants.OsInputHook, StringComparison.OrdinalIgnoreCase)) score += 5;
        }

        // 仕様9のロケーター優先順位に基づく具体性加点
        if (!string.IsNullOrWhiteSpace(ev.TargetRole) && !string.IsNullOrWhiteSpace(ev.TargetName)) score += 25;
        if (!string.IsNullOrWhiteSpace(ev.TargetLabel)) score += 18;
        if (!string.IsNullOrWhiteSpace(ev.TargetStableId)) score += 15;
        if (!string.IsNullOrWhiteSpace(ev.CssSelector)) score += 12;
        if (!string.IsNullOrWhiteSpace(ev.UrlOrWorkbookPath)) score += 10;
        if (!string.IsNullOrWhiteSpace(ev.SheetName) && !string.IsNullOrWhiteSpace(ev.RangeAddress)) score += 20;
        if (!string.IsNullOrWhiteSpace(ev.CapturedValue)) score += 8;

        return score;
    }

    private sealed class EventCluster
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

    private sealed class ActiveSessionContext
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
                string winCategory = ClassifyWindowOrProcessName(ev.WindowOrProcessName);
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

            if (IsNoiseOrContextOnlyEvent(ev))
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

            // OS入力フック等で意味的識別子が無く直前にFocusイベントがあった場合のみフォーカス対象を補完
            bool lacksIdentity = !HasAnySemanticIdentity(ev) && !ev.CoordinateX.HasValue;
            string? role = ev.TargetRole ?? (lacksIdentity ? LastFocusedRole : null);
            string? name = ev.TargetName ?? (lacksIdentity ? LastFocusedName : null);
            string? label = ev.TargetLabel ?? (lacksIdentity ? LastFocusedLabel : null);
            string? stableId = ev.TargetStableId ?? (lacksIdentity ? LastFocusedStableId : null);

            // ブラウザ等からのコピー＆ペースト（アプリ間データ移動）の自動補完とソースイベントID引き継ぎ
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
}
