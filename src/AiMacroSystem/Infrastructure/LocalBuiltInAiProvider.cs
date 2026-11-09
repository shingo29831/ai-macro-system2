// Module: @role: 操作候補から構造化Workflow IRを生成するローカル組み込みAIプロバイダー
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed class LocalBuiltInAiProvider : IAiProvider
{
    public string ProviderName => "LocalBuiltInAi (Ollama互換)";
    public string ModelId { get; set; } = "local-structured-ir-v0.7";
    public bool IsExternal => false;
    public bool IsAvailable { get; set; } = true;

    public Task<string> GenerateWorkflowIrJsonAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        IReadOnlyList<ValidationIssue>? previousValidationIssues,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var allowedOrigins = new List<OriginRule>(policy.AllowedOrigins);
        var children = new List<WorkflowNode>();
        int idx = 1;

        foreach (var cand in candidates)
        {
            // Chrome操作候補のURLオリジンをすべて許可リストへ自動登録する
            if (cand.SuggestedActionType.StartsWith("Chrome.", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) &&
                Uri.TryCreate(cand.TargetUrlOrWorkbook, UriKind.Absolute, out Uri? candUri) &&
                (candUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                 candUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                !allowedOrigins.Any(o => o.Matches(candUri)))
            {
                allowedOrigins.Add(new OriginRule
                {
                    Scheme = candUri.Scheme,
                    Host = candUri.Host,
                    Port = candUri.IsDefaultPort ? (candUri.Scheme == "https" ? 443 : 80) : candUri.Port
                });
            }

            string nodeId = $"step-{idx++}";
            switch (cand.SuggestedActionType)
            {
                case IrConstants.ActionChromeNavigate:
                {
                    string url = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? "https://example.com"
                        : cand.TargetUrlOrWorkbook;

                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                        !allowedOrigins.Any(o => o.Matches(uri)))
                    {
                        allowedOrigins.Add(new OriginRule
                        {
                            Scheme = uri.Scheme,
                            Host = uri.Host,
                            Port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port
                        });
                    }

                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeNavigate,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = url })
                    });
                    break;
                }
                case IrConstants.ActionChromeFill:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeFill,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Label = "入力欄" },
                            Value = string.IsNullOrEmpty(cand.SanitizedValue) ? "サンプル入力" : cand.SanitizedValue,
                            IsSensitive = cand.IsSensitive
                        })
                    });
                    break;
                }
                case IrConstants.ActionChromeClick:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeClick,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = "確認" }
                        })
                    });
                    break;
                }
                case IrConstants.ActionExcelReadRange:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelReadRange,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
                        {
                            WorkbookPath = cand.TargetUrlOrWorkbook ?? Path.Combine(Path.GetTempPath(), "MacroSample.xlsx"),
                            SheetName = cand.SheetName ?? "Sheet1",
                            RangeAddress = cand.RangeAddress ?? "A1:B2"
                        })
                    });
                    break;
                }
                case IrConstants.ActionChromeSubmit:
                {
                    string destUrl = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? "https://example.com/submit"
                        : cand.TargetUrlOrWorkbook;

                    if (Uri.TryCreate(destUrl, UriKind.Absolute, out Uri? uri) &&
                        !allowedOrigins.Any(o => o.Matches(uri)))
                    {
                        allowedOrigins.Add(new OriginRule
                        {
                            Scheme = uri.Scheme,
                            Host = uri.Host,
                            Port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port
                        });
                    }

                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeSubmit,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeSubmitArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = "送信" },
                            DestinationUrl = destUrl,
                            ExpectedSuccessSelector = "#submit-success"
                        })
                    });
                    break;
                }
                case IrConstants.ActionExcelWriteRange:
                {
                    string rangeAddr = cand.RangeAddress ?? "A1:A1";
                    string valText = cand.SanitizedValue ?? "自動記録データ";
                    var parsedRows = ParseMatrixForRange(valText, rangeAddr);
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelWriteRange,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                        {
                            WorkbookPath = cand.TargetUrlOrWorkbook ?? Path.Combine(Path.GetTempPath(), "MacroSample.xlsx"),
                            SheetName = cand.SheetName ?? "Sheet1",
                            RangeAddress = rangeAddr,
                            Values = parsedRows
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppClick:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppClick,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppClickArgs
                        {
                            ApplicationName = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) ? "基幹業務システム" : cand.TargetUrlOrWorkbook,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = "実行" }
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppFill:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppFill,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppFillArgs
                        {
                            ApplicationName = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) ? "基幹業務システム" : cand.TargetUrlOrWorkbook,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "textbox", Label = "入力項目" },
                            Value = string.IsNullOrEmpty(cand.SanitizedValue) ? "自動入力値" : cand.SanitizedValue,
                            IsSensitive = cand.IsSensitive
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppReadText:
                {
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppReadText,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppReadTextArgs
                        {
                            ApplicationName = string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook) ? "基幹業務システム" : cand.TargetUrlOrWorkbook,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "text", Label = "ステータス" }
                        })
                    });
                    break;
                }
            }
        }

        if (children.Count == 0)
        {
            allowedOrigins.Add(new OriginRule { Scheme = "https", Host = "example.com", Port = 443 });
            children.Add(new WorkflowNode
            {
                NodeId = "step-1",
                Type = IrConstants.NodeTypeAction,
                ActionType = IrConstants.ActionChromeNavigate,
                Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = "https://example.com" })
            });
        }

        var wf = new WorkflowDefinition
        {
            SchemaVersion = IrConstants.CurrentSchemaVersion,
            WorkflowId = $"wf-ai-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Name = "記録から自動作成したマクロ",
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = allowedOrigins,
                AllowCoordinateClick = policy.AllowCoordinateClick,
                AllowExternalAi = policy.AllowExternalAi,
                AllowedExcelDirectories = policy.AllowedExcelDirectories
            },
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = children
            }
        };

        string json = JsonSerializer.Serialize(wf, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(json);
    }

    private static List<List<string>> ParseMatrixForRange(string text, string rangeAddress)
    {
        var rawLines = (text ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(',').Select(c => c.Trim()).ToList())
            .ToList();

        if (!IrValidator.TryGetRangeDimensions(rangeAddress, out int targetRows, out int targetCols))
        {
            return rawLines.Count > 0 ? rawLines : new List<List<string>> { new() { text ?? string.Empty } };
        }

        var matrix = new List<List<string>>(targetRows);
        for (int r = 0; r < targetRows; r++)
        {
            var row = new List<string>(targetCols);
            for (int c = 0; c < targetCols; c++)
            {
                string cell = (r < rawLines.Count && c < rawLines[r].Count)
                    ? rawLines[r][c]
                    : (rawLines.Count > 0 && rawLines[0].Count > 0 ? rawLines[0][0] : "データ");
                row.Add(cell);
            }
            matrix.Add(row);
        }
        return matrix;
    }
}
