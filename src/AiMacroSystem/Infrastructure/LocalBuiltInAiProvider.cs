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
        var allowedExcelDirs = new List<string>(policy.AllowedExcelDirectories);
        var children = new List<WorkflowNode>();
        int idx = 1;

        string? activeBrowserUrl = candidates
            .Where(c => c.SuggestedActionType.StartsWith("Chrome.", StringComparison.Ordinal))
            .Select(c => c.TargetUrlOrWorkbook)
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u) && Uri.TryCreate(u, UriKind.Absolute, out _));

        if (string.IsNullOrWhiteSpace(activeBrowserUrl) && allowedOrigins.Count > 0)
        {
            var firstOrigin = allowedOrigins[0];
            string portPart = (firstOrigin.Scheme == "https" && firstOrigin.Port == 443) ||
                              (firstOrigin.Scheme == "http" && firstOrigin.Port == 80)
                ? string.Empty
                : $":{firstOrigin.Port}";
            activeBrowserUrl = $"{firstOrigin.Scheme}://{firstOrigin.Host}{portPart}";
        }

        string? activeWorkbookPath = candidates
            .Where(c => c.SuggestedActionType.StartsWith("Excel.", StringComparison.Ordinal))
            .Select(c => c.TargetUrlOrWorkbook)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        string? activeSheetName = candidates
            .Where(c => c.SuggestedActionType.StartsWith("Excel.", StringComparison.Ordinal))
            .Select(c => c.SheetName)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        string? activeRangeAddress = candidates
            .Where(c => c.SuggestedActionType.StartsWith("Excel.", StringComparison.Ordinal))
            .Select(c => c.RangeAddress)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

        string? activeCustomAppName = candidates
            .Where(c => c.SuggestedActionType.StartsWith("CustomApp.", StringComparison.Ordinal))
            .Select(c => c.TargetUrlOrWorkbook)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

        foreach (var cand in candidates)
        {
            if (cand.SuggestedActionType.StartsWith("Chrome.", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook))
            {
                activeBrowserUrl = cand.TargetUrlOrWorkbook;
                TryAddOriginRule(activeBrowserUrl, allowedOrigins);
            }
            else if (cand.SuggestedActionType.StartsWith("Excel.", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook))
                {
                    activeWorkbookPath = cand.TargetUrlOrWorkbook;
                    TryAddExcelDirectory(activeWorkbookPath, allowedExcelDirs);
                }
                if (!string.IsNullOrWhiteSpace(cand.SheetName))
                {
                    activeSheetName = cand.SheetName;
                }
                if (!string.IsNullOrWhiteSpace(cand.RangeAddress))
                {
                    activeRangeAddress = cand.RangeAddress;
                }
            }
            else if (cand.SuggestedActionType.StartsWith("CustomApp.", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook))
            {
                activeCustomAppName = cand.TargetUrlOrWorkbook;
            }

            string nodeId = $"step-{idx++}";
            switch (cand.SuggestedActionType)
            {
                case IrConstants.ActionChromeNavigate:
                {
                    string url = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeBrowserUrl ?? string.Empty);

                    TryAddOriginRule(url, allowedOrigins);

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
                    string fallbackLabel = !string.IsNullOrWhiteSpace(activeBrowserUrl) ? activeBrowserUrl : "textbox";
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeFill,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "textbox", Label = fallbackLabel, AccessibleName = fallbackLabel },
                            Value = cand.SanitizedValue ?? string.Empty,
                            IsSensitive = cand.IsSensitive
                        })
                    });
                    break;
                }
                case IrConstants.ActionChromeClick:
                {
                    string fallbackName = !string.IsNullOrWhiteSpace(activeBrowserUrl) ? activeBrowserUrl : "button";
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeClick,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = fallbackName, Label = fallbackName }
                        })
                    });
                    break;
                }
                case IrConstants.ActionExcelReadRange:
                {
                    string workbookPath = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeWorkbookPath ?? string.Empty);
                    string sheetName = !string.IsNullOrWhiteSpace(cand.SheetName)
                        ? cand.SheetName
                        : (activeSheetName ?? string.Empty);
                    string rangeAddr = !string.IsNullOrWhiteSpace(cand.RangeAddress)
                        ? cand.RangeAddress
                        : (activeRangeAddress ?? "A1");

                    TryAddExcelDirectory(workbookPath, allowedExcelDirs);

                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelReadRange,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
                        {
                            WorkbookPath = workbookPath,
                            SheetName = sheetName,
                            RangeAddress = rangeAddr
                        })
                    });
                    break;
                }
                case IrConstants.ActionChromeSubmit:
                {
                    string destUrl = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeBrowserUrl ?? string.Empty);

                    TryAddOriginRule(destUrl, allowedOrigins);

                    string fallbackName = !string.IsNullOrWhiteSpace(destUrl) ? destUrl : "submit";
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeSubmit,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ChromeSubmitArgs
                        {
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = fallbackName, Label = fallbackName },
                            DestinationUrl = destUrl,
                            ExpectedSuccessSelector = cand.Locator?.CssSelector ?? "#submit-success"
                        })
                    });
                    break;
                }
                case IrConstants.ActionExcelWriteRange:
                {
                    string workbookPath = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeWorkbookPath ?? string.Empty);
                    string sheetName = !string.IsNullOrWhiteSpace(cand.SheetName)
                        ? cand.SheetName
                        : (activeSheetName ?? string.Empty);
                    string rangeAddr = !string.IsNullOrWhiteSpace(cand.RangeAddress)
                        ? cand.RangeAddress
                        : (activeRangeAddress ?? "A1");
                    string valText = cand.SanitizedValue ?? string.Empty;
                    var parsedRows = ParseMatrixForRange(valText, rangeAddr);

                    TryAddExcelDirectory(workbookPath, allowedExcelDirs);

                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionExcelWriteRange,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                        {
                            WorkbookPath = workbookPath,
                            SheetName = sheetName,
                            RangeAddress = rangeAddr,
                            Values = parsedRows
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppClick:
                {
                    string appName = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeCustomAppName ?? cand.Locator?.Label ?? cand.Locator?.AccessibleName ?? string.Empty);
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppClick,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppClickArgs
                        {
                            ApplicationName = appName,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "button", AccessibleName = appName, Label = appName }
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppFill:
                {
                    string appName = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeCustomAppName ?? cand.Locator?.Label ?? cand.Locator?.AccessibleName ?? string.Empty);
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppFill,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppFillArgs
                        {
                            ApplicationName = appName,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "textbox", Label = appName, AccessibleName = appName },
                            Value = cand.SanitizedValue ?? string.Empty,
                            IsSensitive = cand.IsSensitive
                        })
                    });
                    break;
                }
                case IrConstants.ActionCustomAppReadText:
                {
                    string appName = !string.IsNullOrWhiteSpace(cand.TargetUrlOrWorkbook)
                        ? cand.TargetUrlOrWorkbook
                        : (activeCustomAppName ?? cand.Locator?.Label ?? cand.Locator?.AccessibleName ?? string.Empty);
                    children.Add(new WorkflowNode
                    {
                        NodeId = nodeId,
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionCustomAppReadText,
                        SourceCandidateIds = new List<string> { cand.CandidateId },
                        Args = JsonSerializer.SerializeToElement(new CustomAppReadTextArgs
                        {
                            ApplicationName = appName,
                            Locator = cand.Locator ?? new SemanticLocator { Role = "text", Label = appName, AccessibleName = appName }
                        })
                    });
                    break;
                }
            }
        }

        bool allowCoordClick = policy.AllowCoordinateClick ||
            candidates.Any(c => c.Locator is not null && c.Locator.UsesCoordinatesOnly());

        var wf = new WorkflowDefinition
        {
            SchemaVersion = IrConstants.CurrentSchemaVersion,
            WorkflowId = $"wf-ai-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Name = "記録から自動作成したマクロ",
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = allowedOrigins,
                AllowCoordinateClick = allowCoordClick,
                AllowExternalAi = policy.AllowExternalAi,
                AllowedExcelDirectories = allowedExcelDirs
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

    private static void TryAddOriginRule(string? rawUrl, List<OriginRule> allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return;
        if (Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            !allowedOrigins.Any(o => o.Matches(uri)))
        {
            string scheme = uri.Scheme.ToLowerInvariant();
            allowedOrigins.Add(new OriginRule
            {
                Scheme = scheme,
                Host = uri.Host,
                Port = uri.IsDefaultPort ? (scheme == "https" ? 443 : 80) : uri.Port
            });
        }
    }

    private static void TryAddExcelDirectory(string? workbookPath, List<string> allowedExcelDirs)
    {
        if (string.IsNullOrWhiteSpace(workbookPath)) return;
        string trimmed = workbookPath.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(trimmed);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir) &&
                !allowedExcelDirs.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
            {
                allowedExcelDirs.Add(dir);
            }
        }
        catch
        {
        }
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
                    : (rawLines.Count > 0 && rawLines[0].Count > 0 ? rawLines[0][0] : string.Empty);
                row.Add(cell);
            }
            matrix.Add(row);
        }
        return matrix;
    }
}
