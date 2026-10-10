using System;
using System.Threading;

namespace FakeEsp;

/// <summary>鼠标位移的来源。优先级从高到低（由 TickSecond 里的顺序决定）。</summary>
internal enum MouseSource
{
    None = 0,

    /// <summary>WM_INPUT + RIDEV_INPUTSINK：原始设备计数，未加速，光标被锁也能收到。OS 正规路径。</summary>
    RawInput = 1,

    /// <summary>
    /// 直接读鼠标的 HID 设备（CreateFile + ReadFile）。
    /// 不依赖任何"事件投递"：绕过 UIPI / 钩子限制 / 消息循环，拿到的也是真正的
    /// 原始设备增量，所以游戏把光标锁在屏幕中心时照样有数据。
    /// 注意：读鼠标 HID 不会抢走输入，报告是广播给所有句柄的，游戏照常收到。
    /// </summary>
    Hid = 2,

    /// <summary>WH_MOUSE_LL 低级钩子：几乎总能装上，但拿到的是"光标位置差"，会被系统加速，且光标被钳制时停住。</summary>
    Hook = 3,

    /// <summary>GetCursorPos 轮询：最后的兜底，同钩子的缺点且精度更差。</summary>
    CursorPoll = 4
}

/// <summary>
/// 输入状态汇总。
///
/// 历史上这里有个严重的验证漏洞：只有一条输入路径，而且相机自带一点"呼吸摆动"，
/// 于是"yaw 在变"看起来像输入通了，其实可能一个鼠标事件都没收到。
/// 现在改成：
///   · 三种来源同时被动采集，各自独立计数（收到多少事件、其中多少位移非零）
///   · 每秒结算一次，按优先级自动选一个"真的在出数据"的来源驱动相机
///   · 所有计数器都暴露到 /state 和主窗口，随时能看出输入到底通没通
/// </summary>
internal sealed class InputState
{
    private const int SourceCount = 5;
    private const int MeaningfulPerSecond = 5;   // 一秒至少这么多事件才算"真的在出数据"
    private static readonly MouseSource[] Priority =
    {
        MouseSource.RawInput, MouseSource.Hid, MouseSource.Hook, MouseSource.CursorPoll
    };

    private readonly object _gate = new();

    private readonly long[] _events = new long[SourceCount];     // 收到事件次数（含零位移）
    private readonly long[] _nonzero = new long[SourceCount];    // 累计位移非零次数
    private readonly long[] _window = new long[SourceCount];     // 最近一秒内的非零次数
    private readonly long[] _perSecond = new long[SourceCount];  // 上一秒结算结果

    private volatile int _active = (int)MouseSource.RawInput;

    // 相机消费的累积位移
    private double _pendingX;
    private double _pendingY;
    private double _totalX;
    private double _totalY;
    private double _pendingWheel;

    // 键盘
    private readonly bool[] _down = new bool[256];
    private readonly bool[] _previous = new bool[256];
    private readonly bool[] _edge = new bool[256];
    private long _keyPollTicks;

    public event Action<int, int>? MouseMoved;
    public event Action<int>? WheelMoved;

    public bool RawMouseRegistered { get; set; }
    public bool HookInstalled { get; set; }
    public bool HidOpened { get; set; }

    /// <summary>HID 枚举的人话结论（"枚举 N 个，鼠标可读 M 个，拒因：..."），进诊断文本。</summary>
    public string HidSummary { get; set; } = "未尝试";

    public bool CursorPollEnabled { get; set; } = true;

    // ---- 输入线程的底层诊断（"窗口到底有没有收到消息"这一层）----
    private long _winMessages;        // 输入窗口收到的消息总数（0 = 窗口根本没被派发）
    private long _wmInputMessages;    // 其中 WM_INPUT 的条数（0 = Raw Input 没送到）
    private long _loopHeartbeat;      // 消息循环每秒一次的 WM_TIMER 心跳（证明循环在转）
    private long _hookCalls;          // 钩子回调被调用的次数（0 = 钩子没生效）

    public void ReportWindowMessage() => Interlocked.Increment(ref _winMessages);
    public void ReportWmInput() => Interlocked.Increment(ref _wmInputMessages);
    public void ReportHeartbeat() => Interlocked.Increment(ref _loopHeartbeat);
    public void ReportHookCall() => Interlocked.Increment(ref _hookCalls);

    public long WinMessages => Interlocked.Read(ref _winMessages);
    public long WmInputMessages => Interlocked.Read(ref _wmInputMessages);
    public long LoopHeartbeat => Interlocked.Read(ref _loopHeartbeat);
    public long HookCalls => Interlocked.Read(ref _hookCalls);

    public MouseSource ActiveSource
    {
        get => (MouseSource)_active;
        set => _active = (int)value;
    }

    // ---------------------------------------------------------------- 鼠标

    /// <summary>某个来源收到了一个事件（哪怕位移是 0）。</summary>
    public void ReportEvent(MouseSource source)
    {
        lock (_gate) _events[(int)source]++;
    }

    public void AddMouse(int dx, int dy, MouseSource source)
    {
        var index = (int)source;
        var drives = false;

        lock (_gate)
        {
            _events[index]++;
            if (dx == 0 && dy == 0) return;

            _nonzero[index]++;
            _window[index]++;

            // 只有当前激活的来源驱动相机，否则多路同时工作会翻倍
            if ((MouseSource)_active != source) return;

            _pendingX += dx;
            _pendingY += dy;
            _totalX += dx;
            _totalY += dy;
            drives = true;
        }

        // 在锁外抛事件，避免订阅方回头再拿锁
        if (drives) MouseMoved?.Invoke(dx, dy);
    }

    public void AddWheel(int delta, MouseSource source)
    {
        lock (_gate)
        {
            if ((MouseSource)_active != source) return;
            _pendingWheel += delta;
        }

        WheelMoved?.Invoke(delta);
    }

    public (double X, double Y) ConsumeMouse()
    {
        lock (_gate)
        {
            var result = (_pendingX, _pendingY);
            _pendingX = 0;
            _pendingY = 0;
            return result;
        }
    }

    public double ConsumeWheel()
    {
        lock (_gate)
        {
            var wheel = _pendingWheel;
            _pendingWheel = 0;
            return wheel;
        }
    }

    /// <summary>
    /// 每秒调用一次：结算上一个窗口的计数，并重新挑选驱动来源。
    /// 规则：优先选"一秒内数据量像样"的最高优先级来源；都没数据就保持现状不动。
    /// </summary>
    public MouseSource TickSecond()
    {
        lock (_gate)
        {
            for (var i = 0; i < SourceCount; i++)
            {
                _perSecond[i] = _window[i];
                _window[i] = 0;
            }
        }

        var current = ActiveSource;

        // 1) 优先级顺序里第一个"数据量够"的
        foreach (var candidate in Priority)
        {
            if (PerSecond(candidate) < MeaningfulPerSecond) continue;
            if (candidate == current) return current;

            ActiveSource = candidate;
            Log.Info("鼠标输入来源切换为: " + Name(candidate) + "（每秒 " + PerSecond(candidate) + " 次位移）");
            return candidate;
        }

        // 2) 都没达到门槛，就选数据最多的那个（可能是很慢的鼠标移动）
        var best = MouseSource.None;
        var bestCount = 0L;
        foreach (var candidate in Priority)
        {
            var count = PerSecond(candidate);
            if (count <= bestCount) continue;
            best = candidate;
            bestCount = count;
        }

        if (best != MouseSource.None && best != current)
        {
            ActiveSource = best;
            Log.Info("鼠标输入来源切换为: " + Name(best) + "（每秒 " + bestCount + " 次，数据量偏低）");
            return best;
        }

        return current;
    }

    public long PerSecond(MouseSource source) => _perSecond[(int)source];

    public long TotalNonZero(MouseSource source) => _nonzero[(int)source];

    public long TotalEvents(MouseSource source) => _events[(int)source];

    /// <summary>上一次结算里，三路来源加起来一共多少次位移。</summary>
    public long PerSecondSum()
    {
        var sum = 0L;
        for (var i = 0; i < SourceCount; i++) sum += _perSecond[i];
        return sum;
    }

    public static string Name(MouseSource source) => source switch
    {
        MouseSource.RawInput => "Raw Input",
        MouseSource.Hid => "HID 直读",
        MouseSource.Hook => "低级钩子",
        MouseSource.CursorPoll => "光标轮询",
        _ => "无"
    };

    // ---------------------------------------------------------------- 键盘

    private long _keyHookEvents;
    private long _keyHookLastTick;

    /// <summary>键盘钩子最近是否在出数据（2 秒内有事件）。</summary>
    public bool KeyHookActive =>
        Interlocked.Read(ref _keyHookEvents) > 0 &&
        Environment.TickCount64 - Interlocked.Read(ref _keyHookLastTick) < 2000;

    public long KeyHookEvents => Interlocked.Read(ref _keyHookEvents);

    public void ReportKeyHookEvent()
    {
        Interlocked.Increment(ref _keyHookEvents);
        Interlocked.Exchange(ref _keyHookLastTick, Environment.TickCount64);
    }

    /// <summary>键盘钩子直接写入按键状态（钩子比轮询可靠，见 Native 里的说明）。</summary>
    public void SetKeyFromHook(int vk, bool down)
    {
        if (vk is < 0 or > 255) return;

        lock (_gate)
        {
            if (_down[vk] == down) return;
            _down[vk] = down;
            _previous[vk] = down;
            if (down) _edge[vk] = true;
        }
    }

    public void PollKeysOnce()
    {
        lock (_gate)
        {
            _keyPollTicks++;

            // 键盘钩子正在出数据时就不要再轮询覆盖它 ——
            // 轮询在"游戏以管理员权限在前台"时会全部读到 0，会把钩子的正确状态冲掉。
            if (KeyHookActive) return;

            for (var i = 0; i < Keys.Watched.Length; i++)
            {
                var vk = Keys.Watched[i];
                var isDown = Native.IsKeyDown(vk);
                _down[vk] = isDown;
                if (isDown && !_previous[vk]) _edge[vk] = true;
                _previous[vk] = isDown;
            }
        }
    }

    public bool IsDown(int vk)
    {
        lock (_gate) return _down[vk];
    }

    public bool ConsumeEdge(int vk)
    {
        lock (_gate)
        {
            if (!_edge[vk]) return false;
            _edge[vk] = false;
            return true;
        }
    }

    public void ClearEdges()
    {
        lock (_gate) Array.Clear(_edge, 0, _edge.Length);
    }

    // ---------------------------------------------------------------- 诊断输出

    /// <summary>给 /state 用：一眼能看出输入到底通没通。</summary>
    public void AppendJson(System.Text.StringBuilder sb)
    {
        lock (_gate)
        {
            sb.Append("\"input\":{");
            sb.Append("\"active\":\"").Append(JsonName(ActiveSource)).Append('"');
            sb.Append(",\"perSecond\":").Append(PerSecondSum());
            sb.Append(",\"rawRegistered\":").Append(RawMouseRegistered ? "true" : "false");
            sb.Append(",\"hookInstalled\":").Append(HookInstalled ? "true" : "false");
            sb.Append(",\"hidOpened\":").Append(HidOpened ? "true" : "false");
            sb.Append(",\"totalX\":").Append(Math.Round(_totalX));
            sb.Append(",\"totalY\":").Append(Math.Round(_totalY));
            // 前导逗号不能少！这里漏一个逗号就会让整个 /state 变成非法 JSON，
            // 前端 JSON.parse 抛异常 → 渲染器以为"连不上桥接"→ 什么都不画。
            // 排查时务必用严格 JSON 解析验证，用正则抠数字是发现不了的。
            sb.Append(",\"sources\":{");
            for (var i = 1; i < SourceCount; i++)
            {
                if (i > 1) sb.Append(',');
                var source = (MouseSource)i;
                sb.Append('"').Append(JsonName(source)).Append("\":{");
                sb.Append("\"events\":").Append(_events[i]);
                sb.Append(",\"moves\":").Append(_nonzero[i]);
                sb.Append(",\"perSecond\":").Append(_perSecond[i]);
                sb.Append('}');
            }

            sb.Append("},\"keyPollTicks\":").Append(_keyPollTicks);
            sb.Append(",\"keyHookEvents\":").Append(Interlocked.Read(ref _keyHookEvents));
            sb.Append(",\"keyHookActive\":").Append(KeyHookActive ? "true" : "false");
            sb.Append(",\"winMessages\":").Append(Interlocked.Read(ref _winMessages));
            sb.Append(",\"wmInputMessages\":").Append(Interlocked.Read(ref _wmInputMessages));
            sb.Append(",\"loopHeartbeat\":").Append(Interlocked.Read(ref _loopHeartbeat));
            sb.Append(",\"hookCalls\":").Append(Interlocked.Read(ref _hookCalls));
            sb.Append('}');
        }
    }

    private static string JsonName(MouseSource source) => source switch
    {
        MouseSource.RawInput => "rawInput",
        MouseSource.Hid => "hid",
        MouseSource.Hook => "hook",
        MouseSource.CursorPoll => "cursorPoll",
        _ => "none"
    };
}
