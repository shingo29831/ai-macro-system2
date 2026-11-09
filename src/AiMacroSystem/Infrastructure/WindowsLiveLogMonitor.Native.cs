// Module: @role: Windowsデスクトップ監視用のWin32 P/Invoke API定義および低レベルOS情報取得ヘルパー
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AiMacroSystem.Infrastructure;

public sealed partial class WindowsLiveLogMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hwndCtl);

    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RETURN = 0x0D;
    private const int VK_TAB = 0x09;
    private const int VK_BACK = 0x08;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_SPACE = 0x20;
    private const int VK_OEM_1 = 0xBA;
    private const int VK_OEM_PLUS = 0xBB;
    private const int VK_OEM_COMMA = 0xBC;
    private const int VK_OEM_MINUS = 0xBD;
    private const int VK_OEM_PERIOD = 0xBE;
    private const int VK_OEM_2 = 0xBF;
    private const int VK_OEM_3 = 0xC0;

    private static (string Category, string AppDisplayName) ClassifyApplication(string processName, string windowTitle)
    {
        if (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Microsoft​ Edge", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase))
        {
            return ("Chrome", "Chrome");
        }

        if (processName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("- Excel", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return ("Excel", "Excel");
        }

        string appName = !string.IsNullOrWhiteSpace(windowTitle)
            ? windowTitle.Split(" - ")[0].Trim()
            : (!string.IsNullOrWhiteSpace(processName) ? processName : "独自アプリ");
        return ("CustomApp", appName);
    }

    private static string InferBrowserUrlFromTitle(string windowTitle) =>
        BrowserUiAutomationHelper.InferBrowserUrlOrSearchEngine(windowTitle, out _);

    private static string InferCellFromCoordinates(int x, int y)
    {
        int col = Math.Clamp((x / 120) % 6, 0, 5);
        int row = Math.Clamp((y / 40) % 20 + 1, 1, 20);
        char colChar = (char)('A' + col);
        return $"{colChar}{row}";
    }

    private static string ReadWindowTextSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string ReadClassNameSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(128);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : "Control";
    }

    private static string ReadClipboardTextSafe()
    {
        string result = string.Empty;
        try
        {
            var staThread = new Thread(() =>
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        result = System.Windows.Clipboard.GetText() ?? string.Empty;
                    }
                }
                catch
                {
                    // クリップボード競合時は無視
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(120);
        }
        catch
        {
            // バックグラウンドスレッドからの取得失敗時は空文字
        }
        return result;
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
