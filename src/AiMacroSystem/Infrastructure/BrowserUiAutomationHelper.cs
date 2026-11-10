// Module: @role: UIAutomationを用いたブラウザ要素情報検査およびタイトルからのURL/検索クエリ推定
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace AiMacroSystem.Infrastructure;

public static class BrowserUiAutomationHelper
{
    private static readonly Regex _searchTitleRegex = new(
        @"^(?<query>.+?)\s*[-|―]\s*(?:Google\s*検索|Google\s*Search|Yahoo!検索|検索|Bing|YouTube|Amazon|楽天市場|DuckDuckGo)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string? ElementName, string? ElementRole, string? CurrentValue) TryInspectFocusedOrPointElement(int x, int y)
    {
        string? name = null;
        string? role = null;
        string? val = null;

        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    AutomationElement? elem = AutomationElement.FocusedElement;
                    if (elem is null && x > 0 && y > 0)
                    {
                        elem = AutomationElement.FromPoint(new Point(x, y));
                    }
                    if (elem is null) return;

                    string rawName = elem.Current.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(rawName) &&
                        !rawName.Contains("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase) &&
                        rawName.Length <= 60)
                    {
                        name = rawName.Trim();
                    }

                    var ctrlType = elem.Current.ControlType;
                    if (ctrlType == ControlType.Edit || ctrlType == ControlType.ComboBox)
                    {
                        role = "textbox";
                    }
                    else if (ctrlType == ControlType.Button || ctrlType == ControlType.Hyperlink)
                    {
                        role = "button";
                    }

                    if (elem.TryGetCurrentPattern(ValuePattern.Pattern, out object? patternObj) &&
                        patternObj is ValuePattern valuePattern)
                    {
                        string? v = valuePattern.Current.Value;
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            val = v;
                        }
                    }
                }
                catch
                {
                    // UIAutomation非対応要素は無視
                }
            });
            task.Wait(90);
        }
        catch
        {
            // タイムアウト時はフォールバック識別子を使用
        }

        return (name, role, val);
    }

    public static string InferBrowserUrlOrSearchEngine(string windowTitle, out string? extractedSearchQuery)
    {
        extractedSearchQuery = null;
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return "https://www.google.com";
        }

        string trimmed = windowTitle.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? directUri) &&
            (directUri.Scheme == "https" || directUri.Scheme == "http"))
        {
            return directUri.ToString();
        }

        var searchMatch = _searchTitleRegex.Match(trimmed);
        if (searchMatch.Success)
        {
            string q = searchMatch.Groups["query"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                extractedSearchQuery = q;
            }
        }

        if (trimmed.Contains("Yahoo", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.yahoo.co.jp";
        }
        if (trimmed.Contains("Bing", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.bing.com";
        }
        if (trimmed.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.youtube.com";
        }
        if (trimmed.Contains("Google", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("新しいタブ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("New Tab", StringComparison.OrdinalIgnoreCase))
        {
            return "https://www.google.com";
        }

        return "https://www.google.com";
    }
}
