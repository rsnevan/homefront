using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Homefront;

// ---------------- Volume (Core Audio) ----------------

public static class WinAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p);
        int UnregisterControlChangeNotify(IntPtr p);
        int GetChannelCount(out int count);
        int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float levelDb, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float levelDb);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume Endpoint()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out var dev));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out var o));
        return (IAudioEndpointVolume)o;
    }

    public static (int level, bool muted) Get()
    {
        try
        {
            var ep = Endpoint();
            ep.GetMasterVolumeLevelScalar(out var l);
            ep.GetMute(out var m);
            return ((int)Math.Round(l * 100), m);
        }
        catch { return (-1, false); }
    }

    public static void Set(int level)
    {
        var g = Guid.Empty;
        var ep = Endpoint();
        ep.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, ref g);
        if (level > 0) ep.SetMute(false, ref g);
    }

    public static void Mute(bool m) { var g = Guid.Empty; Endpoint().SetMute(m, ref g); }
}

// ---------------- Input (SendInput) ----------------

public static class WinInput
{
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public int dwFlags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    const int MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10, WHEEL = 0x800, HWHEEL = 0x1000;
    const int KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;

    static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    static INPUT Mouse(int flags, int dx = 0, int dy = 0, int data = 0) => new() { type = 0, u = new() { mi = new() { dx = dx, dy = dy, dwFlags = flags, mouseData = data } } };
    static INPUT Key(ushort vk, bool up, int extra = 0) => new() { type = 1, u = new() { ki = new() { wVk = vk, dwFlags = (up ? KEYUP : 0) | extra } } };

    public static void MoveBy(double dx, double dy) => Send(Mouse(MOVE, (int)Math.Round(dx), (int)Math.Round(dy)));

    /// Absolute move in normalized [0,1] coordinates of the primary screen.
    public static void MoveTo(double nx, double ny)
    {
        var b = Screen.PrimaryScreen!.Bounds;
        SetCursorPos(b.Left + (int)(Math.Clamp(nx, 0, 1) * (b.Width - 1)), b.Top + (int)(Math.Clamp(ny, 0, 1) * (b.Height - 1)));
    }

    public static void Click(string button = "left", bool dbl = false)
    {
        var (d, u) = button == "right" ? (RDOWN, RUP) : (LDOWN, LUP);
        Send(Mouse(d), Mouse(u));
        if (dbl) Send(Mouse(d), Mouse(u));
    }

    public static void Button(string button, bool down)
    {
        var flag = button == "right" ? (down ? RDOWN : RUP) : (down ? LDOWN : LUP);
        Send(Mouse(flag));
    }

    public static void Scroll(double dy, double dx = 0)
    {
        if (Math.Abs(dy) > 0.5) Send(Mouse(WHEEL, data: (int)Math.Round(-dy)));
        if (Math.Abs(dx) > 0.5) Send(Mouse(HWHEEL, data: (int)Math.Round(dx)));
    }

    public static void Type(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\n') { list.Add(Key(0x0D, false)); list.Add(Key(0x0D, true)); continue; }
            list.Add(new INPUT { type = 1, u = new() { ki = new() { wScan = ch, dwFlags = UNICODE } } });
            list.Add(new INPUT { type = 1, u = new() { ki = new() { wScan = ch, dwFlags = UNICODE | KEYUP } } });
        }
        if (list.Count > 0) Send(list.ToArray());
    }

    static readonly Dictionary<string, ushort> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["esc"] = 0x1B, ["space"] = 0x20, ["tab"] = 0x09, ["backspace"] = 0x08, ["delete"] = 0x2E,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["f11"] = 0x7A, ["f5"] = 0x74, ["f"] = 0x46, ["m"] = 0x4D, ["k"] = 0x4B,
        ["j"] = 0x4A, ["l"] = 0x4C, ["c"] = 0x43, ["w"] = 0x57, ["t"] = 0x54, ["r"] = 0x52,
        ["ctrl"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B,
        ["volup"] = 0xAF, ["voldown"] = 0xAE, ["mute"] = 0xAD, ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1,
        ["browserback"] = 0xA6, ["browserforward"] = 0xA7,
    };
    static readonly HashSet<ushort> Extended = [0x25, 0x26, 0x27, 0x28, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x5B];

    /// "ctrl+w", "alt+f4", "space", "f11"...
    public static void Combo(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Equals("f4", StringComparison.OrdinalIgnoreCase) ? (ushort)0x73 : Vk.TryGetValue(k, out var v) ? v : (ushort)0)
            .Where(v => v != 0).ToList();
        if (keys.Count == 0) return;
        var seq = new List<INPUT>();
        foreach (var k in keys) seq.Add(Key(k, false, Extended.Contains(k) ? EXTENDED : 0));
        for (var i = keys.Count - 1; i >= 0; i--) seq.Add(Key(keys[i], true, Extended.Contains(keys[i]) ? EXTENDED : 0));
        Send(seq.ToArray());
    }
}

// ---------------- Screen capture ----------------

public static class WinScreen
{
    static byte[]? _last;
    static int _lastW;
    static DateTime _lastAt;
    static readonly object L = new();
    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    public static (int w, int h) Size { get { var b = Screen.PrimaryScreen!.Bounds; return (b.Width, b.Height); } }

    public static byte[] Capture(int width, int quality = 62)
    {
        lock (L)
        {
            if (_last != null && _lastW == width && DateTime.UtcNow - _lastAt < TimeSpan.FromMilliseconds(350)) return _last;
            var b = Screen.PrimaryScreen!.Bounds;
            using var full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(g, b);
            }
            var h = (int)Math.Round(width * (double)b.Height / b.Width);
            using var small = new Bitmap(width, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.DrawImage(full, 0, 0, width, h);
            }
            using var ms = new MemoryStream();
            var p = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality) } };
            small.Save(ms, Jpeg, p);
            _last = ms.ToArray(); _lastW = width; _lastAt = DateTime.UtcNow;
            return _last;
        }
    }

    // CopyFromScreen leaves the pointer out; draw an amber ring so a phone user can see where they are.
    static void DrawCursor(Graphics g, Rectangle b)
    {
        if (!WinInput.GetCursorPos(out var p)) return;
        var r = Math.Max(10, b.Width / 160);
        var x = p.X - b.Left; var y = p.Y - b.Top;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(Color.FromArgb(230, 11, 18, 32), r / 2.2f);
        using var ring = new Pen(Color.FromArgb(255, 0xFF, 0xC9, 0x4D), r / 3.5f);
        g.DrawEllipse(outline, x - r, y - r, r * 2, r * 2);
        g.DrawEllipse(ring, x - r, y - r, r * 2, r * 2);
        using var dot = new SolidBrush(Color.FromArgb(255, 0xFF, 0xC9, 0x4D));
        g.FillEllipse(dot, x - r / 4f, y - r / 4f, r / 2f, r / 2f);
    }
}

// ---------------- Windows / power / stats ----------------

public static class WinShell
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, IntPtr extra);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] struct MEMSTAT { public uint len, load; public ulong total, avail, tp, ap, tv, av, ext; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);

    /// Windows blocks focus-stealing; a synthetic Alt tap convinces it the user asked for it.
    public static void Focus(IntPtr h, bool maximize = false)
    {
        if (h == IntPtr.Zero) return;
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        if (IsIconic(h)) ShowWindow(h, 9);
        if (maximize) ShowWindow(h, 3);
        SetForegroundWindow(h);
    }

    public static (string title, string process) Foreground()
    {
        var h = GetForegroundWindow();
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out var pid);
        string proc = "";
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        return (sb.ToString(), proc);
    }

    public static void Sleep() => SetSuspendState(false, false, false);
    public static void Lock() => LockWorkStation();
    public static void Restart() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 5 /c \"Restarting from homefront\"") { CreateNoWindow = true, UseShellExecute = false });

    static long _idle, _kernel, _user;
    public static double Cpu()
    {
        GetSystemTimes(out var i, out var k, out var u);
        var di = i - _idle; var dt = (k - _kernel) + (u - _user);
        _idle = i; _kernel = k; _user = u;
        return dt <= 0 ? 0 : Math.Round(100.0 * (dt - di) / dt, 1);
    }

    public static (double usedGb, double totalGb) Memory()
    {
        var m = new MEMSTAT { len = (uint)Marshal.SizeOf<MEMSTAT>() };
        GlobalMemoryStatusEx(ref m);
        return (Math.Round((m.total - m.avail) / 1073741824.0, 1), Math.Round(m.total / 1073741824.0, 1));
    }
}
