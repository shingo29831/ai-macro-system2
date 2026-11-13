// Module: @role: クラスタリングされた操作イベント群からの正規化候補合成・曖昧性評価・品質スコアリング
using System;
using System.Collections.Generic;
using System.Linq;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;

namespace AiMacroSystem.Application;

public sealed partial class MultiLogExtractionEngine
{
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

        string? bestWindowName = rankedByQuality
            .Select(e => e.WindowOrProcessName)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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
        else if (cluster.AppCategory == "Chrome")
        {
            if (!string.IsNullOrWhiteSpace(bestTargetUrlOrBook))
            {
                bestTargetUrlOrBook = NormalizeBrowserUrl(bestTargetUrlOrBook);
            }
            else if (!string.IsNullOrWhiteSpace(bestWindowName))
            {
                bestTargetUrlOrBook = NormalizeBrowserUrl(bestWindowName);
            }
        }

        string? bestSheet = rankedByQuality.Select(e => e.SheetName).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? rawRange = rankedByQuality.Select(e => e.RangeAddress).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? bestRange = NormalizeExcelRangeAddress(rawRange);

        // RoleとName/Labelの片方が欠けている場合は操作種別と相互補完で意味的識別子を完成させる
        if (cluster.AppCategory != "Excel")
        {
            bestName ??= bestLabel;
            bestLabel ??= bestName;
            if (cluster.IntentKind is "Input" or "Write" && (bestRole == "text" || string.IsNullOrWhiteSpace(bestRole)))
            {
                bestRole = "textbox";
            }
            else if (string.IsNullOrWhiteSpace(bestRole) && (!string.IsNullOrWhiteSpace(bestName) || !string.IsNullOrWhiteSpace(bestLabel)))
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

        // 実環境でAccessibleNameが取得できず座標やウィンドウ情報のみの場合でもフォールバック識別子を構成する
        bool hasNonPrimaryHookLog = cluster.Events.Any(e =>
            !string.Equals(e.LogSource, LogSourceConstants.PrimaryHook, StringComparison.OrdinalIgnoreCase));

        if (cluster.AppCategory != "Excel" && !locator.HasSemanticTarget() &&
            (coordEvent is not null || !string.IsNullOrWhiteSpace(bestWindowName) || hasNonPrimaryHookLog))
        {
            string contextTitle = !string.IsNullOrWhiteSpace(bestWindowName)
                ? bestWindowName
                : (!string.IsNullOrWhiteSpace(bestTargetUrlOrBook) ? bestTargetUrlOrBook : "対象ウィンドウ");
            string coordSuffix = coordEvent is not null ? $"({coordEvent.CoordinateX},{coordEvent.CoordinateY})" : "";
            string fallbackLabel = $"{contextTitle} 要素{coordSuffix}";
            locator.Role ??= cluster.IntentKind switch
            {
                "Input" or "Write" => "textbox",
                "Read" => "text",
                _ => "button"
            };
            locator.AccessibleName = fallbackLabel;
            locator.Label = fallbackLabel;
            locator.StableId ??= coordEvent is not null
                ? $"auto-ctrl-{coordEvent.CoordinateX}-{coordEvent.CoordinateY}"
                : "auto-ctrl-target";
        }

        if (cluster.AppCategory == "CustomApp" && string.IsNullOrWhiteSpace(bestTargetUrlOrBook) &&
            (coordEvent is not null || !string.IsNullOrWhiteSpace(bestWindowName) || hasNonPrimaryHookLog || locator.HasSemanticTarget()))
        {
            bestTargetUrlOrBook = !string.IsNullOrWhiteSpace(bestWindowName)
                ? bestWindowName
                : (bestName ?? bestLabel ?? "DesktopApp");
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
        if (!trimmed.Contains(' ', StringComparison.Ordinal) &&
            Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out Uri? httpsUri) &&
            httpsUri.Host.Contains('.', StringComparison.Ordinal))
        {
            return httpsUri.ToString();
        }
        return BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine(trimmed, out _);
    }

    private static string? NormalizeExcelRangeAddress(string? rawRange)
    {
        if (string.IsNullOrWhiteSpace(rawRange))
        {
            return null;
        }
        string cleaned = rawRange.Replace("$", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (cleaned.Contains('!', StringComparison.Ordinal))
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
}
