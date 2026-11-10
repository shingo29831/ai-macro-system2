// Module: @role: Windowsデスクトップ上のアクティブウィンドウ・打鍵・クリック監視および操作イベント抽出
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AiMacroSystem.Application;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Infrastructure;

public sealed partial class WindowsLiveLogMonitor : ILiveOperationLogCollector
{
    private Timer? _pollTimer;
    private RecordingService? _recordingService;
    private Action<RawRecordedEvent>? _onEventCaptured;
    private readonly object _sync = new();
    private readonly LowLevelInputHook _lowLevelHook = new();

    private bool _wasLeftDown;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly StringBuilder _typedBuffer = new();
    private string _lastWindowTitle = string.Empty;
    private string _lastAppCategory = string.Empty;
    private string _lastAppName = string.Empty;
    private string _lastBrowserUrl = "https://www.google.com";
    private string _lastControlLabel = "検索";
    private string _lastCopiedText = string.Empty;
    private string _lastRecordedSearchQuery = string.Empty;
    private string _lastFlushedText = string.Empty;
    private bool _hasEmittedEnterForLastInput;
    private bool _hasEmittedInitialBrowserNavigate;
    private bool _hasExplicitClickInCurrentWindow;
    private int _lastClickX = 300;
    private int _lastClickY = 200;
    private DateTimeOffset _startedAt;

    public string DefaultExcelWorkbookPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiMacroSample.xlsx");

    public void Start(RecordingService recordingService, Action<RawRecordedEvent>? onEventCaptured = null)
    {
        lock (_sync)
        {
            _recordingService = recordingService;
            _onEventCaptured = onEventCaptured;
            _typedBuffer.Clear();
            _pressedKeys.Clear();
            _wasLeftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            _lastWindowTitle = string.Empty;
            _lastRecordedSearchQuery = string.Empty;
            _lastFlushedText = string.Empty;
            _hasEmittedEnterForLastInput = false;
            _hasEmittedInitialBrowserNavigate = false;
            _hasExplicitClickInCurrentWindow = false;
            _startedAt = DateTimeOffset.UtcNow;

            _lowLevelHook.Start();
            _pollTimer?.Dispose();
            _pollTimer = new Timer(PollTick, null, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(25));
        }
    }

    public void StopAndFlush()
    {
        lock (_sync)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _lowLevelHook.Stop();
            DrainLowLevelKeysLocked(_lastAppCategory, _lastAppName);
            FlushTypedBufferLocked(pressedEnter: false);

            // ウィンドウ切替のみで個別クリック・打鍵が未記録だった場合は対象画面への遷移イベントを補完
            if (_recordingService is not null &&
                _recordingService.IsRecording &&
                _lastAppCategory == "Chrome" &&
                !_recordingService.GetRecordedEvents().Any(e =>
                    e.EventKind is "Click" or "Input" or "Write" or "Paste" or "Navigate" or "Submit"))
            {
                var navTs = _recordingService.AdvanceLogicalClock(TimeSpan.FromMilliseconds(100));
                string navUrl = InferBrowserUrlFromTitle(_lastWindowTitle);
                Emit(_recordingService.RecordEvent(
                    "Chrome", "Navigate", null, null, null, null, null,
                    navUrl, null, null, null,
                    logSource: LogSourceConstants.BrowserDom, sourceConfidence: 0.98,
                    windowOrProcessName: _lastWindowTitle, timestampOverride: navTs));
            }
        }
    }

    private void PollTick(object? state)
    {
        if (!Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_recordingService is null || !_recordingService.IsRecording)
            {
                return;
            }

            // マウス左クリックを先に判定しオーバーレイ直後の外部クリック取りこぼしを防止
            bool isLeftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (isLeftDown && !_wasLeftDown && (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds > 150)
            {
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr targetHwnd = WindowFromPoint(pt);
                    if (targetHwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(targetHwnd, out uint targetPid);
                        if (targetPid != 0 && targetPid != (uint)Environment.ProcessId)
                        {
                            IntPtr rootHwnd = GetAncestor(targetHwnd, GA_ROOT);
                            if (rootHwnd == IntPtr.Zero) rootHwnd = targetHwnd;
                            string clickWinTitle = ReadWindowTextSafe(rootHwnd);
                            string clickProcName = GetProcessNameSafe(targetPid);
                            var (clickCategory, clickAppName) = ClassifyApplication(clickProcName, clickWinTitle);

                            EnsureWindowContextRecordedLocked(clickCategory, clickAppName, clickWinTitle);
                            FlushTypedBufferLocked();
                            _hasExplicitClickInCurrentWindow = true;
                            _lastClickX = pt.X;
                            _lastClickY = pt.Y;
                            RecordMouseClickMultiLogsLocked(targetHwnd, clickCategory, clickAppName, clickWinTitle, pt);
                        }
                    }
                }
            }
            _wasLeftDown = isLeftDown;

            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero)
            {
                return;
            }

            GetWindowThreadProcessId(fgHwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                return;
            }

            string winTitle = ReadWindowTextSafe(fgHwnd);
            string procName = GetProcessNameSafe(pid);
            var (appCategory, appDisplayName) = ClassifyApplication(procName, winTitle);

            // タイトル変化によるフラッシュ前に低レベルキー入力を先に吸い上げEnterを取りこぼさない
            PollKeyboardInputLocked(appCategory, appDisplayName);
            EnsureWindowContextRecordedLocked(appCategory, appDisplayName, winTitle);
        }
        catch
        {
            // OSフック取得失敗時も記録セッションを継続
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void Emit(RawRecordedEvent ev) => _onEventCaptured?.Invoke(ev);
}
