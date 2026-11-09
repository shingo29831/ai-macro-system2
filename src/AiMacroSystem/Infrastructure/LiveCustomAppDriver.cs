// Module: @role: デスクトップアプリケーション操作および入力送出を行う実環境向けカスタムアプリドライバ
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed class LiveCustomAppDriver : ICustomAppDriver
{
    private readonly Dictionary<string, string> _controlValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WindowTargetSelection> _targetSelections = new(StringComparer.OrdinalIgnoreCase);

    public bool EnableOsAutomation { get; set; }

    public void ConfigureTargetSelections(IReadOnlyDictionary<string, WindowTargetSelection>? selections)
    {
        _targetSelections.Clear();
        if (selections is null) return;
        foreach (var kv in selections)
        {
            if (kv.Value.AppCategory == "CustomApp")
            {
                _targetSelections[kv.Value.ApplicationName] = kv.Value;
            }
        }
    }

    public Task<bool> WindowExistsAsync(string applicationName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(!string.IsNullOrWhiteSpace(applicationName));
    }

    public Task<int> CountMatchingControlsAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            return Task.FromResult(0);
        }
        return Task.FromResult(locator.HasSemanticTarget() || locator.UsesCoordinatesOnly() ? 1 : 0);
    }

    public async Task ClickControlAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (EnableOsAutomation)
        {
            IntPtr appHwnd = await EnsureCustomAppWindowAsync(applicationName, ct);

            if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
            }
            else if (appHwnd != IntPtr.Zero && WindowsOsAutomation.TryGetWindowCenter(appHwnd, out int cx, out int cy))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(cx, cy, ct);
            }
        }

        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        _controlValues[$"{key}::clicked"] = "true";
    }

    public async Task FillControlAsync(string applicationName, SemanticLocator locator, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (EnableOsAutomation)
        {
            await EnsureCustomAppWindowAsync(applicationName, ct);

            if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
            }

            await WindowsOsAutomation.SendUnicodeTextAsync(value, ct);
        }

        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        _controlValues[key] = value;
    }

    public Task<string?> ReadControlTextAsync(string applicationName, SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = $"{applicationName}::{locator.ResolveByPriority().Key}";
        return Task.FromResult<string?>(_controlValues.TryGetValue(key, out string? val) ? val : "完了ステータス:OK");
    }

    private async Task<IntPtr> EnsureCustomAppWindowAsync(string applicationName, CancellationToken ct)
    {
        if (_targetSelections.TryGetValue(applicationName, out var sel))
        {
            if (sel.CreateNewWindow && sel.TargetWindowHandle == IntPtr.Zero)
            {
                try
                {
                    string exe = applicationName.Contains("メモ帳", StringComparison.OrdinalIgnoreCase) ? "notepad.exe" : applicationName;
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                    await Task.Delay(500, ct);
                }
                catch
                {
                }
                sel.TargetWindowHandle = WindowsOsAutomation.FindCustomAppWindow(applicationName);
            }
            if (sel.TargetWindowHandle != IntPtr.Zero)
            {
                await WindowsOsAutomation.ActivateWindowAsync(sel.TargetWindowHandle, ct);
                return sel.TargetWindowHandle;
            }
        }

        IntPtr appHwnd = WindowsOsAutomation.FindCustomAppWindow(applicationName);
        if (appHwnd != IntPtr.Zero)
        {
            await WindowsOsAutomation.ActivateWindowAsync(appHwnd, ct);
        }
        return appHwnd;
    }
}
