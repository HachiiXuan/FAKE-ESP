using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FakeEsp;

/// <summary>
/// 主窗口 = 实时状态 + 全部设置。
///
/// 上半部分是**输入自检**：一眼看出鼠标到底有没有被读到、当前用的是哪条链路。
/// 这一块是刻意做在最显眼位置的 —— 之前整个项目最大的坑就是输入没通却看不出来。
/// 下半部分的设置控件由 OptionSpec.All() 自动生成，和 OBS 里的 /tune 网页面板同源。
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Color Bg = Color.FromRgb(0x1e, 0x21, 0x27);
    private static readonly Color Fg = Color.FromRgb(0xd8, 0xdd, 0xe8);
    private static readonly Color Dim = Color.FromRgb(0x8b, 0x93, 0xa5);
    private static readonly Color Accent = Color.FromRgb(0x4a, 0x9e, 0xff);
    private static readonly Color Panel = Color.FromRgb(0x26, 0x2a, 0x33);
    private static readonly Color Ok = Color.FromRgb(0x4a, 0xde, 0x80);
    private static readonly Color Bad = Color.FromRgb(0xf8, 0x71, 0x71);
    private static readonly Color Warn = Color.FromRgb(0xff, 0xc0, 0x40);

    private readonly World _world;
    private readonly InputState _input;
    private readonly HttpServer _server;
    private readonly string _previewUrl;

    private readonly Dictionary<string, Action> _refreshers = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _controlTimer;

    // 状态区：按需求只留一个指示灯 + 一行状态文字，具体数据全部去掉
    private readonly System.Windows.Shapes.Ellipse _inputDot = new() { Width = 12, Height = 12 };
    private readonly TextBlock _inputTitle = new();
    private Button? _restartElevatedButton;
    private readonly TextBlock _adminHint = new();
    private Button? _overlayButton;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private bool _everHadInput;
    private DateTime _flashUntil = DateTime.MinValue;
    private string _flashText = string.Empty;

    /// <summary>是否已提权（由 Program 设置）。键盘热键在游戏前台时需要它。</summary>
    public bool IsElevated { get; set; }

    /// <summary>点「以管理员身份重启」。</summary>
    public event Action? RestartElevatedClicked;

    /// <summary>点窗口右上角 X 时是否真的退出（托盘菜单退出时置 true）。</summary>
    public bool ExitRequested { get; set; }

    public event Action? ExitClicked;

    /// <summary>点「覆盖窗 开/关」按钮。</summary>
    public event Action? OverlayToggleClicked;

    /// <summary>由外层提供覆盖窗当前是否可见，用来更新按钮文字。</summary>
    public Func<bool>? OverlayStateProvider;

    public MainWindow(World world, InputState input, HttpServer server, string previewUrl)
    {
        _world = world;
        _input = input;
        _server = server;
        _previewUrl = previewUrl;

        Title = "FAKE ESP";
        Width = 440;
        Height = 920;
        Background = new SolidColorBrush(Bg);
        Foreground = new SolidColorBrush(Fg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 标题栏 / Alt-Tab 的图标。exe 自身的图标由 csproj 的 ApplicationIcon 决定，
        // 但 WPF 窗口**不会自动继承**它 —— 少了这一行，标题栏左上角仍是默认图标。
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico"));
        }
        catch (Exception ex)
        {
            Log.Warn("设置窗口图标失败: " + ex.Message);
        }

        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 18) };

        // 状态卡片放最上面，但**每一行都钉死高度**：数字一直在跳也不会挤动下面的设置。
        // （试过挪到最下方，但设置项很多、必然要滚动，那样输入自检就被挤出视野了。）
        BuildStatus(panel);
        BuildControls(panel);
        BuildActions(panel);

        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _controlTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _controlTimer.Tick += (_, _) => RefreshControls();
        _controlTimer.Start();

        Closing += (_, _) =>
        {
            // 按需求：关窗口 = 直接退出，绝不留在后台。
            if (ExitRequested) return;
            ExitRequested = true;
            ExitClicked?.Invoke();
        };

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _controlTimer.Stop();
        };

        RefreshStatus();
    }

    // ================================================================ 状态区

    private void BuildStatus(StackPanel panel)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Panel),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var inner = new StackPanel();

        // 用 Grid 而不是横向 StackPanel：横向 StackPanel 会给子元素"无限宽度"，
        // TextBlock 因此永远不换行，长句子会被 Border 直接裁掉（之前就是这样被截断的）。
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _inputDot.Fill = new SolidColorBrush(Warn);
        _inputDot.VerticalAlignment = VerticalAlignment.Center;
        _inputDot.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_inputDot, 0);
        header.Children.Add(_inputDot);

        _inputTitle.FontSize = 13.5;
        _inputTitle.FontWeight = FontWeights.SemiBold;
        _inputTitle.VerticalAlignment = VerticalAlignment.Center;
        _inputTitle.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_inputTitle, 1);
        header.Children.Add(_inputTitle);
        inner.Children.Add(header);

        // 「以管理员身份重启」按钮：不需要时用 Hidden（占位）而不是 Collapsed，
        // 这样卡片高度恒定，状态切换不会把下面的设置顶来顶去。
        _restartElevatedButton = new Button
        {
            Content = "以管理员身份重启",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(20, 8, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x4a, 0x72)),
            Foreground = new SolidColorBrush(Fg),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3a, 0x6e, 0xa5)),
            Visibility = Visibility.Hidden
        };
        _restartElevatedButton.Click += (_, _) => RestartElevatedClicked?.Invoke();
        inner.Children.Add(_restartElevatedButton);

        // 说明文字放在按钮**下面**，灰字，不加括号
        _adminHint.Text = "游戏里的键盘操作需要它";
        _adminHint.FontSize = 11;
        _adminHint.Foreground = new SolidColorBrush(Dim);
        _adminHint.Margin = new Thickness(20, 4, 0, 0);
        _adminHint.Visibility = Visibility.Hidden;
        inner.Children.Add(_adminHint);

        card.Child = inner;
        panel.Children.Add(card);
    }

    private void RefreshStatus()
    {
        var anyInput =
            _input.TotalNonZero(MouseSource.RawInput)
            + _input.TotalNonZero(MouseSource.Hid)
            + _input.TotalNonZero(MouseSource.Hook)
            + _input.TotalNonZero(MouseSource.CursorPoll) > 0;

        if (anyInput) _everHadInput = true;

        var secondsSinceStart = (DateTime.UtcNow - _startedAt).TotalSeconds;
        // 绿灯要求"检测到输入"且"已提权"两者都满足 —— 只满足输入的话键盘在游戏前台仍然无效
        var needsAdmin = _everHadInput && !IsElevated;

        if (_everHadInput && IsElevated)
        {
            _inputDot.Fill = new SolidColorBrush(Ok);
            _inputTitle.Text = "运行正常";
        }
        else if (needsAdmin)
        {
            _inputDot.Fill = new SolidColorBrush(Warn);
            _inputTitle.Text = "输入正常，但没有管理员权限：游戏里的键盘操作会失效";
        }
        else if (secondsSinceStart < 8)
        {
            _inputDot.Fill = new SolidColorBrush(Warn);
            _inputTitle.Text = "正在检测输入……移动一下鼠标";
        }
        else
        {
            _inputDot.Fill = new SolidColorBrush(Bad);
            _inputTitle.Text = "没有检测到鼠标输入";
        }

        if (_restartElevatedButton != null)
        {
            var show = needsAdmin ? Visibility.Visible : Visibility.Hidden;
            _restartElevatedButton.Visibility = show;
            _adminHint.Visibility = show;
        }

        if (_overlayButton != null)
        {
            _overlayButton.Content = (OverlayStateProvider?.Invoke() ?? false) ? "关闭覆盖窗" : "开启覆盖窗";
        }

        // 临时提示（比如"已复制"）优先占用这一行，两秒后自动恢复
        if (DateTime.UtcNow < _flashUntil)
        {
            _inputDot.Fill = new SolidColorBrush(Ok);
            _inputTitle.Text = _flashText;
        }
    }

    private void Flash(string text, double seconds = 2.0)
    {
        _flashText = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        RefreshStatus();
    }

    // ================================================================ 设置区

    private void BuildControls(StackPanel panel)
    {
        string? lastGroup = null;

        foreach (var spec in OptionSpec.All())
        {
            if (spec.Group != lastGroup)
            {
                lastGroup = spec.Group;
                panel.Children.Add(new TextBlock
                {
                    Text = spec.Group,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Dim),
                    Margin = new Thickness(0, 12, 0, 6)
                });
            }

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            var label = new TextBlock
            {
                Text = spec.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (!string.IsNullOrEmpty(spec.Hint)) label.ToolTip = spec.Hint;
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            FrameworkElement control = spec.Type switch
            {
                "range" => BuildSlider(spec),
                "choice" => BuildChoice(spec),
                _ => BuildToggle(spec)
            };
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            panel.Children.Add(row);

            if (!string.IsNullOrEmpty(spec.Hint))
            {
                var hint = new TextBlock
                {
                    Text = spec.Hint,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6f, 0x77, 0x88)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, -1, 0, 3)
                };
                panel.Children.Add(hint);
            }
        }
    }

    private FrameworkElement BuildToggle(ControlSpec spec)
    {
        var check = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = ReadBool(spec.Key)
        };
        check.Checked += (_, _) => Apply(spec.Key, "1");
        check.Unchecked += (_, _) => Apply(spec.Key, "0");

        _refreshers[spec.Key] = () =>
        {
            var value = ReadBool(spec.Key);
            if (check.IsChecked != value) check.IsChecked = value;
        };

        return check;
    }

    private FrameworkElement BuildSlider(ControlSpec spec)
    {
        var min = spec.Min ?? 0;
        var max = spec.Max ?? 1;
        var step = spec.Step ?? 1;
        var decimals = (step.ToString(CultureInfo.InvariantCulture).Split('.') is { Length: 2 } parts)
            ? parts[1].Length
            : 0;
        var format = "0." + new string('#', Math.Max(decimals, 1));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        var box = new TextBlock
        {
            Foreground = new SolidColorBrush(Accent),
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 值为 0 时可以显示成"关闭"之类的文字（例如"视角回正间隔 = 0"其实是关闭）
        string Fmt(double v) =>
            (spec.ZeroLabel != null && Math.Abs(v) < 1e-9)
                ? spec.ZeroLabel
                : v.ToString(format, CultureInfo.InvariantCulture);

        var current = ReadDouble(spec.Key, min);
        slider.Value = Math.Clamp(current, min, max);
        box.Text = Fmt(current);

        slider.ValueChanged += (_, e) =>
        {
            box.Text = Fmt(e.NewValue);
            // 拖动过程中也不断提交，方便一边看一边调
            Apply(spec.Key, e.NewValue.ToString(CultureInfo.InvariantCulture));
        };

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(slider);
        grid.Children.Add(box);

        _refreshers[spec.Key] = () =>
        {
            if (slider.IsMouseCaptureWithin) return;
            var value = ReadDouble(spec.Key, min);
            if (Math.Abs(slider.Value - value) < 1e-9) return;
            slider.Value = Math.Clamp(value, min, max);
            box.Text = Fmt(value);
        };

        return grid;
    }

    private FrameworkElement BuildChoice(ControlSpec spec)
    {
        var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        var current = ReadString(spec.Key);

        foreach (var raw in spec.Options ?? Array.Empty<string>())
        {
            var parts = raw.Split('|');
            var value = parts.Length > 1 ? parts[1] : parts[0];
            var item = new ComboBoxItem { Content = parts[0], Tag = value };
            combo.Items.Add(item);
            if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) combo.SelectedItem = item;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item) Apply(spec.Key, (string)item.Tag);
        };

        _refreshers[spec.Key] = () =>
        {
            var value = ReadString(spec.Key);
            foreach (ComboBoxItem item in combo.Items)
            {
                if (!string.Equals((string)item.Tag, value, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ReferenceEquals(combo.SelectedItem, item)) combo.SelectedItem = item;
                return;
            }
        };

        return combo;
    }

    // ================================================================ 操作区

    private void BuildActions(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "快捷操作",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Dim),
            Margin = new Thickness(0, 16, 0, 6)
        });

        var grid = new Grid();
        for (var i = 0; i < 2; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition());

        void Add(string text, int col, int row, Action action, Color? color = null)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(3),
                Padding = new Thickness(6, 5, 6, 5),
                Background = new SolidColorBrush(color ?? Panel),
                Foreground = new SolidColorBrush(Fg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x36, 0x3b, 0x47))
            };
            button.Click += (_, _) => action();
            Grid.SetColumn(button, col);
            Grid.SetRow(button, row);
            grid.Children.Add(button);
        }

        Add("默认设置", 0, 0, () => Apply("preset", "default"));
        Add("复制诊断信息", 1, 0, CopyDiagnostics);
        Add("复制 OBS 地址", 0, 1, CopyObsUrl);

        _overlayButton = new Button
        {
            Content = "开启覆盖窗",
            Margin = new Thickness(3),
            Padding = new Thickness(6, 5, 6, 5),
            Background = new SolidColorBrush(Panel),
            Foreground = new SolidColorBrush(Fg),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x36, 0x3b, 0x47))
        };
        _overlayButton.Click += (_, _) => OverlayToggleClicked?.Invoke();
        Grid.SetColumn(_overlayButton, 1);
        Grid.SetRow(_overlayButton, 1);
        grid.Children.Add(_overlayButton);

        var exit = new Button
        {
            Content = "退出 FAKE ESP",
            Margin = new Thickness(3),
            Padding = new Thickness(6, 5, 6, 5),
            Background = new SolidColorBrush(Color.FromRgb(0x5a, 0x2b, 0x2b)),
            Foreground = new SolidColorBrush(Fg),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8b, 0x3a, 0x3a))
        };
        exit.Click += (_, _) => ExitClicked?.Invoke();
        Grid.SetColumn(exit, 0);
        Grid.SetRow(exit, 2);
        Grid.SetColumnSpan(exit, 2);
        grid.Children.Add(exit);

        panel.Children.Add(grid);

        panel.Children.Add(new TextBlock
        {
            Text = "热键：Ins 一键隐身 · Home 视角回中\n" +
                   "关闭这个窗口就是退出程序，不会留在后台。",
            FontSize = 11,
            Foreground = new SolidColorBrush(Dim),
            Margin = new Thickness(0, 12, 0, 0),
            LineHeight = 17
        });
    }

    private void OpenPreview()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_previewUrl) { UseShellExecute = true });
            Log.Info("已打开预览窗口: " + _previewUrl);
        }
        catch (Exception ex)
        {
            Log.Error("打开预览失败", ex);
        }
    }

    private void CopyObsUrl()
    {
        try
        {
            var url = _server.Url + "?mode=obs";
            System.Windows.Clipboard.SetText(url);
            Log.Info("已复制 OBS 浏览器源地址: " + url);
        }
        catch (Exception ex)
        {
            Log.Error("复制失败", ex);
        }
    }

    /// <summary>
    /// 把输入诊断拼成一段可以直接粘贴的纯文本。
    /// 这一块是刻意做的：输入到底通没通，光看界面描述容易说不清，
    /// 一键复制一段数字，对方一眼就能判断。
    /// </summary>
    private string BuildDiagnostics()
    {
        var lines = new List<string>
        {
            "===== FAKE ESP 输入诊断 =====",
            "当前来源: " + InputState.Name(_input.ActiveSource) + "    每秒位移: " + _input.PerSecondSum(),
            "",
            Describe(MouseSource.RawInput, _input.RawMouseRegistered ? "已注册" : "未注册"),
            Describe(MouseSource.Hid, _input.HidOpened ? "已打开" : "未打开"),
            Describe(MouseSource.Hook, _input.HookInstalled ? "已安装" : "未安装"),
            Describe(MouseSource.CursorPoll, "总是可用"),
            "",
            "HID 细节: " + _input.HidSummary,
            "",
            "输入窗口收到的消息数: " + _input.WinMessages,
            "其中 WM_INPUT 条数:   " + _input.WmInputMessages,
            "消息循环心跳(每秒+1): " + _input.LoopHeartbeat,
            "钩子回调次数:         " + _input.HookCalls,
            "",
            "目标数: " + _world.TargetCount + "    模拟频率: " + _world.SimHz.ToString("0", CultureInfo.InvariantCulture) + "Hz",
            "OBS 浏览器源: " + _server.Url + "?mode=obs",
            "=============================="
        };

        return string.Join(Environment.NewLine, lines);

        string Describe(MouseSource source, string state) =>
            InputState.Name(source) + ": " + state
            + "  每秒=" + _input.PerSecond(source)
            + "  累计位移=" + _input.TotalNonZero(source)
            + "  事件数=" + _input.TotalEvents(source);
    }

    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildDiagnostics());
            Flash("诊断信息已复制到剪贴板");
            Log.Info("已复制诊断信息");
        }
        catch (Exception ex)
        {
            Log.Error("复制诊断信息失败", ex);
        }
    }

    // ================================================================ 读写

    private void Apply(string key, string value)
    {
        try
        {
            _world.ApplySettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value });
        }
        catch (Exception ex)
        {
            Log.Error("应用参数失败 " + key + "=" + value, ex);
        }
    }

    private void RefreshControls()
    {
        foreach (var refresh in _refreshers.Values)
        {
            try
            {
                refresh();
            }
            catch
            {
                // 单个控件刷新失败不影响其它
            }
        }
    }

    private bool ReadBool(string key) =>
        _world.TryGetOption(key, out var raw) && raw is "true" or "True" or "1";

    private double ReadDouble(string key, double fallback) =>
        _world.TryGetOption(key, out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private string ReadString(string key) => _world.TryGetOption(key, out var raw) ? raw : string.Empty;
}
