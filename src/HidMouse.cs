using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FakeEsp;

/// <summary>
/// 输入方案三：**直接读鼠标的 HID 设备**。
///
/// 为什么需要它：实测在这台机器上，WM_INPUT 和 WH_MOUSE_LL 两条链路一条数据都收不到
/// （真实鼠标移动 929 像素，两者计数仍然是 0），而消息循环本身是正常的 —— 说明
/// 操作系统根本没把输入事件投递给我们这个进程（受限令牌 / UIPI 会这样）。
/// HID 直读绕开整个"事件投递"模型：自己打开设备、自己读报告。
///
/// 附带的好处：拿到的是**设备原始增量**，游戏把光标锁在屏幕中心时照样有数据，
/// 而且没有系统指针加速，和游戏自己读的原始输入曲线一致。
///
/// 安全声明：读鼠标 HID **不会抢走输入**。HID 输入报告是广播给所有已打开的句柄的，
/// 游戏照样收得到；这里既不写设备，也不改任何系统状态。
/// </summary>
internal sealed class HidMouse : IDisposable
{
    // ---- SetupAPI ----
    private static readonly Guid GuidDeviceInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    // ---- hid.dll 返回值 ----
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_Input = 0;

    // ---- 报告描述符里的用量 ----
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageMouse = 0x02;
    private const ushort UsageX = 0x30;
    private const ushort UsageY = 0x31;
    private const ushort UsageWheel = 0x38;

    // HIDP_VALUE_CAPS 的结构偏移（自己按 hidsdi.h 推算，避免结构体布局写错）
    private const int ValueCapsStride = 72;
    private const int OffsetBitSize = 18;
    private const int OffsetLogicalMin = 40;
    private const int OffsetNotRangeUsage = 56;

    // HIDP_CAPS 的字段偏移
    private const int CapsOffsetUsage = 2;
    private const int CapsOffsetInputReportByteLength = 4;
    private const int CapsOffsetNumberInputValueCaps = 48;

    private readonly InputState _input;
    private Thread? _thread;
    private volatile bool _running;
    private IntPtr _handle = IntPtr.Zero;
    private IntPtr _preparsed = IntPtr.Zero;
    private int _reportLength;
    private int _xBitSize = 8;
    private int _yBitSize = 8;
    private int _xLogicalMin = -127;
    private int _yLogicalMin = -127;
    private bool _haveWheel;

    private long _reports;
    private long _nonZero;
    private string _devicePath = string.Empty;

    public bool IsOpen => _handle != IntPtr.Zero && _handle != new IntPtr(-1);
    public string DevicePath => _devicePath;
    public int ReportLength => _reportLength;
    public long Reports => Interlocked.Read(ref _reports);
    public long NonZero => Interlocked.Read(ref _nonZero);

    public HidMouse(InputState input)
    {
        _input = input;
    }

    /// <summary>枚举所有 HID 设备，找到第一个鼠标并打开。返回是否成功。</summary>
    public bool TryOpen()
    {
        // 静态只读字段不能按 ref 传出去，先拷一份到局部变量
        var interfaceGuid = GuidDeviceInterfaceHid;

        var deviceSet = Native.SetupDiGetClassDevs(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1))
        {
            Log.Warn("枚举 HID 设备失败，Win32 错误码 " + Marshal.GetLastWin32Error());
            return false;
        }

        var found = 0;
        var denied = 0;
        var notMouse = 0;
        var lastReason = string.Empty;
        try
        {
            var interfaceData = new Native.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>()
            };
            var guidForEnum = GuidDeviceInterfaceHid;

            for (uint index = 0; ; index++)
            {
                if (!Native.SetupDiEnumDeviceInterfaces(deviceSet, IntPtr.Zero, ref guidForEnum, index, ref interfaceData))
                {
                    break; // 枚举结束
                }

                var path = GetDevicePath(deviceSet, ref interfaceData);
                if (string.IsNullOrEmpty(path)) continue;
                found++;

                if (TryOpenPath(path, out var reason))
                {
                    Log.Info("HID 直读: 选中 " + Shorten(path));
                    _input.HidSummary = "枚举 " + found + " 个设备，成功打开 " + Shorten(path)
                                        + "（报告 " + _reportLength + " 字节）";
                    return true;
                }

                // 逐个设备报告为什么不行 —— 不这样就没法判断是"没枚举到鼠标"还是"打开了但打不开"
                Log.Info("HID [" + index + "] " + Shorten(path) + " -> " + reason);
                lastReason = reason;
                if (reason.StartsWith("不是鼠标", StringComparison.Ordinal)) notMouse++;
                else if (reason.StartsWith("CreateFile", StringComparison.Ordinal)) denied++;
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举 HID 设备异常", ex);
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceSet);
        }

        Log.Warn("在 " + found + " 个 HID 设备里没有找到可读的鼠标");
        _input.HidSummary = "枚举 " + found + " 个设备；不是鼠标 " + notMouse + " 个；打不开(拒绝访问等) "
                            + denied + " 个" + (lastReason.Length > 0 ? "；最后一条：" + lastReason : string.Empty);
        return false;
    }

    private static string GetDevicePath(IntPtr deviceSet, ref Native.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        Native.SetupDiGetDeviceInterfaceDetail(deviceSet, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return string.Empty;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize 必须是"结构体本身"的大小：x64 是 8，x86 是 4 + 2
            var cbSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(buffer, cbSize);

            if (!Native.SetupDiGetDeviceInterfaceDetail(deviceSet, ref interfaceData, buffer, required, out _, IntPtr.Zero))
            {
                return string.Empty;
            }

            // DevicePath 紧跟在 DWORD cbSize 之后
            return Marshal.PtrToStringUni(buffer + 4) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>设备接口路径又长又难读，日志里只留可辨识的尾巴。</summary>
    private static string Shorten(string path)
    {
        var trimmed = path.Replace(@"\\?\", string.Empty);
        var parts = trimmed.Split('#');
        return parts.Length >= 3
            ? parts[1] + " / " + parts[2]
            : trimmed;
    }

    private bool TryOpenPath(string path, out string reason)
    {
        reason = "未知原因";

        var handle = Native.CreateFileRead(path);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            reason = "CreateFile 失败，错误码 " + Marshal.GetLastWin32Error();
            return false;
        }

        var preparsed = IntPtr.Zero;
        try
        {
            if (!Native.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
            {
                reason = "HidD_GetPreparsedData 失败，错误码 " + Marshal.GetLastWin32Error();
                return false;
            }

            var caps = Marshal.AllocHGlobal(256);
            try
            {
                var capsStatus = Native.HidP_GetCaps(preparsed, caps);
                if (capsStatus != HIDP_STATUS_SUCCESS)
                {
                    reason = "HidP_GetCaps 状态码 0x" + capsStatus.ToString("X8");
                    return false;
                }

                var usagePage = (ushort)Marshal.ReadInt16(caps, 0);
                var usage = (ushort)Marshal.ReadInt16(caps, CapsOffsetUsage);
                var reportLength = (ushort)Marshal.ReadInt16(caps, CapsOffsetInputReportByteLength);

                if (usagePage != UsagePageGenericDesktop || usage != UsageMouse)
                {
                    reason = "不是鼠标 (usagePage=0x" + usagePage.ToString("X2") + " usage=0x" + usage.ToString("X2") + ")";
                    return false;
                }

                if (reportLength <= 0)
                {
                    reason = "输入报告长度为 0";
                    return false;
                }

                _handle = handle;
                _preparsed = preparsed;
                _reportLength = reportLength;
                _devicePath = path;
                ReadValueCaps(caps);
                reason = "OK";
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(caps);
            }
        }
        catch (Exception ex)
        {
            reason = "异常 " + ex.GetType().Name;
        }

        if (preparsed != IntPtr.Zero) Native.HidD_FreePreparsedData(preparsed);
        Native.CloseHandleSafe(handle);
        return false;
    }

    /// <summary>读出 X/Y 的位宽和逻辑最小值，用来正确地把无符号结果还原成有符号增量。</summary>
    private void ReadValueCaps(IntPtr caps)
    {
        var count = (ushort)Marshal.ReadInt16(caps, CapsOffsetNumberInputValueCaps);
        if (count == 0) return;

        var buffer = Marshal.AllocHGlobal(ValueCapsStride * count);
        try
        {
            var length = count;
            if (Native.HidP_GetValueCaps(HidP_Input, buffer, ref length, _preparsed) != HIDP_STATUS_SUCCESS) return;

            for (var i = 0; i < length; i++)
            {
                var offset = i * ValueCapsStride;
                var usagePage = (ushort)Marshal.ReadInt16(buffer, offset);
                if (usagePage != UsagePageGenericDesktop) continue;

                var usage = (ushort)Marshal.ReadInt16(buffer, offset + OffsetNotRangeUsage);
                var bitSize = (ushort)Marshal.ReadInt16(buffer, offset + OffsetBitSize);
                var logicalMin = Marshal.ReadInt32(buffer, offset + OffsetLogicalMin);

                switch (usage)
                {
                    case UsageX:
                        _xBitSize = bitSize;
                        _xLogicalMin = logicalMin;
                        break;
                    case UsageY:
                        _yBitSize = bitSize;
                        _yLogicalMin = logicalMin;
                        break;
                    case UsageWheel:
                        _haveWheel = true;
                        break;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Start()
    {
        if (!IsOpen) return;

        _input.HidOpened = true;
        Log.Info("HID 直读已打开: " + _devicePath);
        Log.Info("  X/Y 位宽 " + _xBitSize + "/" + _yBitSize + "，" + (IntPtr.Size == 8 ? "x64" : "x86")
                 + "，输入报告长度 " + _reportLength + " 字节");

        _running = true;
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "fake-esp-hid",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    private void ReadLoop()
    {
        var buffer = Marshal.AllocHGlobal(_reportLength);
        var loggedFirst = false;

        try
        {
            while (_running)
            {
                if (!Native.ReadFileHid(_handle, buffer, (uint)_reportLength, out var read) || read == 0)
                {
                    // 设备被拔出 / 句柄失效
                    var error = Marshal.GetLastWin32Error();
                    if (_running) Log.Warn("读取 HID 报告失败，Win32 错误码 " + error + "，HID 直读停止");
                    return;
                }

                Interlocked.Increment(ref _reports);
                _input.ReportEvent(MouseSource.Hid);

                var x = ReadUsage(buffer, read, UsageX, _xBitSize, _xLogicalMin);
                var y = ReadUsage(buffer, read, UsageY, _yBitSize, _yLogicalMin);

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    var bytes = new StringBuilder();
                    for (var i = 0; i < Math.Min((int)read, 8); i++)
                        bytes.Append(Marshal.ReadByte(buffer, i).ToString("X2")).Append(' ');
                    Log.Info("第一份 HID 报告(" + read + " 字节): " + bytes + "-> dx=" + x + " dy=" + y);
                }

                if (x == 0 && y == 0)
                {
                    if (_haveWheel)
                    {
                        var wheel = ReadUsage(buffer, read, UsageWheel, 8, 0);
                        if (wheel != 0) _input.AddWheel(wheel, MouseSource.Hid);
                    }

                    continue;
                }

                Interlocked.Increment(ref _nonZero);
                _input.AddMouse(x, y, MouseSource.Hid);
            }
        }
        catch (Exception ex)
        {
            Log.Error("HID 读取线程异常", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>用 HidP_GetUsageValue 取值，再按逻辑范围还原成有符号数。</summary>
    private int ReadUsage(IntPtr buffer, uint length, ushort usage, int bitSize, int logicalMin)
    {
        var status = Native.HidP_GetUsageValue(HidP_Input, UsagePageGenericDesktop, 0, usage,
            out var value, _preparsed, buffer, length);
        if (status != HIDP_STATUS_SUCCESS) return 0;

        // 有符号轴（逻辑最小值是负数）需要把无符号结果符号扩展回去
        if (logicalMin < 0 && bitSize is > 0 and < 32)
        {
            var signBit = 1u << (bitSize - 1);
            if ((value & signBit) != 0) return (int)((long)value - (1L << bitSize));
        }

        return (int)value;
    }

    public void Dispose()
    {
        _running = false;

        // 关掉句柄能让阻塞中的 ReadFile 立刻返回，线程随之退出
        if (IsOpen)
        {
            Native.CancelIoEx(_handle, IntPtr.Zero);
        }

        try
        {
            _thread?.Join(800);
        }
        catch
        {
            // 退出时忽略
        }

        if (_preparsed != IntPtr.Zero)
        {
            Native.HidD_FreePreparsedData(_preparsed);
            _preparsed = IntPtr.Zero;
        }

        if (IsOpen)
        {
            Native.CloseHandleSafe(_handle);
            _handle = IntPtr.Zero;
        }

        _input.HidOpened = false;
    }
}
