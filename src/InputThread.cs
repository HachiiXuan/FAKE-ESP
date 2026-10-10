using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FakeEsp;

/// <summary>
/// 鼠标输入采集线程。
///
/// 为什么要有这么个类（踩过的坑）：
///   之前在 WPF 的 UI 线程上用 HwndSource 承载 WM_INPUT，并在同一个线程上装 WH_MOUSE_LL。
///   结果两条链路**一条数据都没有** —— 实测 200 次鼠标位移注入，WM_INPUT 计数为 0，钩子回调
///   一次都没触发，而同一时刻 UI 线程的 DispatcherTimer 是准点触发的（消息泵没坏）。
///   既然两条完全不同的机制同时哑掉，问题就出在"承载方式"上，而不是某一条链路。
///
/// 所以这里改成输入捕获的标准做法：**自己开一条 STA 线程，自己注册窗口类、自己
/// CreateWindowEx 建窗口、自己跑 GetMessage/DispatchMessage 循环**，两条链路都装在这条线程上。
/// 完全不依赖 WPF 的 Dispatcher 和 HwndSource。
///
/// 诊断全部暴露到 /state：
///   winMessages   —— 我们的窗口总共收到多少条消息（0 就说明窗口根本没被派发）
///   wmInputMessages —— 其中 WM_INPUT 有多少条（0 就说明 Raw Input 没送到）
///   loopHeartbeat —— 消息循环每秒一次的 WM_TIMER 心跳（证明循环在转）
///   hookCalls     —— 钩子回调被调用的次数（0 就说明钩子没生效）
/// </summary>
internal sealed class InputThread : IDisposable
{
    private const string WindowClassName = "FakeEspInputWindow";
    private const int HWND_MESSAGE = -3;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_DESTROY = 0x0002;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint TIMER_ID = 1;

    private readonly InputState _input;
    private readonly Native.LowLevelMouseProc _hookProc;      // 必须留着，否则委托被 GC 回收
    private readonly Native.LowLevelKeyboardProc _kbProc;     // 同上
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _started;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hook = IntPtr.Zero;
    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _instance = IntPtr.Zero;
    private Native.WndProcDelegate? _wndProc;                 // 同理，必须留着

    // 诊断计数（跨线程，用 Interlocked）
    private long _winMessageCount;      // 本地计数，只用于"头几条消息打日志"
    private int _lastHookX;
    private int _lastHookY;
    private bool _hasHookLast;

    public bool WindowCreated => _hwnd != IntPtr.Zero;
    public bool RawRegistered { get; private set; }
    public bool HookInstalled => _hook != IntPtr.Zero;

    public InputThread(InputState input)
    {
        _input = input;
        _hookProc = HookCallback;
        _kbProc = KeyboardCallback;
    }

    public bool Start(int timeoutMs = 5000)
    {
        if (_started) return true;

        _running = true;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "fake-esp-input"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        var ok = _ready.Wait(timeoutMs);
        if (!ok) Log.Error("输入线程在 " + timeoutMs + "ms 内没有就绪");
        _started = ok;
        return ok;
    }

    // ================================================================ 线程主体

    private void ThreadMain()
    {
        try
        {
            _instance = Native.GetModuleHandleW(null);

            if (!CreateWindowOnThisThread())
            {
                _ready.Set();
                return;
            }

            RegisterRawInput();
            InstallHook();

            // 心跳定时器：每秒一条 WM_TIMER，证明消息循环真的在转
            var timer = Native.SetTimerSafe(_hwnd, (IntPtr)TIMER_ID, 1000);
            Log.Info(timer != IntPtr.Zero
                ? "心跳定时器已设置 (id=" + timer + ")"
                : "心跳定时器设置失败，Win32 错误码 " + Marshal.GetLastWin32Error());

            Log.Info("输入线程就绪 (窗口 0x" + _hwnd.ToString("X") + ", 线程 " + Environment.CurrentManagedThreadId + ")");
            _ready.Set();

            RunMessageLoop();
        }
        catch (Exception ex)
        {
            Log.Fatal("输入线程异常", ex);
            _ready.Set();
        }
        finally
        {
            Cleanup();
        }
    }

    private bool CreateWindowOnThisThread()
    {
        _wndProc = WndProc;

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = _instance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = IntPtr.Zero
        };

        var atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // 1410 = 类已注册（同进程重复注册），可以继续用
            if (err != 1410)
            {
                Log.Error("RegisterClassEx 失败，Win32 错误码 " + err);
                return false;
            }
        }

        // 一律用**屏幕外的真实顶层窗口**，不要用 message-only 窗口。
        // 实测教训：RIDEV_INPUTSINK 配 message-only 窗口时，RegisterRawInputDevices
        // 会返回成功，但一条 WM_INPUT 都收不到（窗口收不到消息级别的投递）。
        // 真实顶层窗口 + 屏幕外坐标 + 不抢焦点，是对 Raw Input 最可靠的做法。
        _hwnd = Native.CreateWindowEx(
            (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            WindowClassName, "FakeEspInput",
            Native.WS_POPUP,
            -32000, -32000, 8, 8,
            IntPtr.Zero, _instance);

        if (_hwnd == IntPtr.Zero)
        {
            Log.Error("输入窗口创建失败，Win32 错误码 " + Marshal.GetLastWin32Error());
            return false;
        }

        Log.Info("已创建屏幕外输入窗口（真实顶层窗口，Raw Input 用）");

        // 必须真的"显示"出来（屏幕外，用户看不到），否则某些情况下 Raw Input 不投递
        Native.ShowWindowNoActivate(_hwnd);
        return true;
    }

    private void RegisterRawInput()
    {
        RawRegistered = Native.RegisterRawMouseInput(_hwnd);
        _input.RawMouseRegistered = RawRegistered;
        Log.Info(RawRegistered
            ? "Raw Input 已注册到输入线程窗口 (RIDEV_INPUTSINK)"
            : "Raw Input 注册失败");
    }

    private void InstallHook()
    {
        _hook = Native.InstallMouseHook(_hookProc);
        _input.HookInstalled = _hook != IntPtr.Zero;
        Log.Info(_hook != IntPtr.Zero
            ? "低级鼠标钩子已装在输入线程上（只读，绝不吞事件）"
            : "低级鼠标钩子安装失败");

        // 键盘钩子：解决"游戏以管理员权限在前台时 GetAsyncKeyState 一律返回 0"的问题
        _kbHook = Native.InstallKeyboardHook(_kbProc);
        Log.Info(_kbHook != IntPtr.Zero
            ? "低级键盘钩子已装在输入线程上（只读，绝不吞事件）"
            : "低级键盘钩子安装失败，键盘只能退回轮询（游戏前台时可能读不到）");
    }

    private void RunMessageLoop()
    {
        while (_running)
        {
            var result = Native.GetMessage(out var msg);
            if (result == 0) break;        // WM_QUIT
            if (result == -1)              // 错误
            {
                Log.Error("GetMessage 返回 -1，错误码 " + Marshal.GetLastWin32Error());
                break;
            }

            Native.TranslateMessageSafe(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    // ================================================================ 窗口过程

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var seen = Interlocked.Increment(ref _winMessageCount) - 1;
        _input.ReportWindowMessage();
        // 头几条消息打进日志：这是"窗口过程到底有没有被调用"的直接证据
        if (seen < 6) Log.Info("输入窗口消息 [" + seen + "] msg=0x" + msg.ToString("X4"));

        if (msg == Native.WM_INPUT)
        {
            _input.ReportWmInput();
            _input.ReportEvent(MouseSource.RawInput);

            if (Native.TryReadRawMouse(lParam, out var dx, out var dy, out var buttonFlags, out var buttonData)
                && (dx != 0 || dy != 0))
            {
                _input.AddMouse(dx, dy, MouseSource.RawInput);
            }

            // RI_MOUSE_WHEEL = 0x0400；滚轮增量放在 usButtonData 里，是有符号 short，单位 120
            const int RI_MOUSE_WHEEL = 0x0400;
            if ((buttonFlags & RI_MOUSE_WHEEL) != 0)
            {
                _input.AddWheel(unchecked((short)buttonData), MouseSource.RawInput);
            }
        }
        else if (msg == WM_TIMER)
        {
            _input.ReportHeartbeat();
        }
        else if (msg == WM_DESTROY)
        {
            Native.PostQuit(0);
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ================================================================ 钩子回调

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _input.ReportHookCall();

        // nCode < 0 时必须原样转交，不能做任何处理
        if (nCode >= 0 && wParam == (IntPtr)Native.WM_MOUSEMOVE)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var x = info.pt.X;
                var y = info.pt.Y;

                if (_hasHookLast)
                {
                    var dx = x - _lastHookX;
                    var dy = y - _lastHookY;
                    if (dx != 0 || dy != 0) _input.AddMouse(dx, dy, MouseSource.Hook);
                    else _input.ReportEvent(MouseSource.Hook);
                }

                _lastHookX = x;
                _lastHookY = y;
                _hasHookLast = true;
            }
            catch (Exception ex)
            {
                Log.Error("鼠标钩子回调异常", ex);
            }
        }

        // 永远原样转交，绝不吞事件，否则游戏就收不到鼠标了
        return Native.NextHook(_hook, nCode, wParam, lParam);
    }

    // ================================================================ 键盘钩子回调

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 时必须原样转交
        if (nCode >= 0)
        {
            try
            {
                _input.ReportKeyHookEvent();

                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                var vk = Native.NormalizeVirtualKey((int)info.vkCode);

                if (wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN)
                    _input.SetKeyFromHook(vk, true);
                else if (wParam == (IntPtr)Native.WM_KEYUP || wParam == (IntPtr)Native.WM_SYSKEYUP)
                    _input.SetKeyFromHook(vk, false);
            }
            catch (Exception ex)
            {
                Log.Error("键盘钩子回调异常", ex);
            }
        }

        // 永远原样转交，绝不吞事件，否则你就打不了字了
        return Native.NextHook(_kbHook, nCode, wParam, lParam);
    }

    // ================================================================ 收尾

    private void Cleanup()
    {
        try
        {
            if (_hook != IntPtr.Zero)
            {
                Native.RemoveMouseHook(_hook);
                _hook = IntPtr.Zero;
                _input.HookInstalled = false;
            }

            if (_kbHook != IntPtr.Zero)
            {
                Native.RemoveMouseHook(_kbHook);   // 同一个 UnhookWindowsHookEx
                _kbHook = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                Native.KillTimerSafe(_hwnd, (IntPtr)TIMER_ID);
                Native.DestroyWindowSafe(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error("输入线程收尾异常", ex);
        }
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;

        // 往输入线程的队列里塞一条 WM_QUIT，让 GetMessage 返回 0 从而退出循环
        if (_hwnd != IntPtr.Zero)
        {
            Safe.Run(() => Native.PostMessageSafe(_hwnd, Native.WM_QUIT), "投递 WM_QUIT");
        }

        try
        {
            _thread?.Join(1500);
        }
        catch
        {
            // 退出时忽略
        }

        _ready.Dispose();
    }
}
