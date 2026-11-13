// Module: @role: EasyStepItemとWorkflow IRノード間の相互変換およびノード引数生成を担当するPartialクラス
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Presentation;

public sealed partial class MainViewModel
{
    private EasyStepItem CreateStepItemFromInputs(
        int stepNumber,
        string actionKind,
        string url,
        string elementName,
        string fillVal,
        bool isSens,
        string excelPath,
        string sheet,
        string range,
        string excelText,
        List<string>? sourceCandidateIds,
        SemanticLocator? preservedLocator = null)
    {
        string customAppName = string.IsNullOrWhiteSpace(StepCustomAppNameInput) ? "基幹業務システム" : StepCustomAppNameInput.Trim();
        string safeElem = string.IsNullOrWhiteSpace(elementName) ? "対象要素" : elementName.Trim();
        string safeUrl = string.IsNullOrWhiteSpace(url) ? "https://example.com/portal" : url.Trim();
        string safeRange = string.IsNullOrWhiteSpace(range) ? "A1" : range.Trim().Replace("$", "").ToUpperInvariant();
        string nodeId = $"step-{stepNumber}";
        WorkflowNode node;

        switch (actionKind)
        {
            case IrConstants.ActionChromeNavigate:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeNavigate,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs { Url = safeUrl })
                };
                break;

            case IrConstants.ActionChromeFill:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeFill,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                    {
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("textbox", safeElem, preservedLocator),
                        Value = isSens ? SensitiveDataProtector.RedactedPlaceholder : fillVal,
                        IsSensitive = isSens
                    })
                };
                break;

            case IrConstants.ActionChromeClick:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeClick,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeClickArgs
                    {
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("button", safeElem, preservedLocator)
                    })
                };
                break;

            case IrConstants.ActionChromeSubmit:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionChromeSubmit,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ChromeSubmitArgs
                    {
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("button", safeElem, preservedLocator),
                        DestinationUrl = safeUrl,
                        ExpectedSuccessSelector = "#submit-success"
                    })
                };
                break;

            case IrConstants.ActionExcelReadRange:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionExcelReadRange,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ExcelReadRangeArgs
                    {
                        WorkbookPath = excelPath.Trim(),
                        SheetName = sheet.Trim(),
                        RangeAddress = safeRange
                    })
                };
                break;

            case IrConstants.ActionCustomAppClick:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionCustomAppClick,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new CustomAppClickArgs
                    {
                        ApplicationName = customAppName,
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("button", safeElem, preservedLocator)
                    })
                };
                break;

            case IrConstants.ActionCustomAppFill:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionCustomAppFill,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new CustomAppFillArgs
                    {
                        ApplicationName = customAppName,
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("textbox", safeElem, preservedLocator),
                        Value = isSens ? SensitiveDataProtector.RedactedPlaceholder : fillVal,
                        IsSensitive = isSens
                    })
                };
                break;

            case IrConstants.ActionCustomAppReadText:
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionCustomAppReadText,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new CustomAppReadTextArgs
                    {
                        ApplicationName = customAppName,
                        Locator = StepBlockFormatter.MergeLocatorWithPreservedMetadata("text", safeElem, preservedLocator)
                    })
                };
                break;

            default:
            {
                var rows = StepBlockFormatter.AdjustMatrixToRange(StepBlockFormatter.ParseCsvMatrix(excelText), safeRange);
                node = new WorkflowNode
                {
                    NodeId = nodeId,
                    Type = IrConstants.NodeTypeAction,
                    ActionType = IrConstants.ActionExcelWriteRange,
                    SourceCandidateIds = sourceCandidateIds,
                    Args = JsonSerializer.SerializeToElement(new ExcelWriteRangeArgs
                    {
                        WorkbookPath = excelPath.Trim(),
                        SheetName = sheet.Trim(),
                        RangeAddress = safeRange,
                        Values = rows
                    })
                };
                break;
            }
        }

        return BuildEasyStepItemFromNode(node, stepNumber, false);
    }

    private void RebuildIrJsonFromEasySteps()
    {
        var origins = new List<OriginRule>();
        var excelDirs = new List<string>();
        var children = new List<WorkflowNode>();

        if (!string.IsNullOrWhiteSpace(EditorIrJson))
        {
            try
            {
                var prevDef = JsonSerializer.Deserialize<WorkflowDefinition>(EditorIrJson);
                if (prevDef?.Policy is not null)
                {
                    foreach (var prevOrigin in prevDef.Policy.AllowedOrigins)
                    {
                        if (!origins.Any(o =>
                            string.Equals(o.Scheme, prevOrigin.Scheme, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(o.Host, prevOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
                            o.Port == prevOrigin.Port))
                        {
                            origins.Add(prevOrigin);
                        }
                    }

                    foreach (var prevDir in prevDef.Policy.AllowedExcelDirectories)
                    {
                        TryRegisterExcelDirectory(prevDir, excelDirs);
                    }
                }
            }
            catch
            {
            }
        }

        int num = 1;
        int total = EasySteps.Count;
        foreach (var item in EasySteps)
        {
            item.StepNumber = num;
            item.UnderlyingNode.NodeId = $"step-{num}";
            item.HasNextBlock = num < total;
            num++;
            children.Add(item.UnderlyingNode);

            if (item.UnderlyingNode.Args.HasValue)
            {
                if (item.ActionType == IrConstants.ActionChromeNavigate)
                {
                    var nav = item.UnderlyingNode.Args.Value.Deserialize<ChromeNavigateArgs>();
                    TryRegisterHttpOrigin(nav?.Url, origins);
                }
                else if (item.ActionType == IrConstants.ActionChromeSubmit)
                {
                    var sub = item.UnderlyingNode.Args.Value.Deserialize<ChromeSubmitArgs>();
                    TryRegisterHttpOrigin(sub?.DestinationUrl, origins);
                }
                else if (item.ActionType == IrConstants.ActionExcelReadRange)
                {
                    var read = item.UnderlyingNode.Args.Value.Deserialize<ExcelReadRangeArgs>();
                    TryRegisterExcelDirectory(read?.WorkbookPath, excelDirs);
                }
                else if (item.ActionType == IrConstants.ActionExcelWriteRange)
                {
                    var write = item.UnderlyingNode.Args.Value.Deserialize<ExcelWriteRangeArgs>();
                    TryRegisterExcelDirectory(write?.WorkbookPath, excelDirs);
                }
            }

            foreach (var tracedEv in _workflowService.TraceNodeToRawEvents(item.UnderlyingNode))
            {
                TryRegisterHttpOrigin(tracedEv.UrlOrWorkbookPath, origins);
                TryRegisterExcelDirectory(tracedEv.UrlOrWorkbookPath, excelDirs);
            }
        }

        foreach (var rawEv in _recordingService.GetRecordedEvents())
        {
            TryRegisterHttpOrigin(rawEv.UrlOrWorkbookPath, origins);
            TryRegisterExcelDirectory(rawEv.UrlOrWorkbookPath, excelDirs);
        }

        if (_liveBrowserDriver?.CurrentUrl is not null)
        {
            TryRegisterHttpOrigin(_liveBrowserDriver.CurrentUrl.ToString(), origins);
        }

        if (!string.IsNullOrWhiteSpace(AllowedOriginHostInput))
        {
            TryRegisterHttpOrigin(AllowedOriginHostInput, origins);
        }

        if (!string.IsNullOrWhiteSpace(StepUrlInput) && origins.Count == 0)
        {
            TryRegisterHttpOrigin(StepUrlInput, origins);
        }

        if (!string.IsNullOrWhiteSpace(AllowedExcelDirectoryInput))
        {
            TryRegisterExcelDirectory(AllowedExcelDirectoryInput, excelDirs);
        }

        if (!string.IsNullOrWhiteSpace(StepExcelPathInput))
        {
            TryRegisterExcelDirectory(StepExcelPathInput, excelDirs);
        }

        var def = new WorkflowDefinition
        {
            SchemaVersion = IrConstants.CurrentSchemaVersion,
            WorkflowId = string.IsNullOrWhiteSpace(WorkflowIdInput) ? "wf-custom" : WorkflowIdInput.Trim(),
            Name = string.IsNullOrWhiteSpace(WorkflowNameInput) ? "マイマクロ" : WorkflowNameInput.Trim(),
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = origins,
                AllowCoordinateClick = AllowCoordinateClickPolicy,
                AllowExternalAi = AllowExternalAiPolicy,
                AllowedExcelDirectories = excelDirs
            },
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = children
            }
        };

        EditorIrJson = JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true });
        _currentSavedVersionId = null;
        _currentApprovalId = null;
        CurrentApprovalStatusText = "手順が変更されたため、実行前に再承認が必要です。";
        ValidateCurrentEditorIr();
    }

    private void RebuildEasyStepsFromIrJson(string irJson)
    {
        try
        {
            var def = JsonSerializer.Deserialize<WorkflowDefinition>(irJson);
            if (def?.Root is null) return;

            var actionNodes = new List<WorkflowNode>();
            StepBlockFormatter.CollectActionNodesRecursive(def.Root, actionNodes);
            if (actionNodes.Count == 0) return;

            EasySteps.Clear();
            SelectedStepForEdit = null;
            int total = actionNodes.Count;
            for (int i = 0; i < total; i++)
            {
                var node = actionNodes[i];
                var stepItem = BuildEasyStepItemFromNode(node, i + 1, i + 1 < total);
                EasySteps.Add(stepItem);
            }
            VisualBlocksUpdated?.Invoke();
        }
        catch
        {
        }
    }

    private EasyStepItem BuildEasyStepItemFromNode(WorkflowNode node, int stepNumber, bool hasNextBlock)
    {
        string actionType = node.ActionType ?? IrConstants.ActionChromeClick;
        var (title, detail) = StepBlockFormatter.DescribeActionNode(node);
        var (icon, category, colorHex) = StepBlockFormatter.GetBlockVisualMeta(actionType);
        var (appBadge, targetLoc, opValue) = StepBlockFormatter.ExtractStructuredBlockInfo(node);

        string sourceLogSummary = "ブロック編集により構成";
        var tracedEvents = _workflowService.TraceNodeToRawEvents(node);
        if (tracedEvents.Count > 0)
        {
            var distinctSources = tracedEvents
                .Select(e => e.LogSource)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            sourceLogSummary = $"抽出元ログ ({tracedEvents.Count}件統合): {string.Join(" + ", distinctSources)}";
        }
        else if (node.SourceCandidateIds is { Count: > 0 })
        {
            var memEvents = _recordingService.GetRecordedEvents();
            if (memEvents.Count > 0)
            {
                var distinctSources = memEvents
                    .Select(e => e.LogSource)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                sourceLogSummary = $"抽出元ログ: {string.Join(" + ", distinctSources)}";
            }
        }

        return new EasyStepItem
        {
            StepNumber = stepNumber,
            NodeId = string.IsNullOrWhiteSpace(node.NodeId) ? $"step-{stepNumber}" : node.NodeId,
            ActionType = actionType,
            FriendlyTitle = title,
            FriendlyDetail = detail,
            UnderlyingNode = node,
            BlockIcon = icon,
            BlockCategory = category,
            AccentColorHex = colorHex,
            TargetAppBadge = appBadge,
            TargetLocationSummary = targetLoc,
            OperationValueSummary = opValue,
            SourceLogSummary = sourceLogSummary,
            HasNextBlock = hasNextBlock
        };
    }

    private static void TryRegisterHttpOrigin(string? rawUrlOrHost, List<OriginRule> origins)
    {
        if (string.IsNullOrWhiteSpace(rawUrlOrHost)) return;
        string trimmed = rawUrlOrHost.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            if (trimmed.Contains('.', StringComparison.Ordinal) &&
                !trimmed.Contains('\\', StringComparison.Ordinal) &&
                !trimmed.Contains(' ', StringComparison.Ordinal) &&
                !trimmed.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out Uri? httpsUri))
            {
                uri = httpsUri;
            }
            else
            {
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(uri.Host)) return;

        string scheme = uri.Scheme.ToLowerInvariant();
        int port = uri.IsDefaultPort ? (scheme == "https" ? 443 : 80) : uri.Port;

        if (!origins.Any(o =>
            string.Equals(o.Scheme, scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
            o.Port == port))
        {
            origins.Add(new OriginRule
            {
                Scheme = scheme,
                Host = uri.Host,
                Port = port
            });
        }
    }

    private static void TryRegisterExcelDirectory(string? rawPath, List<string> excelDirs)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        string trimmed = rawPath.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string ext = Path.GetExtension(trimmed);
        bool isExcelFile = ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".csv", StringComparison.OrdinalIgnoreCase);

        if (!isExcelFile && !Path.IsPathRooted(trimmed))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(trimmed);
            string? dir = isExcelFile || Path.HasExtension(fullPath)
                ? Path.GetDirectoryName(fullPath)
                : fullPath;
            if (!string.IsNullOrWhiteSpace(dir) &&
                !excelDirs.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
            {
                excelDirs.Add(dir);
            }
        }
        catch
        {
        }
    }
}
