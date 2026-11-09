// Module: @role: Win32 APIを用いたウィンドウ制御・マウス/キーボード入力送出・OS自動化低レベル操作
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

internal static class WindowsOsAutomation
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const int SW_RESTORE = 9;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

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

    public static async Task MoveAndLeftClickAsync(int x, int y, CancellationToken ct)
    {
        SetCursorPos(x, y);
        await Task.Delay(60, ct);
        mouse_event(MOUSEEVENTF_LEFTDOWN, x, y, 0, UIntPtr.Zero);
        await Task.Delay(35, ct);
        mouse_event(MOUSEEVENTF_LEFTUP, x, y, 0, UIntPtr.Zero);
        await Task.Delay(80, ct);
    }

    public static async Task SendVirtualKeyAsync(ushort vk, CancellationToken ct)
    {
        var inputs = new INPUT[]
        {
            new()
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } }
            },
            new()
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        await Task.Delay(90, ct);
    }

    public static async Task SendCtrlShortcutAndNavigateUrlAsync(string url, CancellationToken ct)
    {
        // アドレスバーへフォーカス(Ctrl+L)し、日本語IME未確定状態を回避するためクリップボード貼り付け(Ctrl+V)優先でURL遷移する
        const ushort VK_CONTROL = 0x11;
        const ushort VK_L = 0x4C;
        const ushort VK_V = 0x56;
        const ushort VK_RETURN = 0x0D;

        var ctrlL = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_L } } },
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_L, dwFlags = KEYEVENTF_KEYUP } } },
            new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)ctrlL.Length, ctrlL, Marshal.SizeOf<INPUT>());
        await Task.Delay(140, ct);

        if (TrySetClipboardTextSafe(url))
        {
            var ctrlV = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V } } },
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
                new() { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } }
            };
            SendInput((uint)ctrlV.Length, ctrlV, Marshal.SizeOf<INPUT>());
            await Task.Delay(100, ct);
        }
        else
        {
            await SendUnicodeTextAsync(url, ct);
        }

        await SendVirtualKeyAsync(VK_RETURN, ct);
        await Task.Delay(550, ct);
    }

    private static bool TrySetClipboardTextSafe(string text)
    {
        bool ok = false;
        try
        {
            var staThread = new Thread(() =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    ok = true;
                }
                catch
                {
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(120);
        }
        catch
        {
        }
        return ok;
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

    public static async Task SendUnicodeTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            });
        }

        INPUT[] arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        await Task.Delay(80, ct);
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

    private static string ReadWindowTextSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string GetProcessNameSafe(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
