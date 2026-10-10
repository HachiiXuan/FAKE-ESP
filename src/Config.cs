using System;
using System.Globalization;
using System.IO;

namespace FakeEsp;

/// <summary>命令行 + 运行时参数。overlay 是"单屏边玩边看"那个透明穿透窗。</summary>
internal sealed class Config
{
    public int Port = 17777;
    public bool Overlay;
    public bool OverlayOpaque;
    public bool OverlayKey;
    public bool Settings;
    public bool NoUi;
    public bool Admin;
    public bool ElevatedAttempt;
    public bool Console;
    public bool NoInput;
    public int RunSeconds;
    public string? WebRootOverride;
    public string? LogFile;

    public static Config Parse(string[] args)
    {
        var c = new Config();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim();
            switch (a.ToLowerInvariant())
            {
                case "--overlay":
                case "-o":
                    c.Overlay = true;
                    break;
                case "--overlay-opaque":
                    // 诊断用：不透明窗口，用来测量硬件加速路径的上限
                    c.Overlay = true;
                    c.OverlayOpaque = true;
                    break;
                case "--overlay-key":
                    c.Overlay = true;
                    c.OverlayKey = true;
                    break;
                case "--settings":
                case "-s":
                    c.Settings = true;
                    break;
                case "--no-ui":
                    // 无界面模式：只跑服务（给脚本或纯 OBS 场景用）
                    c.NoUi = true;
                    break;
                case "--admin":
                    // 启动就提权（会弹 UAC）。默认不这么做，见 Program 里的说明。
                    c.Admin = true;
                    break;
                case "--elevated-attempt":
                    // 内部标记：这是提权之后的那一次启动，不要再尝试提权（防死循环）
                    c.ElevatedAttempt = true;
                    break;
                case "--console":
                case "-c":
                    c.Console = true;
                    break;
                case "--no-input":
                    c.NoInput = true;
                    break;
                case "--run-seconds":
                case "--selftest":
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                        c.RunSeconds = secs;
                    break;
                case "--port":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
                        c.Port = p;
                    break;
                case "--web":
                    if (i + 1 < args.Length) c.WebRootOverride = args[++i];
                    break;
                case "--log":
                    if (i + 1 < args.Length) c.LogFile = args[++i];
                    break;
            }
        }

        return c;
    }

    /// <summary>从 exe 目录往上找 web/ 目录，这样编辑 web/*.js 后刷新即可，不需要重新构建。</summary>
    public string ResolveWebRoot()
    {
        if (!string.IsNullOrWhiteSpace(WebRootOverride))
        {
            var full = Path.GetFullPath(WebRootOverride!);
            if (Directory.Exists(full)) return full;
            Log.Warn("--web 指定的目录不存在，回退自动查找: " + full);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "web");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }

        // 极端回退：就在 exe 旁边
        return Path.Combine(AppContext.BaseDirectory, "web");
    }
}
