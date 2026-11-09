// Module: @role: ワークフロー操作ノードの視覚的表現、要約文生成、検証結果フォーマットおよび入力値変換を担当する静的ヘルパー
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public static class StepBlockFormatter
{
    public static void CollectActionNodesRecursive(WorkflowNode node, List<WorkflowNode> acc)
    {
        if (node.Type == IrConstants.NodeTypeAction && !string.IsNullOrWhiteSpace(node.ActionType))
        {
            acc.Add(node);
            return;
        }
        if (node.Children is not null) foreach (var c in node.Children) CollectActionNodesRecursive(c, acc);
        if (node.ThenChildren is not null) foreach (var c in node.ThenChildren) CollectActionNodesRecursive(c, acc);
        if (node.ElseChildren is not null) foreach (var c in node.ElseChildren) CollectActionNodesRecursive(c, acc);
        if (node.BodyChildren is not null) foreach (var c in node.BodyChildren) CollectActionNodesRecursive(c, acc);
    }

    public static SemanticLocator? ExtractLocatorFromNode(WorkflowNode node)
    {
        if (!node.Args.HasValue) return null;
        JsonElement args = node.Args.Value;
        return node.ActionType switch
        {
            IrConstants.ActionChromeFill => args.Deserialize<ChromeFillArgs>()?.Locator,
            IrConstants.ActionChromeClick => args.Deserialize<ChromeClickArgs>()?.Locator,
            IrConstants.ActionChromeSubmit => args.Deserialize<ChromeSubmitArgs>()?.Locator,
            IrConstants.ActionCustomAppClick => args.Deserialize<CustomAppClickArgs>()?.Locator,
            IrConstants.ActionCustomAppFill => args.Deserialize<CustomAppFillArgs>()?.Locator,
            IrConstants.ActionCustomAppReadText => args.Deserialize<CustomAppReadTextArgs>()?.Locator,
            _ => null
        };
    }

    public static SemanticLocator MergeLocatorWithPreservedMetadata(string role, string safeElem, SemanticLocator? preserved) =>
        new()
        {
            Role = role,
            AccessibleName = safeElem,
            Label = safeElem,
            StableId = preserved?.StableId,
            CssSelector = preserved?.CssSelector,
            CoordinateX = preserved?.CoordinateX,
            CoordinateY = preserved?.CoordinateY
        };

    public static List<List<string>> AdjustMatrixToRange(List<List<string>> rawRows, string rangeAddress)
    {
        if (!IrValidator.TryGetRangeDimensions(rangeAddress, out int targetRows, out int targetCols))
        {
            return rawRows;
        }
        var adjusted = new List<List<string>>(targetRows);
        for (int r = 0; r < targetRows; r++)
        {
            var row = new List<string>(targetCols);
            for (int c = 0; c < targetCols; c++)
            {
                string val = (r < rawRows.Count && c < rawRows[r].Count)
                    ? rawRows[r][c]
                    : (rawRows.Count > 0 && rawRows[0].Count > 0 ? rawRows[0][0] : "データ");
                row.Add(val);
            }
            adjusted.Add(row);
        }
        return adjusted;
    }

    public static List<List<string>> ParseCsvMatrix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<List<string>> { new() { "データ" } };
        }
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var matrix = new List<List<string>>();
        foreach (string line in lines)
        {
            matrix.Add(line.Split(',').Select(s => s.Trim()).ToList());
        }
        return matrix.Count > 0 ? matrix : new List<List<string>> { new() { text } };
    }

    public static (string Icon, string Category, string ColorHex) GetBlockVisualMeta(string actionKind) =>
        actionKind switch
        {
            IrConstants.ActionChromeNavigate => ("🌐", "Chrome ナビゲーション", "#2563EB"),
            IrConstants.ActionChromeFill => ("✏️", "Chrome 文字入力", "#0D9488"),
            IrConstants.ActionChromeClick => ("👆", "Chrome クリック操作", "#4F46E5"),
            IrConstants.ActionChromeSubmit => ("📤", "Chrome フォーム外部送信", "#D97706"),
            IrConstants.ActionExcelReadRange => ("📖", "Excel データ読み取り", "#16A34A"),
            IrConstants.ActionExcelWriteRange => ("📝", "Excel データ書き込み", "#15803D"),
            IrConstants.ActionCustomAppClick => ("🖥", "独自アプリ クリック操作", "#7C3AED"),
            IrConstants.ActionCustomAppFill => ("⌨️", "独自アプリ 文字入力", "#9333EA"),
            IrConstants.ActionCustomAppReadText => ("🔍", "独自アプリ テキスト取得", "#6D28D9"),
            _ => ("⚙️", "アクション", "#475569")
        };

    public static (string AppBadge, string TargetLocation, string OperationValue) ExtractStructuredBlockInfo(WorkflowNode node)
    {
        if (!node.Args.HasValue)
        {
            return ("システム", node.ActionType ?? "操作", "-");
        }

        JsonElement args = node.Args.Value;
        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var a = args.Deserialize<ChromeNavigateArgs>();
                return ("Chrome ブラウザ", $"URL: {a?.Url}", "Webページ遷移・オリジン検証");
            }
            case IrConstants.ActionChromeFill:
            {
                var a = args.Deserialize<ChromeFillArgs>();
                var loc = a?.Locator ?? new SemanticLocator();
                var (strategy, key) = loc.ResolveByPriority();
                string coordInfo = loc.CoordinateX.HasValue && loc.CoordinateY.HasValue ? $" [座標: ({loc.CoordinateX},{loc.CoordinateY})]" : "";
                string val = a?.IsSensitive == true ? "[REDACTED] (機密保護)" : (a?.Value ?? string.Empty);
                return ("Chrome ブラウザ", $"要素: {key} ({strategy}){coordInfo}", $"入力値: 「{val}」");
            }
            case IrConstants.ActionChromeClick:
            {
                var a = args.Deserialize<ChromeClickArgs>();
                var loc = a?.Locator ?? new SemanticLocator();
                var (strategy, key) = loc.ResolveByPriority();
                string coordInfo = loc.CoordinateX.HasValue && loc.CoordinateY.HasValue ? $" [座標: ({loc.CoordinateX},{loc.CoordinateY})]" : "";
                return ("Chrome ブラウザ", $"要素: {key} ({strategy}){coordInfo}", "クリック操作・一意性検証");
            }
            case IrConstants.ActionChromeSubmit:
            {
                var a = args.Deserialize<ChromeSubmitArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                return ("Chrome ブラウザ", $"送信先: {a?.DestinationUrl} / 要素: {key} ({strategy})", $"事後検証: {a?.ExpectedSuccessSelector}");
            }
            case IrConstants.ActionExcelReadRange:
            {
                var a = args.Deserialize<ExcelReadRangeArgs>();
                return ("Excel", $"ブック: {Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]", $"セル範囲読み取り ({a?.RangeAddress})");
            }
            case IrConstants.ActionExcelWriteRange:
            {
                var a = args.Deserialize<ExcelWriteRangeArgs>();
                string valPreview = a?.Values is { Count: > 0 }
                    ? string.Join(" / ", a.Values.Select(r => string.Join(", ", r)))
                    : "参照データ書き込み";
                return ("Excel", $"ブック: {Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]", $"書込値: {valPreview}");
            }
            case IrConstants.ActionCustomAppClick:
            {
                var a = args.Deserialize<CustomAppClickArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string appName = string.IsNullOrWhiteSpace(a?.ApplicationName) ? "独自アプリ" : a.ApplicationName;
                return ($"独自アプリ ({appName})", $"要素: {key} ({strategy})", "コントロールクリック操作");
            }
            case IrConstants.ActionCustomAppFill:
            {
                var a = args.Deserialize<CustomAppFillArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string appName = string.IsNullOrWhiteSpace(a?.ApplicationName) ? "独自アプリ" : a.ApplicationName;
                string val = a?.IsSensitive == true ? "[REDACTED] (機密保護)" : (a?.Value ?? string.Empty);
                return ($"独自アプリ ({appName})", $"要素: {key} ({strategy})", $"入力値: 「{val}」");
            }
            case IrConstants.ActionCustomAppReadText:
            {
                var a = args.Deserialize<CustomAppReadTextArgs>();
                var (strategy, key) = (a?.Locator ?? new SemanticLocator()).ResolveByPriority();
                string appName = string.IsNullOrWhiteSpace(a?.ApplicationName) ? "独自アプリ" : a.ApplicationName;
                return ($"独自アプリ ({appName})", $"要素: {key} ({strategy})", "テキスト読み取り");
            }
            default:
                return ("アプリ", node.ActionType ?? "操作", "-");
        }
    }

    public static (string Title, string Detail) DescribeActionNode(WorkflowNode node)
    {
        if (!node.Args.HasValue) return (node.ActionType ?? "操作", "");
        JsonElement args = node.Args.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var a = args.Deserialize<ChromeNavigateArgs>();
                return ("🌐 Webページを開く", $"移動先URL: {a?.Url}");
            }
            case IrConstants.ActionChromeFill:
            {
                var a = args.Deserialize<ChromeFillArgs>();
                string target = a?.Locator?.Label ?? a?.Locator?.AccessibleName ?? "入力欄";
                string val = a?.IsSensitive == true ? "******** (保護)" : (a?.Value ?? "");
                return ("✏️ 文字を入力する", $"「{target}」に「{val}」を入力");
            }
            case IrConstants.ActionChromeClick:
            {
                var a = args.Deserialize<ChromeClickArgs>();
                string target = a?.Locator?.AccessibleName ?? a?.Locator?.Label ?? "ボタン";
                return ("👆 ボタンをクリック", $"対象: 「{target}」");
            }
            case IrConstants.ActionChromeSubmit:
            {
                var a = args.Deserialize<ChromeSubmitArgs>();
                return ("📤 フォームを送信", $"送信先: {a?.DestinationUrl}");
            }
            case IrConstants.ActionExcelReadRange:
            {
                var a = args.Deserialize<ExcelReadRangeArgs>();
                return ("📖 Excelを読み取る", $"{Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]");
            }
            case IrConstants.ActionExcelWriteRange:
            {
                var a = args.Deserialize<ExcelWriteRangeArgs>();
                return ("📝 Excelに書き込む", $"{Path.GetFileName(a?.WorkbookPath ?? "")} [{a?.SheetName}!{a?.RangeAddress}]");
            }
            case IrConstants.ActionCustomAppClick:
            {
                var a = args.Deserialize<CustomAppClickArgs>();
                string target = a?.Locator?.AccessibleName ?? a?.Locator?.Label ?? "ボタン";
                return ("🖥 独自アプリのボタンを押す", $"{a?.ApplicationName} [{target}]");
            }
            case IrConstants.ActionCustomAppFill:
            {
                var a = args.Deserialize<CustomAppFillArgs>();
                string target = a?.Locator?.Label ?? a?.Locator?.AccessibleName ?? "入力欄";
                string val = a?.IsSensitive == true ? "******** (保護)" : (a?.Value ?? "");
                return ("⌨️ 独自アプリに文字を入力", $"{a?.ApplicationName} [{target}: {val}]");
            }
            case IrConstants.ActionCustomAppReadText:
            {
                var a = args.Deserialize<CustomAppReadTextArgs>();
                string target = a?.Locator?.Label ?? a?.Locator?.AccessibleName ?? "テキスト";
                return ("🔍 独自アプリの文字を読み取る", $"{a?.ApplicationName} [{target}]");
            }
            default:
                return (node.ActionType ?? "アクション", "");
        }
    }

    public static FriendlyValidationItem ToFriendlyIssue(ValidationIssue issue)
    {
        string howToFix = issue.Code switch
        {
            "CHROME_ORIGIN_NOT_ALLOWED" => "移動先のURLが許可リストに入っていません。「① かんたん作成」から手順を追加し直すか、URLを確認してください。",
            "EXCEL_RANGE_INVALID" => "Excelのセル範囲は「A1」や「A1:B5」のような形式で入力してください。",
            "LOCATOR_AMBIGUOUS_OR_EMPTY" => "操作するボタンや入力欄の名前を入力してください。",
            "POLICY_PROHIBITED_OPERATION" => "削除などの危険な操作が含まれているため実行できません。該当ステップを削除してください。",
            _ => issue.SuggestedFix ?? "入力項目に空欄や誤りがないか確認してください。"
        };

        return new FriendlyValidationItem
        {
            Severity = "⚠️ 要修正",
            FriendlyLocation = string.IsNullOrWhiteSpace(issue.NodeId) ? "マクロ全体" : $"ステップ ({issue.NodeId})",
            FriendlyReason = issue.Message,
            HowToFix = howToFix,
            TechnicalCode = $"{issue.Stage} / {issue.Code}"
        };
    }

    public static FriendlyPlanReviewItem ToFriendlyPlanItem(PlannedActionItem item)
    {
        string opName = item.ActionType switch
        {
            IrConstants.ActionChromeNavigate => "🌐 Webページを開く",
            IrConstants.ActionChromeFill => "✏️ 文字を入力する",
            IrConstants.ActionChromeClick => "👆 ボタンをクリック",
            IrConstants.ActionChromeSubmit => "📤 フォームを外部送信",
            IrConstants.ActionExcelReadRange => "📖 Excelを読み取る",
            IrConstants.ActionExcelWriteRange => "📝 Excelに書き込む",
            IrConstants.ActionCustomAppClick => "🖥 独自アプリのボタンを押す",
            IrConstants.ActionCustomAppFill => "⌨️ 独自アプリに文字を入力",
            IrConstants.ActionCustomAppReadText => "🔍 独自アプリの文字を読み取る",
            _ => item.ActionType
        };

        string effect = item.SideEffects switch
        {
            SideEffectKind.Read => "読み取りのみ（安全）",
            SideEffectKind.LocalMutation => "画面またはExcelファイルの変更",
            SideEffectKind.ExternalSend => "外部へのデータ送信（要注意）",
            _ => item.SideEffects.ToString()
        };

        string risk = item.EvaluatedRisk switch
        {
            RiskLevel.Low => "🟢 低リスク",
            RiskLevel.Medium => "🟡 中リスク（結果を確認）",
            RiskLevel.High => "🟠 高リスク（個別承認が必要）",
            RiskLevel.Prohibited => "🔴 実行禁止",
            _ => item.EvaluatedRisk.ToString()
        };

        return new FriendlyPlanReviewItem
        {
            NodeId = item.NodeId,
            OperationName = opName,
            TargetDescription = item.TargetSummary,
            SideEffectDescription = effect,
            RiskBadge = risk,
            NeedsExtraApproval = item.RequiresIndividualApproval
        };
    }

    public static void TryRegisterOrigin(string? rawUrl, List<OriginRule> origins)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl) && Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme is "https" or "http" && !origins.Any(o => o.Matches(uri)))
            {
                origins.Add(new OriginRule
                {
                    Scheme = uri.Scheme,
                    Host = uri.Host,
                    Port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port
                });
            }
        }
    }
}
