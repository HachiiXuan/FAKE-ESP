using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FakeEsp;

/// <summary>
/// 假外挂的"世界"。核心设计思路：
///
///   **不要试图和游戏世界对齐，只要做到运动耦合。**
///
/// 我们看不到游戏内部，所以绝对位置永远对不上。但只要满足两点，
/// 大脑就会认定这些框"长在游戏里"：
///   1) 相机的 yaw/pitch 用的是和游戏同一个鼠标原始增量（同一套手感曲线）；
///   2) 框是真实的 3D 空间物体，转视角时有正确的透视视差。
/// 于是 FOV / 灵敏度两个滑块就能把错觉调到几乎看不出来。
/// </summary>
internal sealed class World
{
    // ---------------- 可调参数 ----------------
    //
    // 想加新效果时的固定套路（扩展点就这一处）：
    //   1) 在这里加一个带默认值的字段
    //   2) 在 ToJson() 里加一行
    //   3) 在 ApplySettings() 里加一行
    //   4) 在 web/tune.html 的 CONTROLS 表里加一行，在 esp.js / OverlayWindow 里读它
    // 不需要改协议、不需要改前端框架。

    internal sealed class Options
    {
        // ---- 目标 ----
        public int Count = 12;             // 框的数量（设置界面给 5~20）
        public double MinDist = 20;        // 近于此距离直接剔除（0~100m），避免框在脸上炸开
        public double MaxDist = 60;        // 最远距离
        public bool Jitter = true;         // 高频抖动（模拟真实 ESP 的数据刷新）
        public bool Breathing = false;     // 相机轻微呼吸摆动（默认关，避免掩盖输入问题）

        // ---- 画面元素 ----
        public bool WorldBoxes = false;    // 3D 线框盒子：转视角能看出体积
        public bool Dist = true;           // 距离数字
        public bool Health = true;         // 假血条
        public bool Lines = true;          // 准星到目标的连线

        public bool Names = false;         // 目标假名字
        public bool HeadMarker = true;      // 头顶小标记
        public bool Offscreen = false;     // 屏外方向箭头
        public bool GroundCross = false;   // 落地参考线
        public bool Grid = false;          // 地面网格
        public bool Radar = false;         // 右下角雷达
        public bool Weapons = true;        // 中心准星 / FOV 圈

        // ---- 观感 ----
        public int Thickness = 2;          // 框线宽
        public double Glow = 0;            // 发光强度
        public string Color = "#ffffff";   // 框颜色（默认白色）
        public double Fov = 120;           // 水平视场角（对齐游戏里的 FOV 设置）
        public double Sens = 0.1;          // 每个鼠标原始计数转多少度
        public double MoveSpeed = 6.0;     // WASD 速度 m/s
        public string Bg = "transparent";  // transparent | green | dark | magenta
        public bool Enabled = true;        // 总开关（一键隐身，Ins 键）

        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"count\":").Append(Count);
            sb.Append(",\"minDist\":").Append(Num(MinDist));
            sb.Append(",\"maxDist\":").Append(Num(MaxDist));
            sb.Append(",\"jitter\":").Append(B(Jitter));
            sb.Append(",\"breathing\":").Append(B(Breathing));

            sb.Append(",\"worldBoxes\":").Append(B(WorldBoxes));
            sb.Append(",\"dist\":").Append(B(Dist));
            sb.Append(",\"health\":").Append(B(Health));
            sb.Append(",\"lines\":").Append(B(Lines));

            sb.Append(",\"names\":").Append(B(Names));
            sb.Append(",\"headMarker\":").Append(B(HeadMarker));
            sb.Append(",\"offscreen\":").Append(B(Offscreen));
            sb.Append(",\"groundCross\":").Append(B(GroundCross));
            sb.Append(",\"grid\":").Append(B(Grid));
            sb.Append(",\"radar\":").Append(B(Radar));
            sb.Append(",\"weapons\":").Append(B(Weapons));

            sb.Append(",\"thickness\":").Append(Thickness);
            sb.Append(",\"glow\":").Append(Num(Glow));
            sb.Append(",\"color\":\"").Append(JsonEscape(Color)).Append('"');
            sb.Append(",\"fov\":").Append(Num(Fov));
            sb.Append(",\"sens\":").Append(Num(Sens, 5));
            sb.Append(",\"moveSpeed\":").Append(Num(MoveSpeed));
            sb.Append(",\"bg\":\"").Append(JsonEscape(Bg)).Append('"');
            sb.Append(",\"enabled\":").Append(B(Enabled));
            sb.Append('}');
            return sb.ToString();
        }

        private static string B(bool value) => value ? "true" : "false";
    }

    // ---------------- 实体 ----------------

    public enum Kind
    {
        Player = 0,
        Scav = 1,
        Loot = 2
    }

    internal sealed class Entity
    {
        public int Id;                  // 稳定标识，渲染端靠它判断"谁消失了"→ 击杀播报
        public double X, Y, Z;          // 中心点
        public double Sx, Sy, Sz;       // 尺寸
        public double Vx, Vz;            // 漂移速度
        public double Phase;            // 摆动相位
        public double Wobble;           // 摆动幅度
        public double Wobble2;
        public double Hp = 1.0;
        public Kind Kind;
        public string Name = "Player_01";
        public double Alpha = 1.0;
        public bool Dying;
        public double DieT;
        public double Life;
        public double JitterPhase;
        public double HopPhase;
    }

    /// <summary>给同进程的覆盖窗用的只读快照。</summary>
    public readonly struct EntityView
    {
        public readonly int Id;
        public readonly double X, Y, Z, Sx, Sy, Sz, Hp, Alpha;
        public readonly int Kind;
        public readonly string Name;

        public EntityView(Entity e)
        {
            Id = e.Id;
            X = e.X;
            Y = e.Y;
            Z = e.Z;
            Sx = e.Sx;
            Sy = e.Sy;
            Sz = e.Sz;
            Hp = e.Hp;
            Alpha = e.Alpha;
            Kind = (int)e.Kind;
            Name = e.Name;
        }
    }

    // ---------------- 状态 ----------------

    private readonly object _gate = new();
    private readonly InputState _input;
    private readonly Random _rng = new(20260101);
    private readonly List<Entity> _entities = new();

    private double _yaw;
    private double _pitch;
    private double _eyeX;
    private double _eyeY = 1.70;   // 站姿眼高
    private double _eyeZ;
    private double _time;
    private long _seq;
    private double _smoothedFps = 120;
    private long _lastTick;
    private double _killTimer = 2.0;
    private double _idleYaw;
    private double _idlePitch;
    private double _velY;              // 垂直速度（跳跃用）
    private int _nextNameIndex;
    private int _nextId = 1;

    private const double JumpSpeed = 5.2;    // 起跳初速度 m/s
    private const double Gravity = 14.0;     // 重力 m/s²（偏大一点，手感更干脆）

    public const double EyeHeight = 1.70;
    public const double NearPlane = 0.05;

    public Options Opt { get; } = new();

    /// <summary>当前实体数量（给主窗口显示用）。</summary>
    public int TargetCount
    {
        get { lock (_gate) return _entities.Count; }
    }

    /// <summary>世界模拟线程的实测频率。</summary>
    public double SimHz
    {
        get { lock (_gate) return _smoothedFps; }
    }

    /// <summary>覆盖窗的实测帧率，由 OverlayWindow 回填。</summary>
    public double OverlayFps { get; set; }

    /// <summary>覆盖层开关由 Program 消费，这里只是把热键事件转发出去。</summary>
    public event Action<string>? Hotkey;

    public World(InputState input)
    {
        _input = input;
        ClearEdgesQuietly();

        // 关键：按默认 count 先把实体生成出来，否则世界是空的
        SyncCount();

        // 鼠标事件到达时**立即**积分视角，而不是等到模拟 tick。
        // 这样相机精度完全不受 tick 频率影响（Windows 的 Sleep 粒度约 15ms，
        // 靠 tick 积分会让快速甩枪的位移被量化）。
        _input.MouseMoved += OnMouseMoved;
        Log.Info("世界初始化完成: " + _entities.Count + " 个目标");
    }

    private void OnMouseMoved(int dx, int dy)
    {
        var sens = Opt.Sens;
        lock (_gate)
        {
            _yaw += dx * sens;
            _pitch -= dy * sens;

            if (_yaw > 360) _yaw -= 360;
            if (_yaw < -360) _yaw += 360;
            _pitch = Math.Clamp(_pitch, -89.0, 89.0);
        }
    }

    private void ClearEdgesQuietly()
    {
        // 启动瞬间可能有残留的按键边沿，先清一遍
        foreach (var vk in Keys.Watched) _input.ConsumeEdge(vk);
    }

    // ---------------- 主循环 ----------------

    public void Tick()
    {
        var now = Environment.TickCount64;
        double dt;
        lock (_gate)
        {
            dt = _lastTick == 0 ? 1.0 / 120.0 : (now - _lastTick) / 1000.0;
            _lastTick = now;
        }

        // 卡顿时不要让物理爆掉
        if (dt > 0.25) dt = 0.25;
        if (dt <= 0) dt = 1.0 / 240.0;

        var instantFps = 1.0 / dt;
        lock (_gate)
        {
            _smoothedFps = _smoothedFps * 0.92 + instantFps * 0.08;
        }

        HandleHotkeys();
        UpdateCamera(dt);
        UpdateEntities(dt);

        lock (_gate)
        {
            _time += dt;
            _seq++;
        }
    }

    // ---------------- 热键 ----------------
    //
    // 刻意只留两个。大多数开关请用设置界面（OBS 自定义浏览器面板里的 /tune，
    // 或覆盖窗自带的设置窗口），按 F1~F12 记不住的。

    private void HandleHotkeys()
    {
        // 只有两个热键，按需求保留：
        //   Ins  —— 一键隐身（总开关）
        //   Home —— 视角回中
        if (_input.ConsumeEdge(Keys.Insert))
        {
            Opt.Enabled = !Opt.Enabled;
            Log.Info("热键 Ins: 总开关 " + (Opt.Enabled ? "开" : "关"));
            Hotkey?.Invoke("总开关");
        }

        if (_input.ConsumeEdge(Keys.Home))
        {
            _yaw = 0;
            _pitch = 0;
            Log.Info("热键 Home: 视角已回中");
        }
    }

    private void CycleBackground()
    {
        Opt.Bg = Opt.Bg switch
        {
            "transparent" => "green",
            "green" => "dark",
            "dark" => "magenta",
            _ => "transparent"
        };
    }

    public void ResetOptions()
    {
        var d = new Options();
        Opt.Fov = d.Fov;
        Opt.Sens = d.Sens;
        Opt.Count = d.Count;
        Opt.MinDist = d.MinDist;
        Opt.MaxDist = d.MaxDist;
        Opt.MoveSpeed = d.MoveSpeed;
        Opt.Jitter = d.Jitter;
        Opt.Breathing = d.Breathing;

        Opt.WorldBoxes = d.WorldBoxes;
        Opt.Dist = d.Dist;
        Opt.Health = d.Health;
        Opt.Lines = d.Lines;

        Opt.Names = d.Names;
        Opt.HeadMarker = d.HeadMarker;
        Opt.Offscreen = d.Offscreen;
        Opt.GroundCross = d.GroundCross;
        Opt.Grid = d.Grid;
        Opt.Radar = d.Radar;
        Opt.Weapons = d.Weapons;

        Opt.Thickness = d.Thickness;
        Opt.Glow = d.Glow;
        Opt.Color = d.Color;
        Opt.Fov = d.Fov;
        Opt.Sens = d.Sens;
        Opt.MoveSpeed = d.MoveSpeed;
        Opt.Count = d.Count;
        Opt.MinDist = d.MinDist;
        Opt.MaxDist = d.MaxDist;
        Opt.Bg = d.Bg;
        Opt.Enabled = true;
        SyncCount();
    }

    /// <summary>
    /// 预设。"default" 就是界面「默认设置」按钮用的那一套；
    /// "full" 只是留给我压力测试用的，界面上没有对应按钮。
    /// </summary>
    public void ApplyPreset(string name)
    {
        switch (name)
        {
            case "full":
                Opt.WorldBoxes = true; Opt.Dist = true; Opt.Health = true; Opt.Lines = true;
                Opt.Names = true; Opt.HeadMarker = true; Opt.Offscreen = true; Opt.GroundCross = true;
                Opt.Grid = true; Opt.Radar = true; Opt.Weapons = true;
                break;
            case "default":
            case "minimal":
            default:
                ResetOptions();
                Log.Info("已套用默认设置");
                return;
        }

        Log.Info("已套用预设: " + name);
    }

    public void Recenter()
    {
        lock (_gate)
        {
            _yaw = 0;
            _pitch = 0;
        }
    }

    public void SetCount(int count)
    {
        Opt.Count = Math.Clamp(count, 1, 20);
        SyncCount();
    }

    // ---------------- 相机 ----------------

    private void UpdateCamera(double dt)
    {
        // 视角已经在 OnMouseMoved 里按事件积分过了，这里只处理 WASD 位移
        var speed = Opt.MoveSpeed;
        if (_input.IsDown(Keys.Shift)) speed *= 2.2;
        if (_input.IsDown(Keys.Control)) speed *= 0.35;

        var forward = 0.0;
        var strafe = 0.0;
        var lift = 0.0;

        if (_input.IsDown(Keys.W) || _input.IsDown(Keys.Up)) forward += 1;
        if (_input.IsDown(Keys.S) || _input.IsDown(Keys.Down)) forward -= 1;
        if (_input.IsDown(Keys.D) || _input.IsDown(Keys.Right)) strafe += 1;
        if (_input.IsDown(Keys.A) || _input.IsDown(Keys.Left)) strafe -= 1;
        if (_input.IsDown(Keys.E)) lift += 1;   // Q/E 是自由升降
        if (_input.IsDown(Keys.Q)) lift -= 1;

        // 空格 = 跳跃：给一个向上初速度，然后重力把它拉回来，落到眼高为止。
        // （之前是"按住就往上飞、松开也不掉"，那是 bug）
        var jumpPressed = _input.ConsumeEdge(Keys.Space);

        lock (_gate)
        {
            var yawRad = _yaw * Math.PI / 180.0;
            var fx = Math.Sin(yawRad);
            var fz = Math.Cos(yawRad);
            var rx = Math.Cos(yawRad);
            var rz = -Math.Sin(yawRad);

            var length = Math.Sqrt(forward * forward + strafe * strafe + lift * lift);
            if (length > 1e-6)
            {
                _eyeX += (fx * forward + rx * strafe) / length * speed * dt;
                _eyeZ += (fz * forward + rz * strafe) / length * speed * dt;
                _eyeY += lift / length * speed * dt;
            }

            // 跳跃物理
            var grounded = _eyeY <= EyeHeight + 0.02 && Math.Abs(_velY) < 0.05;
            if (jumpPressed && grounded) _velY = JumpSpeed;

            _velY -= Gravity * dt;
            _eyeY += _velY * dt;
            if (_eyeY <= EyeHeight && _velY <= 0)
            {
                _eyeY = EyeHeight;
                _velY = 0;
            }

            // 轻微"呼吸感"：让画面不完全死住。
            // 默认关闭 —— 它会掩盖"鼠标输入到底通没通"这个关键判断，
            // 而且会让人觉得视角"不跟手"。
            if (Opt.Breathing)
            {
                _idlePitch = Math.Sin(_time * 0.7) * 0.22;
                _idleYaw = Math.Sin(_time * 0.31) * 0.35;
            }
            else
            {
                _idlePitch = 0;
                _idleYaw = 0;
            }

            _eyeY = Math.Clamp(_eyeY, 0.2, 60);
        }
    }

    public void GetCamera(out double x, out double y, out double z, out double yaw, out double pitch, out double fov)
    {
        lock (_gate)
        {
            x = _eyeX;
            y = _eyeY;
            z = _eyeZ;
            yaw = _yaw + _idleYaw;
            pitch = _pitch + _idlePitch;
            fov = Opt.Fov;
        }
    }

    // ---------------- 实体 ----------------

    private void SyncCount()
    {
        lock (_gate)
        {
            while (_entities.Count > Opt.Count)
            {
                _entities.RemoveAt(_entities.Count - 1);
            }

            while (_entities.Count < Opt.Count)
            {
                _entities.Add(SpawnNew());
            }
        }
    }

    private static readonly string[] NamePrefixes = { "Player", "Scav", "Loot" };
    private static readonly string[] PlayerTags = { "Ttv", "xD", "Pro", "Noob", "Camper", "Sweat", "Bot", "Afk" };

    private Entity SpawnNew()
    {
        var kindRoll = _rng.NextDouble();
        Kind kind;
        if (kindRoll < 0.74) kind = Kind.Player;
        else if (kindRoll < 0.93) kind = Kind.Scav;
        else kind = Kind.Loot;

        double sx, sy, sz;
        switch (kind)
        {
            case Kind.Player:
                sx = 0.62; sy = 1.76; sz = 0.42;
                break;
            case Kind.Scav:
                sx = 0.60; sy = 1.72; sz = 0.42;
                break;
            default:
                sx = 0.44; sy = 0.30; sz = 0.60;
                break;
        }

        double px, py, pz, yawDeg;
        lock (_gate)
        {
            px = _eyeX;
            py = _eyeY;
            pz = _eyeZ;
            yawDeg = _yaw;
        }

        // 方位角偏向镜头前方 ±115°：视野里更热闹，但转身依然能看到东西
        var azimuth = yawDeg * Math.PI / 180.0 + (_rng.NextDouble() - 0.5) * (230.0 * Math.PI / 180.0);
        // 距离偏向近处：pow 指数让大部分目标落在 10~40m，屏幕上的框才够大够好看
        var nearest = Math.Max(9.0, Opt.MinDist + 2.0);
        var dist = nearest + Math.Pow(_rng.NextDouble(), 1.8) * Math.Max(4, Opt.MaxDist - nearest);

        var e = new Entity
        {
            Id = _nextId++,
            X = px + Math.Sin(azimuth) * dist,
            Y = sy * 0.5 + _rng.NextDouble() * 0.25,
            Z = pz + Math.Cos(azimuth) * dist,
            Sx = sx,
            Sy = sy,
            Sz = sz,
            Kind = kind,
            Hp = 0.35 + _rng.NextDouble() * 0.65,
            Phase = _rng.NextDouble() * Math.PI * 2,
            JitterPhase = _rng.NextDouble() * Math.PI * 2,
            HopPhase = _rng.NextDouble() * Math.PI * 2,
            Wobble = 0.25 + _rng.NextDouble() * 0.55,
            Wobble2 = 1.1 + _rng.NextDouble() * 2.2,
            Vx = (_rng.NextDouble() - 0.5) * 3.2,
            Vz = (_rng.NextDouble() - 0.5) * 3.2,
            Life = 0
        };

        e.Name = BuildName(kind);
        return e;
    }

    private string BuildName(Kind kind)
    {
        _nextNameIndex++;
        switch (kind)
        {
            case Kind.Player:
                var tag = PlayerTags[_rng.Next(PlayerTags.Length)];
                return _rng.NextDouble() < 0.45
                    ? tag + "_" + NamePrefixes[0] + _rng.Next(10, 99)
                    : NamePrefixes[0] + "_" + _rng.Next(100, 999);
            case Kind.Scav:
                return NamePrefixes[1] + "_" + _rng.Next(10, 99);
            default:
                return NamePrefixes[2] + "_" + _nextNameIndex.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void UpdateEntities(double dt)
    {
        lock (_gate)
        {
            var yawRad = _yaw * Math.PI / 180.0;
            var fx = Math.Sin(yawRad);
            var fz = Math.Cos(yawRad);

            for (var i = _entities.Count - 1; i >= 0; i--)
            {
                var e = _entities[i];
                e.Life += dt;

                if (e.Dying)
                {
                    e.DieT += dt;
                    e.Alpha = Math.Max(0, 1.0 - e.DieT / 0.65);
                    if (e.Alpha <= 0.001)
                    {
                        _entities[i] = RespawnAway(e);
                    }

                    continue;
                }

                // 漂移：缓慢随机游走 + 温和地留在相机附近，这样不会走丢
                e.Phase += dt;
                e.HopPhase += dt * 2.4;

                e.X += e.Vx * dt;
                e.Z += e.Vz * dt;

                var relX = e.X - _eyeX;
                var relZ = e.Z - _eyeZ;
                var dist = Math.Sqrt(relX * relX + relZ * relZ);

                // 离远了往回飘；离太近（小于用户的"近距离剔除"）则推开，
                // 免得一个大框糊在脸上
                var nearLimit = Math.Max(3.0, Opt.MinDist);
                var pull = dist > Opt.MaxDist ? 3.2 : (dist < nearLimit ? -3.0 : 0.0);
                if (Math.Abs(pull) > 0.001 && dist > 1e-3)
                {
                    e.X -= relX / dist * pull * dt;
                    e.Z -= relZ / dist * pull * dt;
                }

                // 让它更愿意待在相机前方（前方 0 权重，正后方权重最高 → 会慢慢绕过来）
                var behind = -(relX * fx + relZ * fz) / Math.Max(dist, 1e-3);
                if (behind > 0.2 && dist > 3)
                {
                    // (-fz, fx) 就是垂直于视线方向的单位向量
                    e.X += -fz * behind * 1.1 * dt * 3;
                    e.Z += fx * behind * 1.1 * dt * 3;
                }

                // 每帧微调朝向速度，产生有机的晃动
                e.Vx += (_rng.NextDouble() - 0.5) * 1.6 * dt;
                e.Vz += (_rng.NextDouble() - 0.5) * 1.6 * dt;
                e.Vx = Math.Clamp(e.Vx, -2.6, 2.6);
                e.Vz = Math.Clamp(e.Vz, -2.6, 2.6);

                // 全部贴地：箱子和人形一样站在地上，只有一点点上下浮动
                e.Y = e.Sy * 0.5 + Math.Sin(e.HopPhase) * 0.02;

                // 太远了直接换个地方重生，保持永远"有东西可看"
                if (dist > Opt.MaxDist * 1.9 && e.Life > 6)
                {
                    _entities[i] = RespawnAway(e);
                }
            }

            // 目标偶尔"被击杀"淡出再换个地方重生，让画面看起来是活的。
            // 注意：这不是"击杀播报"功能（那个按需求已删除），这里没有任何文字，
            // 只是让框偶尔消失/出现。
            _killTimer -= dt;
            if (_killTimer <= 0)
            {
                _killTimer = 1.6 + _rng.NextDouble() * 3.4;
                var alive = new List<int>();
                for (var i = 0; i < _entities.Count; i++)
                {
                    if (!_entities[i].Dying) alive.Add(i);
                }

                if (alive.Count > 2)
                {
                    var pick = _entities[alive[_rng.Next(alive.Count)]];
                    pick.Dying = true;
                    pick.DieT = 0;
                }
            }
        }
    }

    private Entity RespawnAway(Entity e)
    {
        // 换一个全新 id：从渲染端看就是"这个人没了，另一个人出生了"
        e.Id = _nextId++;
        e.Dying = false;
        e.DieT = 0;
        e.Alpha = 1.0;
        e.Life = 0;
        e.Hp = 0.35 + _rng.NextDouble() * 0.65;
        e.Phase = _rng.NextDouble() * Math.PI * 2;
        e.JitterPhase = _rng.NextDouble() * Math.PI * 2;
        e.HopPhase = _rng.NextDouble() * Math.PI * 2;
        e.Vx = (_rng.NextDouble() - 0.5) * 3.2;
        e.Vz = (_rng.NextDouble() - 0.5) * 3.2;

        var azimuth = _yaw * Math.PI / 180.0 + (_rng.NextDouble() - 0.5) * (230.0 * Math.PI / 180.0);
        var nearest = Math.Max(12.0, Opt.MinDist + 3.0);
        var dist = nearest + Math.Pow(_rng.NextDouble(), 1.8) * Math.Max(4, Opt.MaxDist - nearest);
        e.X = _eyeX + Math.Sin(azimuth) * dist;
        e.Z = _eyeZ + Math.Cos(azimuth) * dist;
        e.Name = BuildName(e.Kind);
        return e;
    }

    public EntityView[] SnapshotEntities()
    {
        lock (_gate)
        {
            var result = new EntityView[_entities.Count];
            for (var i = 0; i < _entities.Count; i++) result[i] = new EntityView(_entities[i]);

            return result;
        }
    }

    // ---------------- 序列化 ----------------

    public string BuildStateJson()
    {
        // 输入诊断在 World 锁之外算好，避免两把锁形成嵌套依赖
        var inputJson = new StringBuilder(512);
        inputJson.Append(',');
        _input.AppendJson(inputJson);
        var inputPart = inputJson.ToString();

        var sb = new StringBuilder(4096);
        lock (_gate)
        {
            sb.Append("{\"v\":1,\"seq\":").Append(_seq);
            sb.Append(",\"t\":").Append(Num(_time, 2));
            sb.Append(",\"fps\":").Append(Num(_smoothedFps, 1));
            sb.Append(",\"simHz\":").Append(Num(_smoothedFps, 1));

            sb.Append(",\"cam\":{\"x\":").Append(Num(_eyeX, 2));
            sb.Append(",\"y\":").Append(Num(_eyeY, 2));
            sb.Append(",\"z\":").Append(Num(_eyeZ, 2));
            sb.Append(",\"yaw\":").Append(Num(_yaw + _idleYaw, 3));
            sb.Append(",\"pitch\":").Append(Num(_pitch + _idlePitch, 3));
            sb.Append(",\"fov\":").Append(Num(Opt.Fov));
            sb.Append(",\"eye\":").Append(Num(EyeHeight));
            sb.Append('}');

            sb.Append(",\"opts\":").Append(Opt.ToJson());
            sb.Append(inputPart);

            sb.Append(",\"boxes\":[");
            var first = true;
            foreach (var e in _entities)
            {
                if (!first) sb.Append(',');
                first = false;

                // 抖动：真实 ESP 每帧数据都在跳，这点高频噪声是"像"的关键
                var jx = 0.0;
                var jy = 0.0;
                var jz = 0.0;
                if (Opt.Jitter)
                {
                    var jp = e.JitterPhase + _time * 11.0;
                    jx = Math.Sin(jp * 1.7) * 0.012 + Math.Sin(jp * 3.9) * 0.006;
                    jy = Math.Sin(jp * 2.3 + 1.1) * 0.010;
                    jz = Math.Sin(jp * 2.9 + 2.2) * 0.012;
                }

                sb.Append("{\"id\":").Append(e.Id);
                sb.Append(",\"x\":").Append(Num(e.X + jx, 2));
                sb.Append(",\"y\":").Append(Num(e.Y + jy, 3));
                sb.Append(",\"z\":").Append(Num(e.Z + jz, 2));
                sb.Append(",\"sx\":").Append(Num(e.Sx, 2));
                sb.Append(",\"sy\":").Append(Num(e.Sy, 2));
                sb.Append(",\"sz\":").Append(Num(e.Sz, 2));
                sb.Append(",\"hp\":").Append(Num(e.Hp, 2));
                sb.Append(",\"k\":").Append((int)e.Kind);
                sb.Append(",\"a\":").Append(Num(e.Alpha, 2));
                sb.Append(",\"n\":\"").Append(JsonEscape(e.Name)).Append('"');
                sb.Append('}');
            }

            sb.Append(']');
            sb.Append('}');
        }

        return sb.ToString();
    }

    // ---------------- 调参接口 ----------------

    public string ApplySettings(Dictionary<string, string> query)
    {
        var applied = new List<string>();

        bool TryNum(string key, out double value)
        {
            value = 0;
            if (!query.TryGetValue(key, out var raw)) return false;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        bool TryBool(string key, out bool value)
        {
            value = false;
            if (!query.TryGetValue(key, out var raw)) return false;
            if (raw is "1" or "true" or "on" or "yes") { value = true; return true; }
            if (raw is "0" or "false" or "off" or "no") { value = false; return true; }
            return false;
        }

        if (TryNum("fov", out var fov)) { Opt.Fov = Math.Clamp(fov, 30, 150); applied.Add("fov"); }
        if (TryNum("sens", out var sens)) { Opt.Sens = Math.Clamp(sens, 0.0005, 0.5); applied.Add("sens"); }
        if (TryNum("count", out var count)) { SetCount((int)count); applied.Add("count"); }
        if (TryNum("minDist", out var minDist)) { Opt.MinDist = Math.Clamp(minDist, 0, 100); applied.Add("minDist"); }
        if (TryNum("maxDist", out var maxDist)) { Opt.MaxDist = Math.Clamp(maxDist, 15, 400); applied.Add("maxDist"); }
        if (TryNum("moveSpeed", out var moveSpeed)) { Opt.MoveSpeed = Math.Clamp(moveSpeed, 0.5, 60); applied.Add("moveSpeed"); }
        if (TryNum("thickness", out var thickness)) { Opt.Thickness = Math.Clamp((int)thickness, 1, 8); applied.Add("thickness"); }
        if (TryNum("glow", out var glow)) { Opt.Glow = Math.Clamp(glow, 0, 1); applied.Add("glow"); }

        if (TryBool("jitter", out var jitter)) { Opt.Jitter = jitter; applied.Add("jitter"); }
        if (TryBool("breathing", out var breathing)) { Opt.Breathing = breathing; applied.Add("breathing"); }
        if (TryBool("worldBoxes", out var worldBoxes)) { Opt.WorldBoxes = worldBoxes; applied.Add("worldBoxes"); }
        if (TryBool("dist", out var dist)) { Opt.Dist = dist; applied.Add("dist"); }
        if (TryBool("health", out var health)) { Opt.Health = health; applied.Add("health"); }
        if (TryBool("lines", out var lines)) { Opt.Lines = lines; applied.Add("lines"); }
        if (TryBool("names", out var names)) { Opt.Names = names; applied.Add("names"); }
        if (TryBool("headMarker", out var headMarker)) { Opt.HeadMarker = headMarker; applied.Add("headMarker"); }
        if (TryBool("offscreen", out var offscreen)) { Opt.Offscreen = offscreen; applied.Add("offscreen"); }
        if (TryBool("groundCross", out var groundCross)) { Opt.GroundCross = groundCross; applied.Add("groundCross"); }
        if (TryBool("grid", out var grid)) { Opt.Grid = grid; applied.Add("grid"); }
        if (TryBool("radar", out var radar)) { Opt.Radar = radar; applied.Add("radar"); }
        if (TryBool("weapons", out var weapons)) { Opt.Weapons = weapons; applied.Add("weapons"); }
        if (TryBool("enabled", out var enabled)) { Opt.Enabled = enabled; applied.Add("enabled"); }

        if (query.TryGetValue("color", out var color) && IsSafeColor(color)) { Opt.Color = color; applied.Add("color"); }
        if (query.TryGetValue("bg", out var bg) && (bg is "transparent" or "green" or "dark" or "magenta"))
        {
            Opt.Bg = bg;
            applied.Add("bg");
        }

        if (query.TryGetValue("preset", out var preset))
        {
            ApplyPreset(preset);
            applied.Add("preset");
        }

        if (query.ContainsKey("recenter"))
        {
            Recenter();
            applied.Add("recenter");
        }

        if (query.ContainsKey("reset"))
        {
            ResetOptions();
            applied.Add("reset");
        }

        var result = new StringBuilder(256);
        result.Append("{\"ok\":true,\"applied\":[");
        for (var i = 0; i < applied.Count; i++)
        {
            if (i > 0) result.Append(',');
            result.Append('"').Append(applied[i]).Append('"');
        }

        result.Append("],\"opts\":").Append(Opt.ToJson()).Append('}');
        return result.ToString();
    }

    private static bool IsSafeColor(string value)
    {
        if (value.Length is < 4 or > 9) return false;
        if (value[0] != '#') return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>
    /// 按 key 读当前值（字符串形式）。直接从 ToJson() 里取，
    /// 这样加新参数时不用再维护第 5 份"key → 取值"的映射表。
    /// </summary>
    public bool TryGetOption(string key, out string value)
    {
        value = string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Opt.ToJson());
            if (!doc.RootElement.TryGetProperty(key, out var element)) return false;
            value = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Num(double value, int digits = 3) =>
        Math.Round(value, digits).ToString("0.###", CultureInfo.InvariantCulture);
}
