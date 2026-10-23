// Module: @role: 実行対象ウィンドウの列挙・サムネイルキャプチャ・ウィンドウ拘束制御・UIAutomation/低レベル入力フック補助サービス
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiMacroSystem.Infrastructure;

public sealed class OpenWindowInfo
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public required uint ProcessId { get; init; }
    public required string AppCategory { get; init; }
    public BitmapSource? Thumbnail { get; init; }
}

public sealed class AppTargetPromptRequest
{
    public required string TargetKey { get; init; }
    public required string AppCategory { get; init; }
    public required string ApplicationName { get; init; }
    public required string DisplayTitle { get; init; }
    public required int ActionCount { get; init; }
    public string? InitialUrlOrPath { get; init; }
    public required IReadOnlyList<OpenWindowInfo> AvailableWindows { get; init; }
}

public sealed class WindowTargetSelection
{
    public required string TargetKey { get; init; }
    public required string AppCategory { get; init; }
    public required string ApplicationName { get; init; }
    public required bool CreateNewWindow { get; init; }
    public bool UseBuiltInBrowser { get; init; }
    public IntPtr TargetWindowHandle { get; set; }
    public string SelectedWindowTitle { get; init; } = string.Empty;
}

public static class WindowThumbnailCaptureService
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

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int SRCCOPY = 0x00CC0020;

    public static IReadOnlyList<OpenWindowInfo> GetOpenWindowsForTarget(string appCategory, string? applicationName, bool includeThumbnails = true)
    {
        var results = new List<OpenWindowInfo>();
        uint currentPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == currentPid) return true;

            string title = ReadWindowTextSafe(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("設定", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Microsoft Text Input Application", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string procName = GetProcessNameSafe(pid);
            string detectedCategory = ClassifyWindowCategory(procName, title);

            bool matches = appCategory switch
            {
                "Chrome" => detectedCategory == "Chrome",
                "Excel" => detectedCategory == "Excel",
                _ => MatchesCustomApp(detectedCategory, procName, title, applicationName)
            };

            if (!matches) return true;

            BitmapSource? thumb = includeThumbnails
                ? CaptureWindowThumbnailSafe(hWnd, title, procName, appCategory)
                : null;

            results.Add(new OpenWindowInfo
            {
                Handle = hWnd,
                Title = title,
                ProcessName = procName,
                ProcessId = pid,
                AppCategory = detectedCategory,
                Thumbnail = thumb
            });
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static BitmapSource CreateBadgeThumbnail(string iconText, string labelText, Color accentColor)
    {
        int width = 180;
        int height = 104;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(248, 250, 252),
                Color.FromRgb(226, 232, 240),
                new Point(0, 0),
                new Point(1, 1));
            dc.DrawRoundedRectangle(bgBrush, new Pen(new SolidColorBrush(accentColor), 2), new Rect(1, 1, width - 2, height - 2), 8, 8);

            var pillBrush = new SolidColorBrush(accentColor);
            dc.DrawRoundedRectangle(pillBrush, null, new Rect(14, 14, width - 28, 34), 6, 6);

            var typeface = new Typeface(new FontFamily("Yu Gothic UI, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ftIcon = new FormattedText(
                iconText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                14,
                Brushes.White,
                1.0);
            dc.DrawText(ftIcon, new Point((width - ftIcon.Width) / 2, 21));

            var ftSub = new FormattedText(
                labelText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                11.5,
                new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                1.0);
            dc.DrawText(ftSub, new Point(Math.Max(8, (width - ftSub.Width) / 2), 64));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource CaptureWindowThumbnailSafe(IntPtr hWnd, string title, string procName, string appCategory)
    {
        try
        {
            if (!IsIconic(hWnd) && GetWindowRect(hWnd, out RECT rect))
            {
                int w = Math.Max(1, rect.Right - rect.Left);
                int h = Math.Max(1, rect.Bottom - rect.Top);
                if (w > 40 && h > 40)
                {
                    IntPtr hdcScreen = GetDC(IntPtr.Zero);
                    if (hdcScreen != IntPtr.Zero)
                    {
                        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, w, h);
                        if (hBitmap != IntPtr.Zero)
                        {
                            IntPtr hOld = SelectObject(hdcMem, hBitmap);
                            bool captured = PrintWindow(hWnd, hdcMem, PW_RENDERFULLCONTENT);
                            if (!captured)
                            {
                                captured = PrintWindow(hWnd, hdcMem, 0);
                            }
                            if (!captured)
                            {
                                captured = BitBlt(hdcMem, 0, 0, w, h, hdcScreen, rect.Left, rect.Top, SRCCOPY);
                            }
                            SelectObject(hdcMem, hOld);

                            if (captured)
                            {
                                var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                                    hBitmap,
                                    IntPtr.Zero,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromWidthAndHeight(180, 104));
                                bmp.Freeze();
                                DeleteObject(hBitmap);
                                DeleteDC(hdcMem);
                                ReleaseDC(IntPtr.Zero, hdcScreen);
                                return bmp;
                            }
                            DeleteObject(hBitmap);
                        }
                        DeleteDC(hdcMem);
                        ReleaseDC(IntPtr.Zero, hdcScreen);
                    }
                }
            }
        }
        catch
        {
            // キャプチャ不可の保護ウィンドウはプレースホルダーサムネイルにフォールバック
        }

        string shortTitle = title.Length > 18 ? title[..18] + "..." : title;
        Color color = appCategory switch
        {
            "Chrome" => Color.FromRgb(37, 99, 235),
            "Excel" => Color.FromRgb(22, 163, 74),
            _ => Color.FromRgb(124, 58, 237)
        };
        return CreateBadgeThumbnail(procName, shortTitle, color);
    }

    private static bool MatchesCustomApp(string detectedCategory, string procName, string title, string? applicationName)
    {
        if (detectedCategory is "Chrome" or "Excel") return false;
        if (string.IsNullOrWhiteSpace(applicationName) ||
            applicationName.Equals("基幹業務システム", StringComparison.OrdinalIgnoreCase) ||
            applicationName.Equals("独自アプリ", StringComparison.OrdinalIgnoreCase) ||
            applicationName.Equals("デスクトップアプリ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return title.Contains(applicationName, StringComparison.OrdinalIgnoreCase) ||
               procName.Contains(applicationName, StringComparison.OrdinalIgnoreCase) ||
               detectedCategory == "CustomApp";
    }

    public static string ClassifyWindowCategory(string processName, string windowTitle)
    {
        if (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Microsoft​ Edge", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Brave", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (processName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("- Excel", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return "Excel";
        }

        return "CustomApp";
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

public sealed class LowLevelInputHook : IDisposable
{
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;

    private readonly ConcurrentQueue<int> _keyQueue = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _procDelegate;
    private volatile bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        while (_keyQueue.TryDequeue(out _)) { }

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "AiMacroLowLevelKeyboardHook"
        };
        _hookThread.Start();
    }

    public bool TryDequeueKey(out int vkCode) => _keyQueue.TryDequeue(out vkCode);

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _hookThread?.Join(150);
        _hookThread = null;
        _hookThreadId = 0;
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _procDelegate = HookCallback;
        IntPtr hMod = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _procDelegate, hMod, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            while (_running && GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            _keyQueue.Enqueue((int)kb.vkCode);
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
