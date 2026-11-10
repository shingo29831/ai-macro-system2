// Module: @role: Win32 APIを用いたウィンドウ検出・起動・アクティベーションおよび座標計算
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

internal static partial class WindowsOsAutomation
{
    private static readonly Regex _coordInTextRegex = new(@"\((\d+)\s*,\s*(\d+)\)|-(\d+)-(\d+)$", RegexOptions.Compiled);

    public static HashSet<IntPtr> GetAllBrowserWindowHandles()
    {
        var handles = new HashSet<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd) && IsBrowserWindow(hWnd))
            {
                handles.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    public static string GetWindowTitle(IntPtr hWnd) =>
        hWnd == IntPtr.Zero ? string.Empty : ReadWindowTextSafe(hWnd);

    public static IntPtr FindBrowserWindow()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && IsBrowserWindow(fg))
        {
            return fg;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (IsBrowserWindow(hWnd))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static async Task<IntPtr> LaunchNewBrowserWindowAndWaitAsync(string url, CancellationToken ct)
    {
        var existingHandles = GetAllBrowserWindowHandles();
        LaunchNewBrowserWindow(url);

        IntPtr detectedNewHwnd = IntPtr.Zero;
        for (int i = 0; i < 35; i++)
        {
            await Task.Delay(100, ct);
            var currentHandles = GetAllBrowserWindowHandles();
            foreach (IntPtr h in currentHandles)
            {
                if (!existingHandles.Contains(h))
                {
                    detectedNewHwnd = h;
                    break;
                }
            }
            if (detectedNewHwnd != IntPtr.Zero)
            {
                break;
            }
        }

        if (detectedNewHwnd == IntPtr.Zero)
        {
            detectedNewHwnd = FindBrowserWindow();
        }

        if (detectedNewHwnd != IntPtr.Zero)
        {
            await ActivateWindowAsync(detectedNewHwnd, ct);
            // ページの初期DOMと検索ボックスのフォーカス完了を待機する
            await Task.Delay(950, ct);
        }

        return detectedNewHwnd;
    }

    public static IntPtr FindCustomAppWindow(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName)) return IntPtr.Zero;
        string target = applicationName.Trim();

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && MatchesAppWindow(fg, target))
        {
            return fg;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (MatchesAppWindow(hWnd, target))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static async Task<IntPtr> ActivateWindowAsync(IntPtr hWnd, CancellationToken ct)
    {
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }
            if (GetForegroundWindow() != hWnd)
            {
                SetForegroundWindow(hWnd);
                await Task.Delay(120, ct);
            }
        }
        catch
        {
            // OS権限により前面化できない場合も後続の入力送出を継続
        }
        return hWnd;
    }

    public static bool TryResolveCoordinates(SemanticLocator locator, out int x, out int y)
    {
        if (locator.CoordinateX.HasValue && locator.CoordinateY.HasValue)
        {
            x = locator.CoordinateX.Value;
            y = locator.CoordinateY.Value;
            return true;
        }

        string?[] candidates = { locator.Label, locator.AccessibleName, locator.StableId, locator.CssSelector };
        foreach (string? text in candidates)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var match = _coordInTextRegex.Match(text);
            if (match.Success)
            {
                string gx = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[3].Value;
                string gy = !string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value : match.Groups[4].Value;
                if (int.TryParse(gx, out x) && int.TryParse(gy, out y))
                {
                    return true;
                }
            }
        }

        x = 0;
        y = 0;
        return false;
    }

    public static bool TryGetWindowCenter(IntPtr hWnd, out int x, out int y)
    {
        if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            x = rect.Left + (rect.Right - rect.Left) / 2;
            y = rect.Top + (rect.Bottom - rect.Top) / 2;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    public static IntPtr LaunchNewBrowserWindow(string url)
    {
        string[] browserCandidates = { "chrome.exe", "msedge.exe" };
        foreach (string exe in browserCandidates)
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--new-window \"{url}\"",
                    UseShellExecute = true
                });
                if (proc is not null)
                {
                    return proc.MainWindowHandle;
                }
            }
            catch
            {
                // 次の候補ブラウザへフォールバック
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
        return IntPtr.Zero;
    }

    private static bool IsBrowserWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        string procName = GetProcessNameSafe(pid);
        string title = ReadWindowTextSafe(hWnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        return procName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Brave", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAppWindow(IntPtr hWnd, string targetAppName)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        string procName = GetProcessNameSafe(pid);
        string title = ReadWindowTextSafe(hWnd);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(procName)) return false;

        return title.Contains(targetAppName, StringComparison.OrdinalIgnoreCase) ||
               procName.Contains(targetAppName, StringComparison.OrdinalIgnoreCase);
    }
}
