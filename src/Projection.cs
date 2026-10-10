using System;

namespace FakeEsp;

/// <summary>相机基向量（右手系，yaw=0 时朝向 +Z，yaw 增大 = 向右转）。</summary>
internal struct Camera
{
    public double Px, Py, Pz;
    public double Fx, Fy, Fz;   // forward
    public double Rx, Ry, Rz;   // right
    public double Ux, Uy, Uz;   // up
}

/// <summary>
/// 3D → 2D 投影。这段数学和 web/esp.js 里的 buildCamera / project / projectSegment
/// 是同一套公式，两边必须保持一致，否则浏览器源和覆盖窗的画面会对不上。
/// </summary>
internal static class Projection
{
    public const double Near = 0.05;

    public static Camera BuildCamera(double x, double y, double z, double yawDeg, double pitchDeg)
    {
        const double deg = Math.PI / 180.0;
        var yaw = yawDeg * deg;
        var pitch = pitchDeg * deg;

        var cp = Math.Cos(pitch);
        var sp = Math.Sin(pitch);
        var cy = Math.Cos(yaw);
        var sy = Math.Sin(yaw);

        var fx = sy * cp;
        var fy = sp;
        var fz = cy * cp;

        // right = normalize(cross(worldUp, forward))，worldUp = (0,1,0)
        var rx = fz;
        var ry = 0.0;
        var rz = -fx;
        var rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-9) rl = 1;
        rx /= rl;
        ry /= rl;
        rz /= rl;

        // up = cross(forward, right)
        var ux = fy * rz - fz * ry;
        var uy = fz * rx - fx * rz;
        var uz = fx * ry - fy * rx;

        return new Camera
        {
            Px = x, Py = y, Pz = z,
            Fx = fx, Fy = fy, Fz = fz,
            Rx = rx, Ry = ry, Rz = rz,
            Ux = ux, Uy = uy, Uz = uz
        };
    }

    /// <summary>相机空间深度，正数表示在相机前方。</summary>
    public static double Depth(in Camera k, double x, double y, double z) =>
        (x - k.Px) * k.Fx + (y - k.Py) * k.Fy + (z - k.Pz) * k.Fz;

    /// <summary>投影到屏幕像素坐标。depth 为相机空间深度。</summary>
    public static bool Project(in Camera k, double x, double y, double z,
        double focal, double cx, double cy, out double sx, out double sy, out double depth)
    {
        var dx = x - k.Px;
        var dy = y - k.Py;
        var dz = z - k.Pz;

        depth = dx * k.Fx + dy * k.Fy + dz * k.Fz;
        var inv = 1.0 / Math.Max(depth, 1e-4);
        var xc = dx * k.Rx + dy * k.Ry + dz * k.Rz;
        var yc = dx * k.Ux + dy * k.Uy + dz * k.Uz;

        sx = cx + xc * focal * inv;
        sy = cy - yc * focal * inv;
        return depth > Near;
    }

    /// <summary>
    /// 近平面裁剪后的线段投影。返回 false 表示整段都在相机后面。
    /// 不做裁剪的话，跨过相机的线会在屏幕上翻转成一条横穿全屏的鬼线。
    /// </summary>
    public static bool ProjectSegment(in Camera k,
        double ax, double ay, double az,
        double bx, double by, double bz,
        double focal, double cx, double cy,
        out double x1, out double y1, out double x2, out double y2)
    {
        x1 = y1 = x2 = y2 = 0;

        var za = Depth(k, ax, ay, az);
        var zb = Depth(k, bx, by, bz);
        if (za < Near && zb < Near) return false;

        if (za < Near)
        {
            var t = (Near - za) / (zb - za);
            ax += (bx - ax) * t;
            ay += (by - ay) * t;
            az += (bz - az) * t;
        }
        else if (zb < Near)
        {
            var t = (Near - zb) / (za - zb);
            bx += (ax - bx) * t;
            by += (ay - by) * t;
            bz += (az - bz) * t;
        }

        Project(k, ax, ay, az, focal, cx, cy, out x1, out y1, out _);
        Project(k, bx, by, bz, focal, cx, cy, out x2, out y2, out _);
        return true;
    }
}
