// Module: @role: Win32 APIを用いた対象ウィンドウの列挙・分類・サムネイル描画キャプチャサービス
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiMacroSystem.Infrastructure;

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
