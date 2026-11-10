// Module: @role: Win32低レベルキーボードフックを用いた打鍵イベントの監視と非同期デキュー
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace AiMacroSystem.Infrastructure;

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
