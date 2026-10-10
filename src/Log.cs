using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace FakeEsp;

/// <summary>极简日志：控制台(可选) + 内存环形缓冲(供 /log 查看) + 文件。</summary>
internal static class Log
{
    private const int RingSize = 400;
    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private static StreamWriter? _file;
    private static bool _console;

    public static void AttachConsole()
    {
        _console = true;
    }

    public static void AttachFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
        catch
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + " :: " + ex.GetType().Name + ": " + ex.Message);

    /// <summary>致命错误：即使文件日志没挂上也要让外面看得见。</summary>
    public static void Fatal(string message, Exception? ex)
    {
        var detail = ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
        Write("FATAL", detail);
        try
        {
            Console.Error.WriteLine("FATAL " + detail);
            Console.Error.Flush();
        }
        catch
        {
            // 没有 stderr 就算了
        }
    }

    private static void Write(string level, string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message;
        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingSize) Ring.Dequeue();

            if (_console)
            {
                try { Console.WriteLine(line); } catch { }
            }

            try { _file?.WriteLine(line); } catch { }
        }
    }

    public static string Dump()
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            foreach (var line in Ring) sb.Append(line).Append('\n');
            return sb.ToString();
        }
    }
}

/// <summary>把可空/异常包住的小工具，避免到处 try/catch 噪音。</summary>
internal static class Safe
{
    public static void Run(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(what, ex);
        }
    }

    public static void Run(Action action, string what, int repeatMs)
    {
        var failures = 0;
        while (true)
        {
            try
            {
                action();
                failures = 0;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error(what + " (失败 " + failures + " 次)", ex);
            }

            if (repeatMs <= 0) return;
            Thread.Sleep(failures == 0 ? repeatMs : Math.Min(repeatMs * failures, 2000));
        }
    }
}
