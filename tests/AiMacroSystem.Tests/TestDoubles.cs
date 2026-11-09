// Module: @role: 統合テスト用のブラウザ・Excel・AIモックおよびテストダブル定義
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Tests;

internal sealed class FakeBrowserDriver : IBrowserDriver
{
    public Uri? CurrentUrl { get; set; }
    public List<Uri> NextRedirectChain { get; set; } = new();
    public List<Uri> NextPopups { get; set; } = new();
    public int MatchingCount { get; set; } = 1;
    public bool ThrowOnClick { get; set; }
    public bool PostconditionElementExists { get; set; } = true;
    public string? ReadbackOverride { get; set; }
    public int FillCallCount { get; private set; }
    public int SubmitCallCount { get; private set; }
    private string? _lastFilled;

    public Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(Uri targetUrl, CancellationToken ct)
    {
        CurrentUrl = NextRedirectChain.Count > 0 ? NextRedirectChain[^1] : targetUrl;
        return Task.FromResult<(IReadOnlyList<Uri>, Uri, IReadOnlyList<Uri>)>((NextRedirectChain, CurrentUrl, NextPopups));
    }

    public Task<int> CountMatchingElementsAsync(SemanticLocator locator, CancellationToken ct) =>
        Task.FromResult(MatchingCount);

    public Task ClickElementAsync(SemanticLocator locator, CancellationToken ct)
    {
        SubmitCallCount++;
        if (ThrowOnClick)
        {
            throw new IOException("Simulated network drop during click/submit.");
        }
        return Task.CompletedTask;
    }

    public Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct)
    {
        FillCallCount++;
        _lastFilled = value;
        return Task.CompletedTask;
    }

    public Task<string?> ReadElementValueAsync(SemanticLocator locator, CancellationToken ct) =>
        Task.FromResult(ReadbackOverride ?? _lastFilled);

    public Task<bool> ElementExistsAsync(string selector, CancellationToken ct) =>
        Task.FromResult(PostconditionElementExists);
}

internal sealed class FakeExcelComDriver : IExcelComDriver
{
    public bool WorkbookAccessible { get; set; } = true;
    public bool MultipleWindows { get; set; }
    public bool IsReadOnly { get; set; }
    public bool SheetPresent { get; set; } = true;
    public bool SheetProtected { get; set; }
    public bool ThrowComOnWrite { get; set; }
    public bool CorruptReadback { get; set; }
    public int WriteRange2DCallCount { get; private set; }
    private string[,] _storage = new string[1, 1] { { "" } };

    public bool WorkbookExistsAndAccessible(string workbookPath, out bool isOpenInMultipleWindows, out bool isReadOnly)
    {
        isOpenInMultipleWindows = MultipleWindows;
        isReadOnly = IsReadOnly;
        return WorkbookAccessible;
    }

    public bool SheetExists(string workbookPath, string sheetName, out bool isProtected)
    {
        isProtected = SheetProtected;
        return SheetPresent;
    }

    public string[,] ReadRange2D(string workbookPath, string sheetName, string rangeAddress)
    {
        if (CorruptReadback)
        {
            return new string[1, 1] { { "CORRUPTED_VALUE" } };
        }
        return _storage;
    }

    public void WriteRange2D(string workbookPath, string sheetName, string rangeAddress, string[,] values)
    {
        WriteRange2DCallCount++;
        if (ThrowComOnWrite)
        {
            throw new InvalidOperationException("RPC_E_SERVERFAULT (0x80010105)");
        }
        _storage = values;
    }
}

internal sealed class FakeAiProvider : IAiProvider
{
    public string ProviderName { get; init; } = "LocalOllama";
    public string ModelId { get; init; } = "llama3.1:8b";
    public bool IsExternal { get; init; }
    public bool IsAvailable { get; set; } = true;
    public Queue<string> Responses { get; } = new();
    public List<IReadOnlyList<NormalizedActionCandidate>> ReceivedCandidates { get; } = new();

    public Task<string> GenerateWorkflowIrJsonAsync(
        IReadOnlyList<NormalizedActionCandidate> candidates,
        PolicyDeclaration policy,
        IReadOnlyList<ValidationIssue>? previousValidationIssues,
        CancellationToken ct)
    {
        ReceivedCandidates.Add(candidates);
        string next = Responses.Count > 0 ? Responses.Dequeue() : "{}";
        return Task.FromResult(next);
    }
}
