using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EntityView = FakeEsp.World.EntityView;

namespace FakeEsp;

/// <summary>
/// 单屏"边玩边看"用的覆盖窗。
///
///   · AllowsTransparency + 透明背景 → 只有框被画出来，其余像素完全透明
///   · WS_EX_TRANSPARENT             → 鼠标点击穿透，游戏照常收得到输入
///   · WS_EX_NOACTIVATE + Topmost     → 永远置顶，绝不抢焦点、不进任务栏
///
/// 用的是和 web/esp.js 完全同一套投影数学（见 Projection.cs），两个宿主画面一致。
///
/// ★ 性能经验（全是实测出来的，改之前请先看这段）
///   1) WPF 在这条绘制路径下，**每次 DrawGeometry / DrawRectangle 调用的固定开销约 0.4ms**，
///      和图元多少几乎无关。旧写法 18 个目标会发出约 210 次调用 → 106ms/帧 → 9fps。
///      所以这里把所有同类图元按透明度档位合批，把调用数压到 ~50 次。
///   2) 文字只能用 FormattedText + DrawText（内部有字形缓存）。用 BuildGeometry + 描边画笔
///      给文字加黑边，等于对每个字形轮廓做一次描边外扩，实测每行约 1ms。
///   3) 抗锯齿在这条路径下很贵，而 1px 外挂线本来就要锐利，所以打开 EdgeMode.Aliased。
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const double Deg = Math.PI / 180.0;
    private const int Levels = 5;              // 实体透明度档位：0.2 0.4 0.6 0.8 1.0
    private const int GridBands = 3;           // 地面网格按距离分三层渐隐

    private static readonly double[] GridAlphas = { 0.20, 0.11, 0.05 };

    // ================================================================ 合批缓冲

    /// <summary>线段批量：每档一个缓冲，最后每档只发一次 DrawGeometry。</summary>
    private sealed class SegBuffer
    {
        private readonly List<double>[] _data;

        public SegBuffer(int bands)
        {
            _data = new List<double>[bands];
            for (var i = 0; i < bands; i++) _data[i] = new List<double>();
        }

        public void Clear()
        {
            for (var i = 0; i < _data.Length; i++) _data[i].Clear();
        }

        public void Add(int band, double x1, double y1, double x2, double y2)
        {
            var d = _data[band];
            d.Add(x1);
            d.Add(y1);
            d.Add(x2);
            d.Add(y2);
        }

        public void Flush(DrawingContext dc, Func<int, Pen> penFor, Func<int, Pen>? overlayPenFor = null)
        {
            for (var i = 0; i < _data.Length; i++)
            {
                var d = _data[i];
                if (d.Count < 4) continue;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    for (var k = 0; k + 3 < d.Count; k += 4)
                    {
                        ctx.BeginFigure(new Point(d[k], d[k + 1]), false, false);
                        ctx.LineTo(new Point(d[k + 2], d[k + 3]), true, false);
                    }
                }

                geometry.Freeze();
                if (overlayPenFor != null) dc.DrawGeometry(null, overlayPenFor(i), geometry);
                dc.DrawGeometry(null, penFor(i), geometry);
            }
        }
    }

    /// <summary>矩形填充批量（假血条、雷达点）。</summary>
    private sealed class RectBuffer
    {
        private readonly List<double>[] _data;

        public RectBuffer(int bands)
        {
            _data = new List<double>[bands];
            for (var i = 0; i < bands; i++) _data[i] = new List<double>();
        }

        public void Clear()
        {
            for (var i = 0; i < _data.Length; i++) _data[i].Clear();
        }

        public void Add(int band, double x, double y, double w, double h)
        {
            var d = _data[band];
            d.Add(x);
            d.Add(y);
            d.Add(w);
            d.Add(h);
        }

        public void Flush(DrawingContext dc, Func<int, Brush> brushFor)
        {
            for (var i = 0; i < _data.Length; i++)
            {
                var d = _data[i];
                if (d.Count < 4) continue;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    for (var k = 0; k + 3 < d.Count; k += 4)
                    {
                        var x = d[k];
                        var y = d[k + 1];
                        ctx.BeginFigure(new Point(x, y), true, true);
                        ctx.LineTo(new Point(x + d[k + 2], y), true, false);
                        ctx.LineTo(new Point(x + d[k + 2], y + d[k + 3]), true, false);
                        ctx.LineTo(new Point(x, y + d[k + 3]), true, false);
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(brushFor(i), null, geometry);
            }
        }
    }

    /// <summary>三角形填充批量（头顶标记、屏外箭头）。</summary>
    private sealed class TriBuffer
    {
        private readonly List<double>[] _data;

        public TriBuffer(int bands)
        {
            _data = new List<double>[bands];
            for (var i = 0; i < bands; i++) _data[i] = new List<double>();
        }

        public void Clear()
        {
            for (var i = 0; i < _data.Length; i++) _data[i].Clear();
        }

        public void Add(int band, double ax, double ay, double bx, double by, double cx, double cy)
        {
            var d = _data[band];
            d.Add(ax);
            d.Add(ay);
            d.Add(bx);
            d.Add(by);
            d.Add(cx);
            d.Add(cy);
        }

        public void Flush(DrawingContext dc, Func<int, Brush> brushFor)
        {
            for (var i = 0; i < _data.Length; i++)
            {
                var d = _data[i];
                if (d.Count < 6) continue;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    for (var k = 0; k + 5 < d.Count; k += 6)
                    {
                        ctx.BeginFigure(new Point(d[k], d[k + 1]), true, true);
                        ctx.LineTo(new Point(d[k + 2], d[k + 3]), true, false);
                        ctx.LineTo(new Point(d[k + 4], d[k + 5]), true, false);
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(brushFor(i), null, geometry);
            }
        }
    }

    /// <summary>
    /// 绘制宿主。用 FrameworkElement.OnRender 是 WPF 里画自定义叠加层的惯用法；
    /// 在 Window 上重写 VisualChildrenCount 会和窗口模板打架，所以隔这一层。
    /// </summary>
    private sealed class Surface : FrameworkElement
    {
        private readonly OverlayWindow _owner;

        public Surface(OverlayWindow owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        protected override void OnRender(DrawingContext dc) => _owner.Draw(dc, ActualWidth, ActualHeight);
    }

    // ================================================================ 字段

    private readonly World _world;
    private readonly Surface _surface;
    private bool _ready;

    private readonly SegBuffer _wire = new(Levels);
    private readonly SegBuffer _ground = new(Levels);
    private readonly SegBuffer _outline = new(Levels);
    private readonly SegBuffer _bracket = new(Levels);
    private readonly SegBuffer _tracer = new(Levels);
    private readonly SegBuffer _grid = new(GridBands);
    private readonly SegBuffer _radarLines = new(1);

    private readonly RectBuffer _hpTrack = new(Levels);
    private readonly RectBuffer _hpFill = new(Levels);
    private readonly RectBuffer _radarDots = new(Levels);

    private readonly TriBuffer _markers = new(Levels);
    private readonly TriBuffer _arrows = new(Levels);

    private readonly Dictionary<int, Pen> _pens = new();
    private readonly Dictionary<int, SolidColorBrush> _brushes = new();
    private readonly Dictionary<string, FormattedText> _texts = new();

    private readonly Typeface _typeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private double _pixelsPerDip = 1;

    private readonly List<Item> _items = new();
    private readonly List<Item> _scratch = new();

    // 当帧的投影参数：放进字段，省得每个辅助方法都传一长串
    private Camera _k;
    private double _focal;
    private double _cx;
    private double _cy;

    private int _frames;
    private long _fpsAt;
    private double _fps;

    private readonly struct Item
    {
        public readonly EntityView V;
        public readonly double Dist;
        public readonly double Depth;
        public readonly double CamX;
        public readonly double CamY;
        public readonly double ScreenX;
        public readonly double ScreenY;
        public readonly ScreenBox? Box;

        public Item(EntityView v, double dist, double depth, double camX, double camY,
            double screenX, double screenY, ScreenBox? box)
        {
            V = v;
            Dist = dist;
            Depth = depth;
            CamX = camX;
            CamY = camY;
            ScreenX = screenX;
            ScreenY = screenY;
            Box = box;
        }
    }

    private readonly struct ScreenBox
    {
        public readonly double Left, Top, Right, Bottom;
        public readonly bool Visible;

        public ScreenBox(double left, double top, double right, double bottom, bool visible)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            Visible = visible;
        }
    }

    // ================================================================ 生命周期

    public OverlayWindow(World world, bool opaque = false, bool colorKey = false)
    {
        _world = world;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = !opaque && !colorKey;
        Background = colorKey
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF))
            : (opaque ? new SolidColorBrush(Color.FromRgb(0x0d, 0x0f, 0x13)) : Brushes.Transparent);
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;

        var bounds = ScreenBoundsDips();
        Width = bounds.Width;
        Height = bounds.Height;

        _surface = new Surface(this);
        Content = _surface;
    }

    /// <summary>用 DIP 表达屏幕尺寸：进程已声明 PerMonitorV2，WPF 会换算成物理像素，任何缩放下都铺满。</summary>
    private static (double Width, double Height) ScreenBoundsDips()
    {
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;
        if (width < 2 || height < 2)
        {
            width = Native.ScreenWidth;
            height = Native.ScreenHeight;
        }

        return (width, height);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeClickThroughNoActivate(hwnd);

        if (Background is SolidColorBrush keyBrush && keyBrush.Color == Color.FromRgb(0xFF, 0x00, 0xFF))
        {
            var key = ((uint)0xFF << 16) | ((uint)0x00 << 8) | 0x00;
            if (Native.EnableColorKeyTransparency(hwnd, key))
                Log.Info("色键透明已启用（背景 #FF00FF 被整片抠掉）");
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Math.Abs(dpi - _pixelsPerDip) > 0.001)
        {
            _pixelsPerDip = dpi;
            _texts.Clear();
        }

        _ready = true;
        _fpsAt = Environment.TickCount64;
        _surface.InvalidateVisual();

        Log.Info("覆盖窗已就绪: " + Width + "x" + Height + " DIP  DPI缩放="
                 + _pixelsPerDip.ToString("0.##", CultureInfo.InvariantCulture)
                 + " 渲染层级=" + (RenderCapability.Tier >> 16));

        CompositionTarget.Rendering += OnRendering;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _ready = false;
        base.OnClosed(e);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_ready) return;

        try
        {
            _surface.InvalidateVisual();
        }
        catch (Exception ex)
        {
            Log.Error("覆盖窗刷新异常", ex);
        }

        _frames++;
        var now = Environment.TickCount64;
        var elapsed = now - _fpsAt;
        if (elapsed >= 5000)
        {
            _fps = _frames * 1000.0 / elapsed;
            _frames = 0;
            _fpsAt = now;
            _world.OverlayFps = _fps;
            Log.Info("覆盖窗 " + _fps.ToString("0.#", CultureInfo.InvariantCulture) + " fps");
        }
    }

    // ================================================================ 主绘制

    private void Draw(DrawingContext dc, double w, double h)
    {
        if (w < 2 || h < 2) return;

        var opt = _world.Opt;
        if (!opt.Enabled) return;   // Ins 一键隐身

        var now = Environment.TickCount64;

        _world.GetCamera(out var camX, out var camY, out var camZ, out var yaw, out var pitch, out var fov);

        _focal = (w / 2) / Math.Tan(fov * Deg / 2);
        _cx = w / 2;
        _cy = h / 2;
        _k = Projection.BuildCamera(camX, camY, camZ, yaw, pitch);

        var color = ParseColor(opt.Color);

        // ---- 收集目标，每个目标只投影一次 ----
        var views = _world.SnapshotEntities();
        _items.Clear();

        foreach (var v in views)
        {
            var dx = v.X - camX;
            var dy = v.Y - camY;
            var dz = v.Z - camZ;
            var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // 太近的直接剔除：避免一个大框糊在脸上（距离由设置界面调，0~100m）
            if (dist < opt.MinDist) continue;
            if (dist > opt.MaxDist * 1.25) continue;

            var depth = dx * _k.Fx + dy * _k.Fy + dz * _k.Fz;
            var camXs = dx * _k.Rx + dy * _k.Ry + dz * _k.Rz;
            var camYs = dx * _k.Ux + dy * _k.Uy + dz * _k.Uz;

            var screenX = double.NaN;
            var screenY = double.NaN;
            if (depth > Projection.Near)
            {
                var inv = 1.0 / depth;
                screenX = _cx + camXs * _focal * inv;
                screenY = _cy - camYs * _focal * inv;
            }

            _items.Add(new Item(v, dist, depth, camXs, camYs, screenX, screenY,
                ComputeScreenBox(v, w, h)));
        }

        _items.Sort((a, b) => b.Dist.CompareTo(a.Dist)); // 远的先画

        // ---- 清空合批缓冲 ----
        _wire.Clear();
        _ground.Clear();
        _outline.Clear();
        _bracket.Clear();
        _tracer.Clear();
        _grid.Clear();
        _radarLines.Clear();
        _hpTrack.Clear();
        _hpFill.Clear();
        _radarDots.Clear();
        _markers.Clear();
        _arrows.Clear();

        // ---- 累积图元 ----
        if (opt.WorldBoxes) AccumulateWorldBoxes(opt);
        AccumulateHud(opt);
        if (opt.Lines) AccumulateTracers(w, h);
        if (opt.Offscreen) AccumulateArrows(w, h);
        if (opt.Grid) AccumulateGrid(camX, camZ);
        if (opt.Radar) AccumulateRadar(views, camX, camZ, yaw, w, h, opt);

        // ---- 输出（每一类通常只剩 1~2 次调用）----
        _wire.Flush(dc, band => PenFor(color, 1, AlphaOf(band)));
        _ground.Flush(dc, band => PenFor(color, 1, 0.22 * AlphaOf(band)));
        _outline.Flush(dc, band => PenFor(color, 1, 0.5 * AlphaOf(band)));
        _tracer.Flush(dc, band => PenFor(color, 1, 0.20 * AlphaOf(band)));
        _hpTrack.Flush(dc, band => BrushFor(Colors.Black, 0.45 * AlphaOf(band)));
        _hpFill.Flush(dc, band => BrushFor(color, 0.92 * AlphaOf(band)));
        _markers.Flush(dc, band => BrushFor(color, 0.9 * AlphaOf(band)));
        _arrows.Flush(dc, band => BrushFor(color, 0.42 * AlphaOf(band)));
        _radarDots.Flush(dc, band => BrushFor(color, 0.8 * AlphaOf(band)));

        _grid.Flush(dc, band => PenFor(color, 1, GridAlphas[band]));
        _radarLines.Flush(dc, band => PenFor(color, 1, 0.16));

        // 四角括号 + 廉价发光：再描一根更粗更淡的笔，成本只多一次调用
        var glow = Math.Clamp(opt.Glow, 0, 1);
        var thickness = Math.Max(1, opt.Thickness);
        _bracket.Flush(
            dc,
            band => PenFor(color, thickness, 0.95 * AlphaOf(band)),
            glow > 0.02
                ? band => PenFor(color, thickness * 3.2, 0.10 * glow * AlphaOf(band))
                : null);

        // ---- 文字与零散元素 ----
        if (opt.Names || opt.Dist) DrawLabels(dc, color, opt);
        if (opt.Radar) DrawRadarChrome(dc, camX, camZ, yaw, fov, w, h, color, opt);
        if (opt.Weapons) DrawCenterReticle(dc, w, h, color);
    }

    private static int BandOf(double alpha) => Math.Clamp((int)Math.Ceiling(alpha * Levels) - 1, 0, Levels - 1);

    private static double AlphaOf(int band) => (band + 1) / (double)Levels;

    /// <summary>3D 盒子的 8 个角投到屏幕求包围盒。只用相机前方的角，背后的角会除以接近 0 的深度炸成全屏。</summary>
    private ScreenBox? ComputeScreenBox(EntityView v, double w, double h)
    {
        var hx = v.Sx / 2;
        var hy = v.Sy / 2;
        var hz = v.Sz / 2;

        Span<double> xs = stackalloc double[8];
        Span<double> ys = stackalloc double[8];
        var n = 0;

        for (var i = 0; i < 8; i++)
        {
            var px = v.X + ((i & 1) == 0 ? -hx : hx);
            var py = v.Y + ((i & 2) == 0 ? -hy : hy);
            var pz = v.Z + ((i & 4) == 0 ? -hz : hz);

            var dx = px - _k.Px;
            var dy = py - _k.Py;
            var dz = pz - _k.Pz;
            var zc = dx * _k.Fx + dy * _k.Fy + dz * _k.Fz;
            if (zc <= Projection.Near) continue;

            var inv = 1.0 / zc;
            xs[n] = _cx + (dx * _k.Rx + dy * _k.Ry + dz * _k.Rz) * _focal * inv;
            ys[n] = _cy - (dx * _k.Ux + dy * _k.Uy + dz * _k.Uz) * _focal * inv;
            n++;
        }

        if (n < 2) return null;
        if (Projection.Depth(_k, v.X, v.Y, v.Z) < 0.4) return null;

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        for (var i = 0; i < n; i++)
        {
            if (xs[i] < minX) minX = xs[i];
            if (ys[i] < minY) minY = ys[i];
            if (xs[i] > maxX) maxX = xs[i];
            if (ys[i] > maxY) maxY = ys[i];
        }

        const double pad = 10;
        var visible = maxX > -pad && minX < w + pad && maxY > -pad && minY < h + pad;
        return new ScreenBox(minX, minY, maxX, maxY, visible);
    }

    /// <summary>投影一段 3D 线段并塞进指定缓冲。</summary>
    private void AddSeg(SegBuffer buffer, int band, double ax, double ay, double az, double bx, double by, double bz)
    {
        if (!Projection.ProjectSegment(_k, ax, ay, az, bx, by, bz, _focal, _cx, _cy,
                out var x1, out var y1, out var x2, out var y2)) return;
        buffer.Add(band, x1, y1, x2, y2);
    }

    // ---------------------------------------------------------------- 3D 线框

    private void AccumulateWorldBoxes(World.Options opt)
    {
        foreach (var item in _items)
        {
            var v = item.V;
            var hx = v.Sx / 2;
            var hy = v.Sy / 2;
            var hz = v.Sz / 2;

            var x0 = v.X - hx; var x1 = v.X + hx;
            var y0 = v.Y - hy; var y1 = v.Y + hy;
            var z0 = v.Z - hz; var z1 = v.Z + hz;

            var band = BandOf(0.55 * v.Alpha);

            AddSeg(_wire, band, x0, y0, z0, x1, y0, z0);
            AddSeg(_wire, band, x1, y0, z0, x1, y1, z0);
            AddSeg(_wire, band, x1, y1, z0, x0, y1, z0);
            AddSeg(_wire, band, x0, y1, z0, x0, y0, z0);
            AddSeg(_wire, band, x0, y0, z1, x1, y0, z1);
            AddSeg(_wire, band, x1, y0, z1, x1, y1, z1);
            AddSeg(_wire, band, x1, y1, z1, x0, y1, z1);
            AddSeg(_wire, band, x0, y1, z1, x0, y0, z1);
            AddSeg(_wire, band, x0, y0, z0, x0, y0, z1);
            AddSeg(_wire, band, x1, y0, z0, x1, y0, z1);
            AddSeg(_wire, band, x1, y1, z0, x1, y1, z1);
            AddSeg(_wire, band, x0, y1, z0, x0, y1, z1);

            // 落地参考线（默认关）：竖直投影 + 地面十字
            if (opt.GroundCross && y0 > 0.12)
            {
                var gband = BandOf(0.22 * v.Alpha);
                AddSeg(_ground, gband, v.X, y0, v.Z, v.X, 0, v.Z);
                var r = Math.Max(hx, hz) * 1.6;
                AddSeg(_ground, gband, v.X - r, 0.01, v.Z, v.X + r, 0.01, v.Z);
                AddSeg(_ground, gband, v.X, 0.01, v.Z - r, v.X, 0.01, v.Z + r);
            }
        }
    }

    // ---------------------------------------------------------------- HUD

    private void AccumulateHud(World.Options opt)
    {
        foreach (var item in _items)
        {
            if (item.Box == null || !item.Box.Value.Visible) continue;

            var v = item.V;
            var box = item.Box.Value;
            var a = v.Alpha;

            var left = Snap(box.Left);
            var top = Snap(box.Top);
            var right = Snap(box.Right);
            var bottom = Snap(box.Bottom);
            var bw = Math.Max(2, right - left);
            var bh = Math.Max(2, bottom - top);

            // 1) 细底框（水平/垂直四段，一起塞进线段缓冲）
            var oband = BandOf(0.5 * a);
            _outline.Add(oband, left, top, right, top);
            _outline.Add(oband, right, top, right, bottom);
            _outline.Add(oband, right, bottom, left, bottom);
            _outline.Add(oband, left, bottom, left, top);

            // 2) 四角括号（透视框的灵魂）
            var bband = BandOf(0.95 * a);
            var cl = Math.Max(6, Math.Min(bw, bh) * 0.28);
            _bracket.Add(bband, left, top + cl, left, top);
            _bracket.Add(bband, left, top, left + cl, top);
            _bracket.Add(bband, right - cl, top, right, top);
            _bracket.Add(bband, right, top, right, top + cl);
            _bracket.Add(bband, right, bottom - cl, right, bottom);
            _bracket.Add(bband, right, bottom, right - cl, bottom);
            _bracket.Add(bband, left + cl, bottom, left, bottom);
            _bracket.Add(bband, left, bottom, left, bottom - cl);

            // 3) 头顶小标记（默认关）：物资是菱形，其它是三角
            if (opt.HeadMarker)
            {
                var headX = (left + right) / 2;
                var markY = top - 7;
                var mband = BandOf(0.9 * a);
                if (v.Kind == 2)
                {
                    _markers.Add(mband, headX, markY - 3.2, headX + 3.2, markY, headX, markY + 3.2);
                    _markers.Add(mband, headX, markY - 3.2, headX - 3.2, markY, headX, markY + 3.2);
                }
                else
                {
                    _markers.Add(mband, headX - 3, markY + 2.6, headX + 3, markY + 2.6, headX, markY - 2.6);
                }
            }

            // 4) 假血条
            if (opt.Health)
            {
                var trackY = bottom + 5;
                _hpTrack.Add(BandOf(0.45 * a), left, trackY, bw, 3);
                var filled = Math.Max(0, Math.Min(1, v.Hp));
                _hpFill.Add(BandOf(0.92 * a), left, trackY, bw * filled, 3);
            }
        }
    }

    // ---------------------------------------------------------------- 准星连线

    private void AccumulateTracers(double w, double h)
    {
        // 只连"离准星最近"的最多 2 个。全连的话，地面目标高度相近时
        // 这些线会在视觉上连成一条横贯全屏的长线，看起来像渲染 bug。
        var limit = Math.Min(w, h) * 0.45;

        _scratch.Clear();
        foreach (var item in _items)
        {
            if (double.IsNaN(item.ScreenX)) continue;
            if (item.Dist > 75) continue;
            if (item.V.Alpha < 0.35) continue;

            var dx = item.ScreenX - _cx;
            var dy = item.ScreenY - _cy;
            var off = Math.Sqrt(dx * dx + dy * dy);
            if (off < 18 || off > limit) continue;
            _scratch.Add(item);
        }

        _scratch.Sort((a, b) => OffsetOf(a).CompareTo(OffsetOf(b)));
        var count = Math.Min(2, _scratch.Count);
        for (var i = 0; i < count; i++)
        {
            var item = _scratch[i];
            _tracer.Add(BandOf(item.V.Alpha), _cx, _cy, item.ScreenX, item.ScreenY);
        }

        double OffsetOf(Item item)
        {
            var dx = item.ScreenX - _cx;
            var dy = item.ScreenY - _cy;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    // ---------------------------------------------------------------- 屏外箭头

    private void AccumulateArrows(double w, double h)
    {
        var radius = Math.Min(w, h) * 0.40;

        _scratch.Clear();
        foreach (var item in _items)
        {
            if (item.Box == null || !item.Box.Value.Visible) _scratch.Add(item);
        }

        _scratch.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var limit = Math.Min(6, _scratch.Count);

        for (var i = 0; i < limit; i++)
        {
            var item = _scratch[i];
            var dist = Math.Max(item.Dist, 1e-3);
            var sx = item.CamX / dist;
            var sy = -item.CamY / dist;
            if (item.Depth <= Projection.Near)
            {
                sx = -sx;
                sy = -sy;
            }

            if (Math.Abs(sx) < 1e-4 && Math.Abs(sy) < 1e-4) sy = -1;

            var ang = Math.Atan2(sy, sx);
            var ax = _cx + Math.Cos(ang) * radius;
            var ay = _cy + Math.Sin(ang) * radius;
            var band = BandOf(item.V.Alpha);

            _arrows.Add(band,
                RotateX(6, 0, ang, ax, ay), RotateY(6, 0, ang, ax, ay),
                RotateX(-4.5, -4, ang, ax, ay), RotateY(-4.5, -4, ang, ax, ay),
                RotateX(-4.5, 4, ang, ax, ay), RotateY(-4.5, 4, ang, ax, ay));
        }
    }

    private static double RotateX(double x, double y, double ang, double ax, double ay) =>
        ax + x * Math.Cos(ang) - y * Math.Sin(ang);

    private static double RotateY(double x, double y, double ang, double ax, double ay) =>
        ay + x * Math.Sin(ang) + y * Math.Cos(ang);

    // ---------------------------------------------------------------- 地面网格

    private void AccumulateGrid(double camX, double camZ)
    {
        const double spacing = 6.0;
        const double radius = 42.0;
        const double chunk = 14.0;

        var baseX = Math.Round(camX / spacing) * spacing;
        var baseZ = Math.Round(camZ / spacing) * spacing;

        for (var x = baseX - radius; x <= baseX + radius; x += spacing)
        {
            for (var z = baseZ - radius; z < baseZ + radius; z += chunk) AddGridSegment(x, 0, z, x, 0, z + chunk, camX, camZ);
        }

        for (var z = baseZ - radius; z <= baseZ + radius; z += spacing)
        {
            for (var x = baseX - radius; x < baseX + radius; x += chunk) AddGridSegment(x, 0, z, x + chunk, 0, z, camX, camZ);
        }
    }

    private void AddGridSegment(double ax, double ay, double az, double bx, double by, double bz, double camX, double camZ)
    {
        if (!Projection.ProjectSegment(_k, ax, ay, az, bx, by, bz, _focal, _cx, _cy,
                out var x1, out var y1, out var x2, out var y2)) return;

        var mx = (ax + bx) * 0.5 - camX;
        var mz = (az + bz) * 0.5 - camZ;
        var d = Math.Sqrt(mx * mx + mz * mz);

        _grid.Add(d < 18 ? 0 : (d < 31 ? 1 : 2), x1, y1, x2, y2);
    }

    // ---------------------------------------------------------------- 文字

    private void DrawLabels(DrawingContext dc, Color color, World.Options opt)
    {
        foreach (var item in _items)
        {
            if (item.Box == null || !item.Box.Value.Visible) continue;

            var box = item.Box.Value;
            var a = item.V.Alpha;
            var headX = (Snap(box.Left) + Snap(box.Right)) / 2;
            var ty = Snap(box.Top) - 7 - 7;

            if (opt.Names)
            {
                DrawTextCentered(dc, item.V.Name, 12, color, 0.97 * a, headX, ty);
                ty -= 14;
            }

            if (opt.Dist)
            {
                DrawTextCentered(dc, item.Dist.ToString("0", CultureInfo.InvariantCulture) + "m",
                    11, color, 0.97 * a, headX, ty);
            }
        }
    }

    private static void Line(StreamGeometryContext ctx, double x1, double y1, double x2, double y2)
    {
        ctx.BeginFigure(new Point(x1, y1), false, false);
        ctx.LineTo(new Point(x2, y2), true, false);
    }

    /// <summary>
    /// 文字走 FormattedText + DrawText 快路径（内部有字形缓存）。
    /// 用四向偏移的黑色影子代替描边：效果接近，成本低一个数量级。
    /// </summary>
    private void DrawTextCentered(DrawingContext dc, string text, double size, Color color, double alpha, double centerX, double baselineY)
    {
        var body = GetFormattedText(text, size, color, alpha);
        if (body == null) return;

        var shadow = GetFormattedText(text, size, Colors.Black, alpha * 0.8);
        var x = centerX - body.Width / 2;
        var y = baselineY - body.Baseline;

        if (shadow != null)
        {
            dc.DrawText(shadow, new Point(x - 1, y));
            dc.DrawText(shadow, new Point(x + 1, y));
            dc.DrawText(shadow, new Point(x, y - 1));
            dc.DrawText(shadow, new Point(x, y + 1));
        }

        dc.DrawText(body, new Point(x, y));
    }

    private FormattedText? GetFormattedText(string text, double size, Color color, double alpha)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var band = BandOf(alpha);
        var key = size.ToString("0.#", CultureInfo.InvariantCulture)
                  + "|" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2")
                  + "|" + band + "|" + text;

        if (_texts.TryGetValue(key, out var cached)) return cached;
        if (_texts.Count > 400) _texts.Clear();

        try
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(AlphaOf(band) * 255), color.R, color.G, color.B));
            brush.Freeze();
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, size, brush, _pixelsPerDip);
            _texts[key] = ft;
            return ft;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 雷达

    private void AccumulateRadar(EntityView[] views, double camX, double camZ, double yaw, double w, double h, World.Options opt)
    {
        const double r = 62;
        var cx = w - r - 22;
        var cy = h - r - 22;
        var range = Math.Max(20, Math.Min(opt.MaxDist, 130));
        var scale = r / range;
        var sin = Math.Sin(yaw * Deg);
        var cos = Math.Cos(yaw * Deg);

        foreach (var v in views)
        {
            var dx = v.X - camX;
            var dz = v.Z - camZ;
            var u = dx * cos - dz * sin;   // 右向分量
            var fwd = dx * sin + dz * cos; // 前向分量
            var px = cx + u * scale;
            var py = cy - fwd * scale;
            if (Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) > r - 3) continue;

            var dot = 4.4;
            _radarDots.Add(BandOf(v.Alpha), px - dot / 2, py - dot / 2, dot, dot);
        }
    }

    private void DrawRadarChrome(DrawingContext dc, double camX, double camZ, double yaw, double fov,
        double w, double h, Color color, World.Options opt)
    {
        const double r = 62;
        var cx = w - r - 22;
        var cy = h - r - 22;

        dc.DrawEllipse(BrushFor(Colors.Black, 0.20), PenFor(color, 1, 0.30), new Point(cx, cy), r, r);

        _radarLines.Add(0, cx - r, cy, cx + r, cy);
        _radarLines.Add(0, cx, cy - r, cx, cy + r);

        // 视野扇形：折线近似圆弧，省掉 ArcSegment 的复杂度
        var half = fov * Deg / 2;
        var wedge = new StreamGeometry();
        using (var ctx = wedge.Open())
        {
            ctx.BeginFigure(new Point(cx, cy), true, true);
            const int steps = 14;
            for (var i = 0; i <= steps; i++)
            {
                var ang = -Math.PI / 2 - half + (2 * half) * i / steps;
                ctx.LineTo(new Point(cx + Math.Cos(ang) * r, cy + Math.Sin(ang) * r), true, false);
            }
        }

        wedge.Freeze();
        dc.DrawGeometry(BrushFor(color, 0.07), null, wedge);
    }

    // ---------------------------------------------------------------- 准星

    private void DrawCenterReticle(DrawingContext dc, double w, double h, Color color)
    {
        var cx = w / 2;
        var cy = h / 2;
        var r = Math.Min(w, h) * 0.18;
        dc.DrawEllipse(null, PenFor(color, 1, 0.10), new Point(cx, cy), r, r);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            Line(ctx, Snap(cx - 7), Snap(cy), Snap(cx - 2), Snap(cy));
            Line(ctx, Snap(cx + 2), Snap(cy), Snap(cx + 7), Snap(cy));
            Line(ctx, Snap(cx), Snap(cy - 7), Snap(cx), Snap(cy - 2));
            Line(ctx, Snap(cx), Snap(cy + 2), Snap(cx), Snap(cy + 7));
        }

        geometry.Freeze();
        dc.DrawGeometry(null, PenFor(color, 1, 0.55), geometry);
    }

    // ================================================================ 缓存与工具

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private static Color ParseColor(string hex)
    {
        try
        {
            var h = (hex ?? "#ffffff").TrimStart('#');
            if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            if (h.Length >= 6)
            {
                return Color.FromRgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16));
            }
        }
        catch
        {
            // 颜色值写错就用白色
        }

        return Colors.White;
    }

    private Pen PenFor(Color c, double thickness, double alpha)
    {
        var band = BandOf(alpha);
        var key = (c.R << 16) | (c.G << 8) | c.B;
        key = unchecked(key * 31 + band);
        key = unchecked(key * 31 + (int)Math.Round(thickness * 4));

        if (_pens.TryGetValue(key, out var pen)) return pen;
        if (_pens.Count > 400) _pens.Clear();

        var brush = new SolidColorBrush(Color.FromArgb((byte)(AlphaOf(band) * 255), c.R, c.G, c.B));
        brush.Freeze();
        pen = new Pen(brush, thickness);
        pen.Freeze();
        _pens[key] = pen;
        return pen;
    }

    private SolidColorBrush BrushFor(Color c, double alpha)
    {
        var band = BandOf(alpha);
        var key = (c.R << 16) | (c.G << 8) | c.B;
        key = unchecked(key * 31 + band);

        if (_brushes.TryGetValue(key, out var brush)) return brush;
        if (_brushes.Count > 400) _brushes.Clear();

        brush = new SolidColorBrush(Color.FromArgb((byte)(AlphaOf(band) * 255), c.R, c.G, c.B));
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }
}
