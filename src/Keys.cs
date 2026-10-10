namespace FakeEsp;

/// <summary>只列出用得到的虚拟键码。</summary>
internal static class Keys
{
    public const int LButton = 0x01;
    public const int RButton = 0x02;
    public const int MButton = 0x04;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int Tab = 0x09;

    public const int D0 = 0x30;
    public const int D1 = 0x31;
    public const int D2 = 0x32;
    public const int D3 = 0x33;

    public const int A = 0x41;
    public const int B = 0x42;
    public const int C = 0x43;
    public const int D = 0x44;
    public const int E = 0x45;
    public const int F = 0x46;
    public const int G = 0x47;
    public const int H = 0x48;
    public const int I = 0x49;
    public const int J = 0x4A;
    public const int K = 0x4B;
    public const int L = 0x4C;
    public const int M = 0x4D;
    public const int N = 0x4E;
    public const int O = 0x4F;
    public const int P = 0x50;
    public const int Q = 0x51;
    public const int R = 0x52;
    public const int S = 0x53;
    public const int T = 0x54;
    public const int U = 0x55;
    public const int V = 0x56;
    public const int W = 0x57;
    public const int X = 0x58;
    public const int Y = 0x59;
    public const int Z = 0x5A;

    public const int F1 = 0x70;
    public const int F2 = 0x71;
    public const int F3 = 0x72;
    public const int F4 = 0x73;
    public const int F5 = 0x74;
    public const int F6 = 0x75;
    public const int F7 = 0x76;
    public const int F8 = 0x77;
    public const int F9 = 0x78;
    public const int F10 = 0x79;
    public const int F11 = 0x7A;
    public const int F12 = 0x7B;

    /// <summary>每帧轮询的按键集合。/state 里的 keys 位图顺序也按这个表。</summary>
    public static readonly int[] Watched =
    {
        W, A, S, D, Q, E, R, F,
        Space, Shift, Control,
        Up, Down, Left, Right,
        Home, Insert, End, Delete,
        D1, D2, D3,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Escape, Tab, LButton, RButton
    };

    public static readonly string[] WatchedNames =
    {
        "W", "A", "S", "D", "Q", "E", "R", "F",
        "Space", "Shift", "Control",
        "Up", "Down", "Left", "Right",
        "Home", "Insert", "End", "Delete",
        "1", "2", "3",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Esc", "Tab", "LMB", "RMB"
    };
}
