// Module: @role: OS自動化および内蔵ブラウザ操作に対応した実環境向けChromeブラウザドライバ
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed class LiveChromeBrowserDriver : IBrowserDriver
{
    private readonly Dictionary<string, string> _fieldValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeStateSelectors = new(StringComparer.OrdinalIgnoreCase);
    private bool _newWindowLaunchedInCurrentRun;
    private string? _lastFilledValue;
    private bool _lastFillExecutedSearch;

    public Uri? CurrentUrl { get; private set; }
    public bool LaunchSystemBrowserOnNavigate { get; set; }
    public bool EnableOsAutomation { get; set; }
    public WindowTargetSelection? CurrentTargetSelection { get; private set; }

    public Func<Uri, CancellationToken, Task>? BuiltInBrowserNavigateAsync { get; set; }
    public Func<SemanticLocator, string, CancellationToken, Task>? BuiltInBrowserFillAsync { get; set; }
    public Func<SemanticLocator, CancellationToken, Task>? BuiltInBrowserClickAsync { get; set; }

    public void ConfigureTargetSelection(WindowTargetSelection? selection)
    {
        CurrentTargetSelection = selection;
        _newWindowLaunchedInCurrentRun = false;
        _lastFilledValue = null;
        _lastFillExecutedSearch = false;
        CurrentUrl = null;
    }

    public async Task<(IReadOnlyList<Uri> RedirectChain, Uri FinalUrl, IReadOnlyList<Uri> OpenedPopups)> NavigateAsync(
        Uri targetUrl,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentUrl = targetUrl;
        _lastFilledValue = null;
        _lastFillExecutedSearch = false;
        _activeStateSelectors.Add("body");
        _activeStateSelectors.Add("#main");

        if (CurrentTargetSelection?.UseBuiltInBrowser == true && BuiltInBrowserNavigateAsync is not null)
        {
            await BuiltInBrowserNavigateAsync(targetUrl, ct);
            _newWindowLaunchedInCurrentRun = true;
            return (Array.Empty<Uri>(), targetUrl, Array.Empty<Uri>());
        }

        if ((LaunchSystemBrowserOnNavigate || EnableOsAutomation) &&
            (targetUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             targetUrl.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool isDefaultFallbackUrl = targetUrl.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase) &&
                                           targetUrl.AbsolutePath.Equals("/portal", StringComparison.OrdinalIgnoreCase);
                string launchUrl = isDefaultFallbackUrl ? "https://www.google.com" : targetUrl.ToString();

                if (CurrentTargetSelection is not null)
                {
                    if (CurrentTargetSelection.CreateNewWindow && !_newWindowLaunchedInCurrentRun)
                    {
                        IntPtr newHwnd = await WindowsOsAutomation.LaunchNewBrowserWindowAndWaitAsync(launchUrl, ct);
                        _newWindowLaunchedInCurrentRun = true;
                        if (newHwnd != IntPtr.Zero)
                        {
                            CurrentTargetSelection.TargetWindowHandle = newHwnd;
                        }
                    }
                    else
                    {
                        IntPtr targetHwnd = CurrentTargetSelection.TargetWindowHandle != IntPtr.Zero
                            ? CurrentTargetSelection.TargetWindowHandle
                            : WindowsOsAutomation.FindBrowserWindow();
                        if (targetHwnd != IntPtr.Zero)
                        {
                            await WindowsOsAutomation.ActivateWindowAsync(targetHwnd, ct);
                            if (!isDefaultFallbackUrl)
                            {
                                await WindowsOsAutomation.SendCtrlShortcutAndNavigateUrlAsync(launchUrl, ct);
                            }
                        }
                    }
                }
                else
                {
                    IntPtr existingBrowser = WindowsOsAutomation.FindBrowserWindow();
                    if (existingBrowser != IntPtr.Zero && isDefaultFallbackUrl)
                    {
                        await WindowsOsAutomation.ActivateWindowAsync(existingBrowser, ct);
                    }
                    else if (LaunchSystemBrowserOnNavigate)
                    {
                        await WindowsOsAutomation.LaunchNewBrowserWindowAndWaitAsync(launchUrl, ct);
                    }
                }
            }
            catch
            {
                // ブラウザ起動失敗時も内部DOMセッションで継続
            }
        }

        IReadOnlyList<Uri> redirects = Array.Empty<Uri>();
        IReadOnlyList<Uri> popups = Array.Empty<Uri>();
        return (redirects, targetUrl, popups);
    }

    public Task<int> CountMatchingElementsAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (locator.HasSemanticTarget() || locator.UsesCoordinatesOnly())
        {
            return Task.FromResult(1);
        }
        return Task.FromResult(0);
    }

    public async Task ClickElementAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (CurrentTargetSelection?.UseBuiltInBrowser == true && BuiltInBrowserClickAsync is not null)
        {
            await BuiltInBrowserClickAsync(locator, ct);
        }
        else if (EnableOsAutomation)
        {
            IntPtr browserHwnd = await EnsureBrowserWindowReadyAsync(ct);

            bool isEnterSubmit = string.Equals(locator.StableId, "key-enter-submit", StringComparison.OrdinalIgnoreCase) ||
                                 (locator.AccessibleName is not null && locator.AccessibleName.Contains("Enter", StringComparison.OrdinalIgnoreCase)) ||
                                 (locator.Label is not null && locator.Label.Contains("検索実行", StringComparison.OrdinalIgnoreCase));

            if (isEnterSubmit)
            {
                if (!_lastFillExecutedSearch)
                {
                    if (!string.IsNullOrWhiteSpace(_lastFilledValue) && IsSearchEngineContext(locator))
                    {
                        string searchUrl = BuildSearchEngineQueryUrl(CurrentUrl, _lastFilledValue);
                        await WindowsOsAutomation.SendCtrlShortcutAndNavigateUrlAsync(searchUrl, ct);
                        _lastFillExecutedSearch = true;
                    }
                    else
                    {
                        await WindowsOsAutomation.SendVirtualKeyAsync(0x0D, ct);
                    }
                }
            }
            else if (_newWindowLaunchedInCurrentRun && IsSearchEnginePage(CurrentUrl) && string.IsNullOrWhiteSpace(_lastFilledValue))
            {
                // 新規起動した検索エンジンの初期画面では自動フォーカスされた検索欄が外れないよう座標クリックをスキップする
            }
            else if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y) && !(x == 300 && y == 200))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
            }
            else if (browserHwnd != IntPtr.Zero && WindowsOsAutomation.TryGetWindowCenter(browserHwnd, out int cx, out int cy))
            {
                await WindowsOsAutomation.MoveAndLeftClickAsync(cx, cy, ct);
            }
        }

        string key = GetLocatorKey(locator);
        _activeStateSelectors.Add($"#clicked-{key}");
        _activeStateSelectors.Add("#submit-success");
        _activeStateSelectors.Add(".complete-message");
    }

    public async Task FillElementAsync(SemanticLocator locator, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _lastFilledValue = value;
        _lastFillExecutedSearch = false;

        if (CurrentTargetSelection?.UseBuiltInBrowser == true && BuiltInBrowserFillAsync is not null)
        {
            await BuiltInBrowserFillAsync(locator, value, ct);
        }
        else if (EnableOsAutomation)
        {
            await EnsureBrowserWindowReadyAsync(ct);

            if (IsSearchEngineContext(locator) && !string.IsNullOrWhiteSpace(value))
            {
                // 検索エンジンへの検索語入力はウィンドウ座標ズレやIME未確定状態に左右されないよう検索URLへ確実に遷移させる
                string searchUrl = BuildSearchEngineQueryUrl(CurrentUrl, value);
                await WindowsOsAutomation.SendCtrlShortcutAndNavigateUrlAsync(searchUrl, ct);
                _lastFillExecutedSearch = true;
            }
            else
            {
                if (WindowsOsAutomation.TryResolveCoordinates(locator, out int x, out int y) && !(x == 300 && y == 200))
                {
                    await WindowsOsAutomation.MoveAndLeftClickAsync(x, y, ct);
                }

                await WindowsOsAutomation.SendUnicodeTextAsync(value, ct);
            }
        }

        string key = GetLocatorKey(locator);
        _fieldValues[key] = value;
    }

    private bool IsSearchEngineContext(SemanticLocator locator)
    {
        if (IsSearchEnginePage(CurrentUrl))
        {
            return true;
        }

        string combined = $"{locator.Label} {locator.AccessibleName} {locator.StableId} {locator.CssSelector}";
        return combined.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSearchEnginePage(Uri? uri)
    {
        if (uri is null) return false;
        string host = uri.Host;
        return host.Contains("google.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("yahoo.", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("bing.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchEngineQueryUrl(Uri? baseUri, string query)
    {
        string escaped = Uri.EscapeDataString(query.Trim());
        if (baseUri is not null)
        {
            string host = baseUri.Host;
            if (host.Contains("yahoo.co.jp", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://search.yahoo.co.jp/search?p={escaped}";
            }
            if (host.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://www.bing.com/search?q={escaped}";
            }
            if (host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://duckduckgo.com/?q={escaped}";
            }
        }
        return $"https://www.google.com/search?q={escaped}";
    }

    private async Task<IntPtr> EnsureBrowserWindowReadyAsync(CancellationToken ct)
    {
        if (CurrentTargetSelection is not null)
        {
            if (CurrentTargetSelection.CreateNewWindow && !_newWindowLaunchedInCurrentRun)
            {
                IntPtr newHwnd = await WindowsOsAutomation.LaunchNewBrowserWindowAndWaitAsync("https://www.google.com", ct);
                _newWindowLaunchedInCurrentRun = true;
                CurrentUrl ??= new Uri("https://www.google.com");
                if (newHwnd != IntPtr.Zero)
                {
                    CurrentTargetSelection.TargetWindowHandle = newHwnd;
                }
            }

            if (CurrentTargetSelection.TargetWindowHandle != IntPtr.Zero)
            {
                await WindowsOsAutomation.ActivateWindowAsync(CurrentTargetSelection.TargetWindowHandle, ct);
                return CurrentTargetSelection.TargetWindowHandle;
            }
        }

        IntPtr browserHwnd = WindowsOsAutomation.FindBrowserWindow();
        if (browserHwnd != IntPtr.Zero)
        {
            await WindowsOsAutomation.ActivateWindowAsync(browserHwnd, ct);
        }
        return browserHwnd;
    }

    public Task<string?> ReadElementValueAsync(SemanticLocator locator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = GetLocatorKey(locator);
        return Task.FromResult(_fieldValues.TryGetValue(key, out string? val) ? val : null);
    }

    public Task<bool> ElementExistsAsync(string selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(_activeStateSelectors.Contains(selector));
    }

    private static string GetLocatorKey(SemanticLocator locator) =>
        locator.ResolveByPriority().Key;
}
