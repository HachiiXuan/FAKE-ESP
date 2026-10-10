using System;
using System.Runtime.InteropServices;

namespace FakeEsp;

/// <summary>
/// 全部 Win32 交互集中在这里。原则：**只读不写** ——
/// 不注入、不挂钩子写回、不碰任何别的进程，只是被动观察鼠标/键盘。
/// </summary>
internal static class Native
{
    // ---------- 控制台（WinExe 需要手动挂一个） ----------

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static bool TryAttachConsole()
    {
        var ok = AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole();
        if (!ok) return false;

        try
        {
            var stdout = Console.OpenStandardOutput();
            Console.SetOut(new System.IO.StreamWriter(stdout) { AutoFlush = true });
            var stderr = Console.OpenStandardError();
            Console.SetError(new System.IO.StreamWriter(stderr) { AutoFlush = true });
        }
        catch
        {
            // 拿不到标准输出也无所谓，还有文件日志
        }

        return true;
    }

    // ---------- DPI ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>覆盖窗必须和屏幕像素 1:1，否则 1920x1080 会对不齐。</summary>
    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                SetProcessDPIAware();
        }
        catch
        {
            // Win8.1 以下没有这个函数，忽略
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    // ---------- 键盘：纯轮询，零钩子 ----------

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>任意前台/后台状态下都能读到全局按键状态（不消耗按键）。</summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    // ---------- 鼠标：Raw Input，后台也能收 ----------

    public const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// <summary>
    /// 注意布局：C 里 ulButtons 和 {usButtonFlags,usButtonData} 是 union，
    /// 所以 usButtonFlags 的偏移是 4（usFlags 后面有 2 字节对齐填充）。手写这 2 字节填充。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort pad;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    /// <summary>
    /// 关键点：RIDEV_INPUTSINK 让**后台窗口**也能收到原始鼠标增量。
    /// 这解决了两个致命问题：
    ///   1) 游戏把光标锁在屏幕中心时，普通光标钩子收不到任何位移；
    ///   2) 光标撞到屏幕边缘被钳制后，视角就不再转动。
    /// 而且拿到的是**未经系统加速**的原始增量，和游戏自己用 raw input 读到的一致。
    /// </summary>
    public static bool RegisterRawMouseInput(IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
        var ok = RegisterRawInputDevices(devices, 1, size);
        if (!ok) Log.Error("RegisterRawInputDevices 失败，Win32 错误码 " + Marshal.GetLastWin32Error());
        return ok;
    }

    /// <summary>从 WM_INPUT 的 lParam 里取出鼠标原始位移。</summary>
    public static bool TryReadRawMouse(IntPtr lParam, out int dx, out int dy, out ushort buttonFlags, out ushort buttonData)
    {
        dx = 0;
        dy = 0;
        buttonFlags = 0;
        buttonData = 0;

        var size = (uint)Marshal.SizeOf<RAWINPUT>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var written = GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (written == unchecked((uint)-1) || written == 0) return false;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.header.dwType != 0) return false; // 0 = RIM_TYPEMOUSE

            dx = raw.mouse.lLastX;
            dy = raw.mouse.lLastY;
            buttonFlags = raw.mouse.usButtonFlags;
            buttonData = raw.mouse.usButtonData;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------- 窗口样式：点击穿透 + 不抢焦点 ----------

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    public static void MakeClickThroughNoActivate(IntPtr hwnd)
    {
        var before = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var wanted = before | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(wanted));

        // 读回来确认真的生效了 —— 点击穿透是这个方案的核心承诺，不能只靠"设了就当成功"
        var after = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var ok = (after & WS_EX_TRANSPARENT) != 0
                 && (after & WS_EX_LAYERED) != 0
                 && (after & WS_EX_NOACTIVATE) != 0;

        Log.Info("覆盖窗扩展样式: 0x" + before.ToString("X8") + " -> 0x" + after.ToString("X8")
                 + "  [LAYERED=" + Flag(after, WS_EX_LAYERED)
                 + " TRANSPARENT(点击穿透)=" + Flag(after, WS_EX_TRANSPARENT)
                 + " NOACTIVATE(不抢焦点)=" + Flag(after, WS_EX_NOACTIVATE)
                 + " TOOLWINDOW=" + Flag(after, WS_EX_TOOLWINDOW) + "]"
                 + (ok ? " ✓" : " ✗ 设置失败！"));
    }

    private static string Flag(long value, long mask) => (value & mask) != 0 ? "1" : "0";

    // ---------- 色键透明（LWA_COLORKEY） ----------

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 色键透明：把某个颜色整片抠成透明。
    ///
    /// 为什么需要它：WPF 只要 AllowsTransparency=true 就会走分层窗口路径，
    /// 硬件加速基本失效、退化成软件光栅化，1080p 下每个目标要 3ms，18 个目标直接掉到个位数帧率。
    /// 反过来让窗口保持"不透明"（WPF 走硬件加速），再在 Win32 层面用色键把背景色抠掉，
    /// 就能既透明又跑满帧。代价是只能二值透明：
    ///   · 线条必须关掉抗锯齿（正好，1px 外挂线本来就该锐利）
    ///   · 所有半透明元素必须变成全不透明或完全不可见
    /// </summary>
    public static bool EnableColorKeyTransparency(IntPtr hwnd, uint colorRef)
    {
        var ok = SetLayeredWindowAttributes(hwnd, colorRef, 0, LWA_COLORKEY);
        if (!ok) Log.Error("SetLayeredWindowAttributes 失败，Win32 错误码 " + Marshal.GetLastWin32Error());
        return ok;
    }

    // ---------- 前台窗口（用于判断"是不是在游戏里"，可选） ----------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public static string GetForegroundWindowTitle()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- 屏幕尺寸 ----------

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetricsNative(int nIndex);

    public static int ScreenWidth => GetSystemMetricsNative(SM_CXSCREEN);

    public static int ScreenHeight => GetSystemMetricsNative(SM_CYSCREEN);

    // ---------- 光标位置（钩子 / 轮询回退用） ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out var p))
        {
            x = p.X;
            y = p.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    // ---------- 低级鼠标钩子 WH_MOUSE_LL（回退方案） ----------

    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEMOVE = 0x0200;
    public const uint LLMHF_INJECTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>
    /// 全局低级鼠标钩子。注意：这里**只读**，回调必须原样转给 CallNextHookEx，
    /// 绝不能返回 1 把事件吃掉，否则游戏就收不到鼠标了。
    /// 钩子回调在安装它的线程上被调用，所以那个线程必须跑消息循环。
    /// </summary>
    public static IntPtr InstallMouseHook(LowLevelMouseProc proc)
    {
        var hook = SetWindowsHookExW(WH_MOUSE_LL, proc, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
            Log.Error("SetWindowsHookEx(WH_MOUSE_LL) 失败，Win32 错误码 " + Marshal.GetLastWin32Error());
        return hook;
    }

    public static void RemoveMouseHook(IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        if (!UnhookWindowsHookEx(hook))
            Log.Warn("UnhookWindowsHookEx 失败，Win32 错误码 " + Marshal.GetLastWin32Error());
    }

    // ---------- 低级键盘钩子 WH_KEYBOARD_LL ----------
    //
    // 为什么键盘也要用钩子：GetAsyncKeyState 有个要命的特性 ——
    // 当前台窗口属于**更高完整性级别**的进程（游戏经常以管理员运行）时，
    // 低权限进程调用它会一律返回 0。表现就是"必须让本程序窗口在前台，WASD 才有反应"。
    // 低级钩子装在用户自己的进程里，能收到所有键盘事件，不受这个限制。

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    public static IntPtr InstallKeyboardHook(LowLevelKeyboardProc proc)
    {
        var hook = SetWindowsHookExKeyboard(WH_KEYBOARD_LL, proc, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
            Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) 失败，Win32 错误码 " + Marshal.GetLastWin32Error());
        return hook;
    }

    /// <summary>钩子回调给的是左右分开的 VK，统一归一化成不加左右区分的那个。</summary>
    public static int NormalizeVirtualKey(int vk) => vk switch
    {
        VK_LSHIFT or VK_RSHIFT => 0x10,      // VK_SHIFT
        VK_LCONTROL or VK_RCONTROL => 0x11,  // VK_CONTROL
        VK_LMENU or VK_RMENU => 0x12,        // VK_MENU
        _ => vk
    };

    public static IntPtr NextHook(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam) =>
        CallNextHookEx(hook, nCode, wParam, lParam);

    // ---------- 权限 ----------

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out TOKEN_ELEVATION info, int len, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 当前进程**令牌是否已提权**。
    ///
    /// 注意：绝对不能用 WindowsPrincipal.IsInRole(Administrator) 来判断 ——
    /// 那个查的是"账户在不在管理员组"，管理员账户下的**非提权**进程同样返回 true，
    /// 于是界面会错误地显示绿灯"运行正常"，而键盘其实完全无效。
    /// 必须看令牌本身：TokenElevation.TokenIsElevated != 0 才算真的提权了。
    ///
    /// 为什么关心这个：Windows 的 UIPI 会拦住非提权进程 —— 它对"发往提权窗口"的输入
    /// 既收不到低级键盘钩子回调，GetAsyncKeyState 也一律返回 0。
    /// 表现就是"必须先让本程序窗口在前台，WASD / Ins / Home 才有反应"。
    /// 鼠标不受影响（Raw Input 走设备层），所以只有键盘需要管理员权限。
    /// </summary>
    public static bool IsElevated()
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            if (!GetTokenInformation(token, TokenElevation, out var info, Marshal.SizeOf<TOKEN_ELEVATION>(), out _)) return false;
            return info.TokenIsElevated != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // ---------- 以管理员身份重启自己 ----------

    /// <summary>
    /// 用 runas 动词重新启动自己（会弹 UAC）。用户点"取消"时返回 false。
    /// </summary>
    public static bool RelaunchElevated(string arguments)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,   // runas 需要 ShellExecute
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            // 1223 = 用户在 UAC 弹窗里点了"否"
            Log.Warn("提权启动失败（可能你在 UAC 里点了取消）: " + ex.Message);
            return false;
        }
    }

    // ---------- 自建窗口 + 自己的消息循环（输入线程用） ----------

    public const uint WM_QUIT = 0x0012;
    public const uint WS_POPUP = 0x80000000;
    // WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE 在上面"窗口样式"那一节已经定义（long 型），这里不重复定义
    private const int SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    // GetModuleHandleW 在上面钩子那一节已经声明过（public），这里不重复声明

    // 这些是 InputThread 需要的薄封装，集中放在这里，别的文件就不用各写一遍 P/Invoke
    public static ushort RegisterClassEx(ref WNDCLASSEX wc) => RegisterClassExW(ref wc);
    public static IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr instance) =>
        CreateWindowExW(exStyle, className, windowName, style, x, y, w, h, parent, IntPtr.Zero, instance, IntPtr.Zero);
    public static IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hwnd, msg, wParam, lParam);
    public static bool DestroyWindowSafe(IntPtr hwnd) => DestroyWindow(hwnd);
    public static int GetMessage(out MSG msg) => GetMessageW(out msg, IntPtr.Zero, 0, 0);
    public static bool TranslateMessageSafe(ref MSG msg) => TranslateMessage(ref msg);
    public static IntPtr DispatchMessage(ref MSG msg) => DispatchMessageW(ref msg);
    public static bool PostMessageSafe(IntPtr hwnd, uint msg) => PostMessageW(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
    public static void PostQuit(int code) => PostQuitMessage(code);
    public static IntPtr SetTimerSafe(IntPtr hwnd, IntPtr id, uint elapse) => SetTimer(hwnd, id, elapse, IntPtr.Zero);
    public static bool KillTimerSafe(IntPtr hwnd, IntPtr id) => KillTimer(hwnd, id);
    public static void ShowWindowNoActivate(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOWNOACTIVATE);

    // ================================================================ HID 直读
    // 直接打开鼠标的 HID 设备读输入报告。不依赖 WM_INPUT / 钩子 / 消息循环，
    // 所以能绕开"操作系统不把输入事件投递给本进程"的情况。
    // 注意：读鼠标 HID 不会抢走输入，报告是广播给所有句柄的，游戏照常收到。

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileRaw(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr handle, IntPtr buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    /// <summary>SetupDiGetClassDevs 的 W 版封装（HidMouse 用这个名字调用）。</summary>
    public static IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags) =>
        SetupDiGetClassDevsW(ref classGuid, enumerator, hwndParent, flags);

    public static bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailData, uint detailSize,
        out uint requiredSize, IntPtr deviceInfoData) =>
        SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, detailData, detailSize, out requiredSize, deviceInfoData);

    /// <summary>以只读方式打开设备接口路径。共享读写，不会独占设备。</summary>
    public static IntPtr CreateFileRead(string path) =>
        CreateFileRaw(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    public static bool ReadFileHid(IntPtr handle, IntPtr buffer, uint toRead, out uint read) =>
        ReadFile(handle, buffer, toRead, out read, IntPtr.Zero);

    public static void CancelIo(IntPtr handle) => CancelIoEx(handle, IntPtr.Zero);

    public static void CloseHandleSafe(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        CloseHandle(handle);
    }
}
