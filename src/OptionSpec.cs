using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeEsp;

/// <summary>一个可调项的界面描述。前端（网页面板 / WPF 设置窗）据此自动生成控件。</summary>
internal sealed class ControlSpec
{
    public string Group { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>bool | range | choice</summary>
    public string Type { get; set; } = "bool";

    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }

    /// <summary>choice 类型的候选项："显示文字|实际值" 或直接 "值"</summary>
    public string[]? Options { get; set; }

    public string? Hint { get; set; }
}

/// <summary>
/// 全部可调项的唯一数据源。
///
/// 加一个新效果的完整流程：
///   1) World.Options 里加一个带默认值的字段
///   2) Options.ToJson() 加一行
///   3) World.ApplySettings() 加一行
///   4) 这里加一个 ControlSpec
/// 网页调参面板（/tune）和覆盖窗的设置窗都会自动多出对应的控件，不需要改前端。
/// </summary>
internal static class OptionSpec
{
    public static List<ControlSpec> All() => new()
    {
        // ---------------- 目标 ----------------
        new ControlSpec { Group = "目标", Key = "count", Label = "框数量", Type = "range", Min = 5, Max = 20, Step = 1 },
        new ControlSpec
        {
            Group = "目标", Key = "minDist", Label = "近距离剔除 (m)", Type = "range",
            Min = 0, Max = 100, Step = 1, Hint = "比这个距离更近的目标直接不画，避免大框糊脸"
        },
        new ControlSpec { Group = "目标", Key = "maxDist", Label = "最远距离 (m)", Type = "range", Min = 20, Max = 200, Step = 5 },
        new ControlSpec { Group = "目标", Key = "jitter", Label = "高频抖动", Type = "bool", Hint = "模拟真实 ESP 每帧数据都在跳" },
        new ControlSpec
        {
            Group = "目标", Key = "breathing", Label = "相机呼吸摆动", Type = "bool",
            Hint = "默认关。开启后相机会自己轻微摆动，但如果鼠标输入没通，它会掩盖这个问题"
        },

        // ---------------- 画面元素 ----------------
        new ControlSpec { Group = "画面元素", Key = "worldBoxes", Label = "3D 线框盒子", Type = "bool", Hint = "转视角能看出体积感" },
        new ControlSpec { Group = "画面元素", Key = "dist", Label = "距离数字", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "health", Label = "假血条", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "lines", Label = "准星连线", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "names", Label = "目标假名字", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "headMarker", Label = "头顶小标记", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "offscreen", Label = "屏外方向箭头", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "groundCross", Label = "落地参考线", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "grid", Label = "地面网格", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "radar", Label = "右下角雷达", Type = "bool" },
        new ControlSpec { Group = "画面元素", Key = "weapons", Label = "中心准星圈", Type = "bool" },

        // ---------------- 观感 ----------------
        new ControlSpec { Group = "观感", Key = "thickness", Label = "框线宽", Type = "range", Min = 1, Max = 6, Step = 1 },
        new ControlSpec { Group = "观感", Key = "glow", Label = "发光强度", Type = "range", Min = 0, Max = 1, Step = 0.05 },
        new ControlSpec
        {
            Group = "观感", Key = "color", Label = "框颜色", Type = "choice",
            Options = new[] { "白色|#ffffff", "浅绿|#7dff9b", "红色|#ff5a5a", "黄色|#ffe066", "青色|#66e0ff", "品红|#ff66ff" }
        },
        new ControlSpec
        {
            Group = "观感", Key = "bg", Label = "背景模式", Type = "choice",
            Options = new[] { "透明 (OBS 用这个)|transparent", "绿幕|green", "深色|dark", "品红幕|magenta" },
            Hint = "OBS 浏览器源保持「透明」；只有走绿幕抠像的老流程才需要改"
        },

        // ---------------- 手感对齐 ----------------
        new ControlSpec
        {
            Group = "手感对齐", Key = "fov", Label = "视场角 FOV", Type = "range",
            Min = 40, Max = 140, Step = 1, Hint = "对着游戏里的 FOV 设置调，调到转头时框跟着走的比例一致"
        },
        new ControlSpec
        {
            Group = "手感对齐", Key = "sens", Label = "鼠标灵敏度", Type = "range",
            Min = 0.002, Max = 0.2, Step = 0.001, Hint = "每 1 个鼠标原始计数转多少度"
        },
        new ControlSpec { Group = "手感对齐", Key = "moveSpeed", Label = "WASD 移动速度", Type = "range", Min = 0.5, Max = 20, Step = 0.5 },
        new ControlSpec { Group = "手感对齐", Key = "enabled", Label = "总开关", Type = "bool", Hint = "等同于一键隐身" },
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 必须显式指定 camelCase：默认是 PascalCase，前端读 item.label 会全是 undefined，
        // 结果是控件一个都建不出来（而且不报错，只是静默空白）
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson() => JsonSerializer.Serialize(All(), JsonOptions);
}
