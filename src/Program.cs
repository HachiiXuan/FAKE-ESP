using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FakeEsp;

internal static class Program
{
    private static InputState? _input;
    private static InputThread? _inputThread;
    private static HidMouse? _hidMouse;
    private static HttpServer? _server;
    private static World? _world;
    private static Config? _config;
    private static MainWindow? _mainWindow;
    private static OverlayWindow? _overlay;
    private static Thread? _pollThread;
    private static Thread? _simThread;
    private static Mutex? _singleInstance;
    private static bool _singleInstanceGivenUp;

    private const string SingleInstanceName = @"Local\FakeEspSingleInstance";
    private static volatile bool _running = true;

    // 光标轮询用的上一次位置
    private static int _lastCursorX;
    private static int _lastCursorY;
    private static bool _hasCursor;

    [STAThread]
    private static int Main(string[] args)
    {
        var config = Config.Parse(args);

        if (config.Console) Native.TryAttachConsole();
        Native.EnablePerMonitorDpiAwareness();

        var logPath = config.LogFile ?? Path.Combine(AppContext.BaseDirectory, "fake-esp.log");
        Log.AttachFile(logPath);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal("AppDomain 未处理异常", e.ExceptionObject as Exception);

        // ---------------------------------------------------------------- 提权
        // 键盘热键（WASD / Ins / Home）在"游戏以管理员权限在前台"时需要一个提权进程才能收到，
        // 这是 Windows 的 UIPI 限制，绕不过去。
        //
        // 这里**默认不自动提权**，原因有两个：
        //   1) Process.Start(runas) 会阻塞等待 UAC 答复；万一弹窗不可见就是死等，整个启动卡住。
        //   2) 只用 OBS 覆盖层（只需要鼠标）的人没必要每次都被 UAC 拦一下。
        // 所以默认在界面里提示 + 给一个「以管理员身份重启」按钮（用户主动点，阻塞也无所谓）；
        // 想要一启动就提权的话，加 --admin 参数。
        if (config.Admin && !config.ElevatedAttempt && !Native.IsElevated())
        {
            Log.Info("--admin：尝试提权重启（游戏前台时的键盘操作需要它）");
            if (Native.RelaunchElevated(BuildRelaunchArgs(args)))
            {
                Log.Info("已请求提权重启，本次进程退出");
                _singleInstance?.Dispose();
                return 0;
            }

            Log.Warn("提权未成功，以普通权限继续运行");
        }
        // 单实例：重复双击时不要起第二个进程去抢同一个端口。
        // 第二次启动不弹模态框，而是通过一个命名事件让**已有实例把主窗口叫到前面**，然后自己安静退出。
        const string showEventName = @"Local\FakeEspShowWindow";

        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // ---- 快路：这是"用户又双击了一次" ----
            // 用 --elevated-attempt 标记排除掉提权交接的情况：那种情况下的"已有实例"
            // 其实正在退出，去通知它等于把新实例也一起弄死（"点了运行没反应，也没新窗口"就是这个）。
            if (!config.ElevatedAttempt)
            {
                Log.Info("已有实例在运行：通知它显示主窗口，本次启动退出");
                try
                {
                    if (EventWaitHandle.TryOpenExisting(showEventName, out var signal))
                    {
                        signal.Set();
                        signal.Dispose();
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("通知已有实例失败，退回弹框提示: " + ex.Message);
                }
            }

            // ---- 慢路：锁还在，但可能是"旧实例正在为提权重启而退出" ----
            // 这时稍等一下再抢，抢到了就顶替它继续跑。
            Log.Info("单实例锁被占着，等待上一个实例退出后接管……");
            for (var i = 0; i < 24 && !isFirstInstance; i++)
            {
                Thread.Sleep(150);
                try
                {
                    if (_singleInstance.WaitOne(0)) isFirstInstance = true;
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持有者没释放就退出了，所有权直接归我们
                    isFirstInstance = true;
                }
            }
        }

        if (!isFirstInstance)
        {
            Log.Warn("等待超时，仍有一个 FAKE ESP 在运行，本次启动退出");
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    "FAKE ESP 已经在运行了。\n\n如果看不到它的窗口，请到任务管理器里结束 FakeEsp.exe 再重新打开。",
                    "FAKE ESP",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch
            {
                // 没有交互桌面就算了
            }

            return 0;
        }

        if (config.ElevatedAttempt)
            Log.Info("已接管单实例锁（提权后的新实例开始工作）");

        try
        {
            return Run(config);
        }
        catch (Exception ex)
        {
            Log.Fatal("启动失败", ex);
            return 1;
        }
    }

    /// <summary>把原始命令行参数照抄一遍，并加一个"已经尝试过提权"的标记防止死循环。</summary>
    private static string BuildRelaunchArgs(string[] args)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in args)
        {
            if (a.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            if (a.Contains(' ')) sb.Append('"').Append(a).Append('"');
            else sb.Append(a);
        }

        if (sb.Length > 0) sb.Append(' ');
        sb.Append("--elevated-attempt");
        return sb.ToString();
    }

    private static int Run(Config config)
    {
        _config = config;
        // 版本号从程序集读，别再手写字符串（手写过一次就和 csproj 对不上了）
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info("FAKE ESP v" + (version?.ToString(3) ?? "?") + " 启动 —— 纯娱乐特效，与任何游戏无关");
        Log.Info("日志: " + (config.LogFile ?? Path.Combine(AppContext.BaseDirectory, "fake-esp.log")));

        var webRoot = config.ResolveWebRoot();
        Log.Info("web 根目录: " + webRoot);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Fatal("UI 线程未处理异常", e.Exception);
            e.Handled = true;
        };

        _input = new InputState();

        // ---------------------------------------------------------------- 鼠标输入
        // 四条链路并行被动采集，只有一条会真正驱动相机（InputState 每秒自动选源）：
        //   Raw Input（OS 正规路径） > HID 直读（不依赖事件投递） > 低级钩子 > 光标轮询（兜底）
        // 前两条都在 InputThread 里或独立线程上，互不干扰。
        if (!config.NoInput)
        {
            Safe.Run(() =>
            {
                _inputThread = new InputThread(_input);
                _inputThread.Start();
            }, "启动输入线程");

            // HID 直读：绕开"操作系统不把输入投递给本进程"的情况，见 HidMouse 里的说明
            Safe.Run(() =>
            {
                _hidMouse = new HidMouse(_input);
                if (_hidMouse.TryOpen()) _hidMouse.Start();
                else Log.Warn("HID 直读没打开（没有可读的鼠标设备，或权限不足）");
            }, "打开 HID 直读");
        }
        else
        {
            Log.Warn("--no-input：输入采集已禁用，只跑随机漂移");
        }

        // ---------------------------------------------------------------- 世界 + 服务
        _world = new World(_input);

        _server = new HttpServer(config, webRoot)
        {
            StateProvider = () => _world.BuildStateJson(),
            SetHandler = query => _world.ApplySettings(query)
        };

        if (!_server.TryStart())
        {
            Log.Fatal("HTTP 服务无法启动（端口 " + config.Port + " 被占用？），退出", null);
            return 2;
        }

        // ---------------------------------------------------------------- 界面外壳
        var previewUrl = _server.Url + "?mode=standalone&bg=dark";
        _mainWindow = new MainWindow(_world, _input, _server, previewUrl)
        {
            IsElevated = Native.IsElevated()
        };
        _mainWindow.ExitClicked += ExitApp;
        _mainWindow.RestartElevatedClicked += RestartElevated;
        _mainWindow.OverlayToggleClicked += ToggleOverlay;
        _mainWindow.OverlayStateProvider = () => _overlay != null && _overlay.IsVisible;

        // 两种都要记下来，方便排查"界面显示绿灯但键盘无效"这类问题
        Log.Info("权限: 令牌已提权 = " + _mainWindow.IsElevated);
        if (!_mainWindow.IsElevated)
            Log.Warn("当前不是管理员权限：游戏前台时 WASD / Ins / Home 可能无效（界面里会有提示和重启按钮）");

        if (config.Overlay) ShowOverlay();

        if (!config.NoUi)
        {
            _mainWindow.Show();
            StartShowSignalListener();
        }
        else
        {
            Log.Info("--no-ui：不显示主窗口，只在后台跑服务");
        }

        // ---------------------------------------------------------------- 后台线程
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "fake-esp-poll", Priority = ThreadPriority.AboveNormal };
        _pollThread.Start();

        _simThread = new Thread(SimLoop) { IsBackground = true, Name = "fake-esp-sim", Priority = ThreadPriority.AboveNormal };
        _simThread.Start();

        Log.Info("OBS 浏览器源地址: " + _server.Url + "?mode=obs");
        Log.Info("调参面板:         " + _server.Url + "tune");

        if (config.RunSeconds > 0)
        {
            Log.Info("自检模式：" + config.RunSeconds + " 秒后自动退出");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(config.RunSeconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Log.Info("自检结束：" + _world.BuildStateJson());
                app.Shutdown();
            };
            timer.Start();
        }

        app.Exit += (_, _) => Shutdown();
        app.Run();
        return 0;
    }

    // ================================================================ 单实例联动

    private static EventWaitHandle? _showSignal;

    /// <summary>
    /// 监听"第二次双击"的信号：收到就把主窗口叫到前面。
    /// 这样重复启动 exe 的体验是"窗口弹出来"，而不是"弹一个要手动关掉的框"。
    /// </summary>
    private static void StartShowSignalListener()
    {
        Safe.Run(() =>
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FakeEspShowWindow");

            var thread = new Thread(() =>
            {
                while (_running)
                {
                    try
                    {
                        if (!_showSignal.WaitOne(500)) continue;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                    }
                    catch (Exception ex)
                    {
                        if (_running) Log.Error("监听显示信号异常", ex);
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "fake-esp-show-signal"
            };
            thread.Start();
        }, "启动单实例联动");
    }

    // ================================================================ 界面

    private static void ShowMainWindow()
    {
        Safe.Run(() =>
        {
            if (_mainWindow == null) return;
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }, "显示主窗口");
    }

    private static void ShowOverlay()
    {
        if (_world == null) return;
        Safe.Run(() =>
        {
            if (_overlay == null)
            {
                _overlay = new OverlayWindow(_world, _config?.OverlayOpaque ?? false, _config?.OverlayKey ?? false);
                _overlay.Closed += (_, _) => _overlay = null;
            }

            _overlay.Show();
            Log.Info("覆盖窗已显示（点击穿透 / 置顶 / 无边框）");
        }, "显示覆盖窗");
    }

    private static void HideOverlay()
    {
        Safe.Run(() =>
        {
            _overlay?.Hide();
            _world!.OverlayFps = 0;
            Log.Info("覆盖窗已隐藏");
        }, "隐藏覆盖窗");
    }

    private static void ToggleOverlay()
    {
        if (_overlay != null && _overlay.IsVisible) HideOverlay();
        else ShowOverlay();
    }

    private static void ExitApp()
    {
        Safe.Run(() =>
        {
            if (_mainWindow != null) _mainWindow.ExitRequested = true;
            System.Windows.Application.Current?.Shutdown();
        }, "退出");
    }

    /// <summary>供界面上的「以管理员身份重启」按钮使用。</summary>
    private static void RestartElevated()
    {
        Safe.Run(() =>
        {
            Log.Info("用户请求以管理员身份重启");

            // ★ 顺序是关键：必须先把单实例互斥体**销毁**，再去启动提权副本。
            // 反过来做的话，新实例启动时会发现"已有实例在运行"，于是通知一声就自己退出了 ——
            // 用户看到的现象就是"UAC 点了运行，然后什么都没发生，也没有新窗口"。
            ReleaseSingleInstanceForHandover();

            // 把本次进程的参数原样带过去（跳过第 0 个，那是 exe 路径）
            var argv = Environment.GetCommandLineArgs();
            var rest = new string[Math.Max(0, argv.Length - 1)];
            if (rest.Length > 0) Array.Copy(argv, 1, rest, 0, rest.Length);

            if (!Native.RelaunchElevated(BuildRelaunchArgs(rest)))
            {
                // 用户在 UAC 里点了取消：把锁拿回来，继续以普通权限运行
                ReacquireSingleInstance();
                Log.Warn("提权重启被取消，继续以普通权限运行");
                return;
            }

            ExitApp();
        }, "以管理员身份重启");
    }

    private static void ReleaseSingleInstanceForHandover()
    {
        _singleInstanceGivenUp = true;
        try { _singleInstance?.ReleaseMutex(); } catch { /* 没持有就算了 */ }
        try { _singleInstance?.Dispose(); } catch { /* 忽略 */ }
        _singleInstance = null;
    }

    private static void ReacquireSingleInstance()
    {
        _singleInstance = new Mutex(true, SingleInstanceName, out _);
        _singleInstanceGivenUp = false;
    }

    // ================================================================ 后台循环

    /// <summary>250Hz：按键轮询 + 光标轮询；每满一秒结算一次输入来源。</summary>
    private static void PollLoop()
    {
        const int intervalMs = 4;
        var lastSecond = Environment.TickCount64;

        while (_running)
        {
            var started = Environment.TickCount64;
            try
            {
                _input?.PollKeysOnce();
                PollCursor();

                if (started - lastSecond >= 1000)
                {
                    lastSecond = started;
                    _input?.TickSecond();
                }
            }
            catch (Exception ex)
            {
                Log.Error("输入轮询异常", ex);
                Thread.Sleep(100);
            }

            var sleep = intervalMs - (int)(Environment.TickCount64 - started);
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    /// <summary>光标位置轮询（第三路兜底）。</summary>
    private static void PollCursor()
    {
        if (_input == null || !_input.CursorPollEnabled) return;
        if (!Native.TryGetCursorPos(out var x, out var y)) return;

        if (_hasCursor)
        {
            var dx = x - _lastCursorX;
            var dy = y - _lastCursorY;
            if (dx != 0 || dy != 0) _input.AddMouse(dx, dy, MouseSource.CursorPoll);
            else _input.ReportEvent(MouseSource.CursorPoll);
        }

        _lastCursorX = x;
        _lastCursorY = y;
        _hasCursor = true;
    }

    private static void SimLoop()
    {
        const int intervalMs = 8;
        while (_running)
        {
            var started = Environment.TickCount64;
            try
            {
                _world?.Tick();
            }
            catch (Exception ex)
            {
                Log.Error("世界模拟异常", ex);
                Thread.Sleep(100);
            }

            var sleep = intervalMs - (int)(Environment.TickCount64 - started);
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    private static void Shutdown()
    {
        if (!_running) return;
        _running = false;

        Safe.Run(() => _server?.Stop(), "停止 HTTP 服务");
        Safe.Run(() => _hidMouse?.Dispose(), "关闭 HID 直读");
        Safe.Run(() => _inputThread?.Dispose(), "停止输入线程");
        Safe.Run(() => _showSignal?.Dispose(), "释放单实例信号");
        if (!_singleInstanceGivenUp)
            Safe.Run(() => _singleInstance?.ReleaseMutex(), "释放单实例锁");
        Safe.Run(() => _singleInstance?.Dispose(), "释放单实例");
        Log.Info("已退出");
    }
}
