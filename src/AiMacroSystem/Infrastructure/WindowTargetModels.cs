// Module: @role: 実行対象ウィンドウ選択・プロンプト要求・キャプチャ情報のデータモデル定義
using System;
using System.Collections.Generic;
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
