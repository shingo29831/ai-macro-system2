// Module: @role: アプリケーションエントリポイント・依存関係注入・起動時リカバリー初期化
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;
using AiMacroSystem.Infrastructure;
using AiMacroSystem.Presentation;

namespace AiMacroSystem;

public sealed class DisconnectedBrowserDriver : IBrowserDriver
{
    public Uri? CurrentUrl => null;
    public Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(Uri targetUrl, CancellationToken ct) =>
        throw new InvalidOperationException("Playwright browser session is not attached.");
    public Task<int> CountMatchingElementsAsync(SemanticLocator locator, CancellationToken ct) => Task.FromResult(0);
    public Task ClickElementAsync(SemanticLocator locator, CancellationToken ct) =>
        throw new InvalidOperationException("Playwright browser session is not attached.");
    public Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct) =>
        throw new InvalidOperationException("Playwright browser session is not attached.");
    public Task<string?> ReadElementValueAsync(SemanticLocator locator, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<bool> ElementExistsAsync(string selector, CancellationToken ct) => Task.FromResult(false);
}

public sealed class DisconnectedExcelDriver : IExcelComDriver
{
    public bool WorkbookExistsAndAccessible(string workbookPath, out bool isOpenInMultipleWindows, out bool isReadOnly)
    {
        isOpenInMultipleWindows = false;
        isReadOnly = false;
        return File.Exists(workbookPath);
    }

    public bool SheetExists(string workbookPath, string sheetName, out bool isProtected)
    {
        isProtected = false;
        return false;
    }

    public string[,] ReadRange2D(string workbookPath, string sheetName, string rangeAddress) =>
        throw new InvalidOperationException("Excel COM session is not attached.");

    public void WriteRange2D(string workbookPath, string sheetName, string rangeAddress, string[,] values) =>
        throw new InvalidOperationException("Excel COM session is not attached.");
}

public sealed class App : System.Windows.Application
{
    [STAThread]
    public static void Main()
    {
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiMacroSystem");
        Directory.CreateDirectory(appDataDir);
        string dbPath = Path.Combine(appDataDir, "aimacro-v07.sqlite");

        var repository = new SqliteRepository(dbPath);
        var validator = new IrValidator();
        var policyEngine = new PolicyEngine(validator);
        var approvalService = new ApprovalService();
        var workflowService = new WorkflowManagementService(repository, validator);
        var recordingService = new RecordingService(repository);
        var normalizationService = new NormalizationService(repository);

        var liveBrowserDriver = new LiveChromeBrowserDriver
        {
            EnableOsAutomation = true,
            LaunchSystemBrowserOnNavigate = true
        };
        var liveExcelDriver = new LiveExcelDriver();
        var liveCustomAppDriver = new LiveCustomAppDriver
        {
            EnableOsAutomation = true
        };
        var liveLogMonitor = new WindowsLiveLogMonitor();
        var localAiProvider = new LocalBuiltInAiProvider();

        var chromeAdapter = new ChromeAdapter(liveBrowserDriver);
        var excelAdapter = new ExcelAdapter(liveExcelDriver);
        var customAppAdapter = new CustomAppAdapter(liveCustomAppDriver);

        var adapters = new Dictionary<string, IActionAdapter>(StringComparer.Ordinal)
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

        var recoveryService = new RecoveryService(repository, adapters);
        var executionEngine = new ExecutionEngine(repository, policyEngine, approvalService, recoveryService, adapters);
        var aiOrchestrator = new AiOrchestrator(localAiProvider, null, validator, repository);

        var viewModel = new MainViewModel(
            repository,
            workflowService,
            recordingService,
            normalizationService,
            policyEngine,
            approvalService,
            executionEngine,
            recoveryService,
            aiOrchestrator,
            liveBrowserDriver,
            liveLogMonitor,
            liveCustomAppDriver);

        var app = new App();
        var window = new MainWindow(viewModel);
        window.Loaded += async (_, _) => await viewModel.InitializeAndCheckRecoveryAsync();
        app.Run(window);
    }
}
