using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FakeEsp;

/// <summary>
/// 自己用 TcpListener 实现的极小 HTTP/1.1 服务。
///
/// 为什么不用 HttpListener：它要走 http.sys，需要 URL ACL，很多受限环境下会直接
/// 报"句柄无效"，而且必须管理员。裸 socket 没有任何这些依赖，只监听 127.0.0.1，
/// 行为完全可预测。
///
/// 同一份文件同时喂给两个消费者：
///   · OBS 浏览器源（透明合成）
///   · OBS 自定义浏览器面板里的 /tune 调参页
/// 页面和接口同源，所以完全不需要处理 CORS。
/// </summary>
internal sealed class HttpServer
{
    private const int MaxConnections = 24;
    private const int IdleTimeoutMs = 20000;

    private readonly Config _config;
    private readonly string _webRoot;
    private TcpListener? _listener;
    private volatile bool _running;
    private int _connections;
    private long _requests;

    public HttpServer(Config config, string webRoot)
    {
        _config = config;
        _webRoot = webRoot;
    }

    /// <summary>/state 的数据来源，由 Program 注入。</summary>
    public Func<string>? StateProvider { get; set; }

    /// <summary>/set?k=v 的处理，返回给前端的结果文本。</summary>
    public Func<Dictionary<string, string>, string>? SetHandler { get; set; }

    public string Url => "http://127.0.0.1:" + _config.Port.ToString(CultureInfo.InvariantCulture) + "/";

    public bool TryStart()
    {
        // 端口可能要等一会儿才释放：接管（旧实例为提权重启而退出）时新旧实例会短暂并存，
        // 那个瞬间 17777 还被旧的占着。所以这里重试几次，而不是一失败就放弃 ——
        // 放弃的话 OBS 的浏览器源就再也连不上了。
        Exception? last = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _config.Port);
                listener.Start(64);
                _listener = listener;
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 11) Thread.Sleep(250);
            }
        }

        if (_listener == null)
        {
            var message = "监听 127.0.0.1:" + _config.Port + " 失败（端口被占用？试了 12 次）";
            if (last != null) Log.Error(message, last);
            else Log.Error(message);
            return false;
        }

        _running = true;
        var thread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "fake-esp-http"
        };
        thread.Start();

        Log.Info("本地服务已启动: " + Url + "  (web 根目录: " + _webRoot + ")");
        return true;
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 退出时忽略
        }

        _listener = null;
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch (SocketException)
            {
                if (!_running) return;
                Thread.Sleep(20);
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Accept 异常", ex);
                Thread.Sleep(50);
                continue;
            }

            if (Volatile.Read(ref _connections) >= MaxConnections)
            {
                try { client.Close(); } catch { }
                continue;
            }

            Interlocked.Increment(ref _connections);
            var worker = new Thread(() => ServeClient(client))
            {
                IsBackground = true,
                Name = "fake-esp-http-conn"
            };
            worker.Start();
        }
    }

    private void ServeClient(TcpClient client)
    {
        try
        {
            client.NoDelay = true; // 延迟敏感：/state 会被 60Hz 轮询
            client.ReceiveTimeout = IdleTimeoutMs;
            client.SendTimeout = IdleTimeoutMs;

            using var stream = client.GetStream();
            // leaveOpen: 我们自己管 stream 的生命周期
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, true);

            while (_running)
            {
                var requestLine = reader.ReadLine();
                if (requestLine == null) return;          // 对端关闭
                if (requestLine.Length == 0) continue;    // 容忍空行

                var contentLength = 0;
                while (true)
                {
                    var header = reader.ReadLine();
                    if (header == null) return;
                    if (header.Length == 0) break;

                    var colon = header.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = header[..colon].Trim();
                    var value = header[(colon + 1)..].Trim();

                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                    }
                }

                // 我们只处理 GET/HEAD，但如果有 body 也必须读干净，否则会污染下一个请求
                if (contentLength > 0)
                {
                    var buffer = new char[Math.Min(contentLength, 8192)];
                    var remaining = contentLength;
                    while (remaining > 0)
                    {
                        var read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (read <= 0) return;
                        remaining -= read;
                    }
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    WriteResponse(stream, 400, "text/plain; charset=utf-8", "bad request line");
                    return;
                }

                var method = parts[0].ToUpperInvariant();
                var target = parts[1];
                var headOnly = method == "HEAD";

                if (method != "GET" && method != "HEAD")
                {
                    WriteResponse(stream, 405, "text/plain; charset=utf-8", "only GET/HEAD");
                    continue;
                }

                Interlocked.Increment(ref _requests);

                var queryIndex = target.IndexOf('?');
                var rawPath = queryIndex < 0 ? target : target[..queryIndex];
                var rawQuery = queryIndex < 0 ? string.Empty : target[(queryIndex + 1)..];

                string path;
                try
                {
                    path = Uri.UnescapeDataString(rawPath);
                }
                catch
                {
                    path = rawPath;
                }

                if (!Route(stream, path, rawQuery, headOnly))
                {
                    return; // 连接需要关闭
                }

                if (headOnly)
                {
                    // HEAD 之后按规范应当关闭，省得客户端赖着等 body
                    return;
                }
            }
        }
        catch (IOException)
        {
            // 客户端断开 / 读超时，正常现象
        }
        catch (SocketException)
        {
            // 同上
        }
        catch (Exception ex)
        {
            Log.Error("处理连接异常", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            try { client.Close(); } catch { }
        }
    }

    /// <summary>返回 false 表示需要关闭连接。</summary>
    private bool Route(Stream stream, string path, string rawQuery, bool headOnly)
    {
        switch (path.ToLowerInvariant())
        {
            case "/state":
            case "/api/state":
            {
                var json = StateProvider?.Invoke() ?? "{}";
                WriteResponse(stream, 200, "application/json; charset=utf-8", json, headOnly);
                return true;
            }

            case "/set":
            {
                var query = ParseQuery(rawQuery);
                var result = SetHandler?.Invoke(query) ?? "{\"ok\":false,\"error\":\"no handler\"}";
                WriteResponse(stream, 200, "application/json; charset=utf-8", result, headOnly);
                return true;
            }

            case "/spec":
            {
                // 控件表：网页面板和 WPF 设置窗都从这里生成，加新效果不需要改前端
                WriteResponse(stream, 200, "application/json; charset=utf-8", OptionSpec.ToJson(), headOnly);
                return true;
            }

            case "/log":
            {
                WriteResponse(stream, 200, "text/plain; charset=utf-8", Log.Dump(), headOnly);
                return true;
            }

            case "/health":
            case "/ping":
            {
                WriteResponse(stream, 200, "text/plain; charset=utf-8", "ok", headOnly);
                return true;
            }

            case "/stats":
            {
                var json = "{\"requests\":" + Interlocked.Read(ref _requests) +
                           ",\"connections\":" + Volatile.Read(ref _connections) + "}";
                WriteResponse(stream, 200, "application/json; charset=utf-8", json, headOnly);
                return true;
            }

            case "/shutdown":
            case "/quit":
            {
                WriteResponse(stream, 200, "text/plain; charset=utf-8", "bye", headOnly);
                Log.Info("收到 /shutdown，退出");
                try
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        System.Windows.Application.Current.Shutdown());
                }
                catch (Exception ex)
                {
                    Log.Error("触发退出失败", ex);
                }

                return false;
            }
        }

        return ServeStatic(stream, path, headOnly);
    }

    private bool ServeStatic(Stream stream, string path, bool headOnly)
    {
        if (path.Length == 0 || path == "/") path = "/index.html";

        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Contains("..", StringComparison.Ordinal))
        {
            WriteResponse(stream, 400, "text/plain; charset=utf-8", "bad path", headOnly);
            return true;
        }

        // 没有扩展名就当作 .html：这样 /tune 直接就是 tune.html，
        // 拖进 OBS 当浏览器面板时地址更短好记
        if (!Path.HasExtension(relative)) relative += ".html";

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_webRoot, relative));
        }
        catch
        {
            WriteResponse(stream, 400, "text/plain; charset=utf-8", "bad path", headOnly);
            return true;
        }

        if (!full.StartsWith(Path.GetFullPath(_webRoot), StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(stream, 403, "text/plain; charset=utf-8", "forbidden", headOnly);
            return true;
        }

        if (!File.Exists(full))
        {
            WriteResponse(stream, 404, "text/plain; charset=utf-8",
                "404 not found: " + path + "\n(web 根目录: " + _webRoot + ")\n", headOnly);
            return true;
        }

        try
        {
            var bytes = File.ReadAllBytes(full);
            WriteResponse(stream, 200, MimeFor(full), bytes, headOnly);
        }
        catch (IOException ex)
        {
            Log.Warn("读取失败 " + full + " :: " + ex.Message);
            WriteResponse(stream, 500, "text/plain; charset=utf-8", "read failed", headOnly);
        }

        return true;
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                var key = Uri.UnescapeDataString(pair[..eq]);
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                result[key] = value;
            }
        }

        return result;
    }

    private static void WriteResponse(Stream stream, int status, string contentType, string body, bool headOnly = false) =>
        WriteResponse(stream, status, contentType, Encoding.UTF8.GetBytes(body), headOnly);

    private static void WriteResponse(Stream stream, int status, string contentType, byte[] body, bool headOnly)
    {
        var header = new StringBuilder(256);
        header.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(StatusText(status)).Append("\r\n");
        header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        // 覆盖层会被反复刷新，绝不允许缓存，否则改完代码看不到效果
        header.Append("Cache-Control: no-store, no-cache, must-revalidate, max-age=0\r\n");
        header.Append("Pragma: no-cache\r\n");
        header.Append("Access-Control-Allow-Origin: *\r\n");
        header.Append("Connection: keep-alive\r\n");
        header.Append("Server: fake-esp\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (!headOnly && body.Length > 0)
        {
            stream.Write(body, 0, body.Length);
        }

        stream.Flush();
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "OK"
    };
}
