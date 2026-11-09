// Module: @role: 仕様書第18章の受け入れテスト基盤・ライフサイクル・テスト用ワークフロー定義
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AiMacroSystem.Tests;

public sealed partial class SpecificationAcceptanceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRepository _repository;
    private readonly IrValidator _validator;
    private readonly PolicyEngine _policyEngine;
    private readonly ApprovalService _approvalService;
    private readonly WorkflowManagementService _workflowService;
    private readonly FakeBrowserDriver _browserDriver;
    private readonly FakeExcelComDriver _excelDriver;
    private readonly Dictionary<string, IActionAdapter> _adapters;
    private readonly RecoveryService _recoveryService;
    private readonly ExecutionEngine _executionEngine;

    public SpecificationAcceptanceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aimacro-test-{Guid.NewGuid():N}.sqlite");
        _repository = new SqliteRepository(_dbPath);
        _validator = new IrValidator();
        _policyEngine = new PolicyEngine(_validator);
        _approvalService = new ApprovalService();
        _workflowService = new WorkflowManagementService(_repository, _validator);
        _browserDriver = new FakeBrowserDriver();
        _excelDriver = new FakeExcelComDriver();

        var chromeAdapter = new ChromeAdapter(_browserDriver);
        var excelAdapter = new ExcelAdapter(_excelDriver);
        var customAppAdapter = new CustomAppAdapter(new LiveCustomAppDriver());

        _adapters = new Dictionary<string, IActionAdapter>(StringComparer.Ordinal)
        {
            [IrConstants.ActionChromeNavigate] = chromeAdapter,
            [IrConstants.ActionChromeClick] = chromeAdapter,
            [IrConstants.ActionChromeFill] = chromeAdapter,
            [IrConstants.ActionChromeSubmit] = chromeAdapter,
            [IrConstants.ActionExcelReadRange] = excelAdapter,
            [IrConstants.ActionExcelWriteRange] = excelAdapter,
            [IrConstants.ActionCustomAppClick] = customAppAdapter,
            [IrConstants.ActionCustomAppFill] = customAppAdapter,
            [IrConstants.ActionCustomAppReadText] = customAppAdapter
        };

        _recoveryService = new RecoveryService(_repository, _adapters);
        _executionEngine = new ExecutionEngine(_repository, _policyEngine, _approvalService, _recoveryService, _adapters);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // Ignore cleanup lock in test runner
        }
    }

    private static WorkflowDefinition CreateValidSampleDefinition()
    {
        return new WorkflowDefinition
        {
            SchemaVersion = "0.7",
            WorkflowId = "wf-sample",
            Name = "Sample Workflow",
            Version = 1,
            Policy = new PolicyDeclaration
            {
                AllowedOrigins = new List<OriginRule>
                {
                    new() { Scheme = "https", Host = "example.com", Port = 443 }
                },
                AllowCoordinateClick = false,
                AllowExternalAi = false
            },
            Root = new WorkflowNode
            {
                NodeId = "root",
                Type = IrConstants.NodeTypeSequence,
                Children = new List<WorkflowNode>
                {
                    new()
                    {
                        NodeId = "nav-1",
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeNavigate,
                        Args = JsonSerializer.SerializeToElement(new ChromeNavigateArgs
                        {
                            Url = "https://example.com/form"
                        })
                    },
                    new()
                    {
                        NodeId = "fill-1",
                        Type = IrConstants.NodeTypeAction,
                        ActionType = IrConstants.ActionChromeFill,
                        Args = JsonSerializer.SerializeToElement(new ChromeFillArgs
                        {
                            Locator = new SemanticLocator { Role = "textbox", AccessibleName = "Username" },
                            Value = "alice"
                        })
                    }
                }
            }
        };
    }
}
