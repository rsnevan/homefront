using System.Diagnostics;
using System.Management;

namespace Homefront;

/// Opens things on the HTPC screen.
public class Apps
{
    readonly ConfigStore _cfg;
    readonly string _kioskProfile;
    public string? KioskPage { get; private set; }     // "ambient" | "player" | null
    public event Action<string?>? KioskChanged;

    public Apps(ConfigStore cfg, string dataDir)
    {
        _cfg = cfg;
        _kioskProfile = Path.Combine(dataDir, "kiosk-profile");
    }

    string Browser => _cfg.Value.Pc.Browser;

    /// Streaming sites go into the everyday Brave profile so existing logins (Netflix, DStv...) work.
    public async Task OpenUrl(string url)
    {
        if (url.StartsWith("app:", StringComparison.OrdinalIgnoreCase)) { await OpenApp(url[4..].ToLowerInvariant()); return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ArgumentException("Only http(s) links can be opened");
        await CloseKiosk();
        Process.Start(new ProcessStartInfo(Browser, $"--new-window \"{u.AbsoluteUri}\"") { UseShellExecute = false });
        await FocusBrowser(maximize: true);
    }

    // Desktop apps a shortcut can open as "app:<name>": process to look for, and how to start it.
    static readonly Dictionary<string, (string process, string[] start)> KnownApps = new()
    {
        ["spotify"] = ("Spotify", ["spotify:"]),
        ["iptvnator"] = ("IPTVnator", [@"%LOCALAPPDATA%\Programs\iptvnator\IPTVnator.exe", @"%ProgramFiles%\IPTVnator\IPTVnator.exe"]),
        ["fredtv"] = ("open_tv", [@"%ProgramFiles%\Fred TV\open_tv.exe", @"%LOCALAPPDATA%\Programs\Fred TV\open_tv.exe"]),
        ["vlc"] = ("vlc", [@"%ProgramFiles%\VideoLAN\VLC\vlc.exe", @"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe"]),
        ["jellyfin"] = ("Jellyfin Media Player", [@"%ProgramFiles%\Jellyfin\Jellyfin Media Player\JellyfinMediaPlayer.exe"]),
    };

    async Task OpenApp(string name)
    {
        if (!KnownApps.TryGetValue(name, out var app)) throw new ArgumentException($"Unknown app '{name}'");
        await CloseKiosk();
        Process? Window() => Process.GetProcessesByName(app.process).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        var running = Window();
        if (running == null)
        {
            var target = app.start.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(s => s.EndsWith(':') || File.Exists(s))
                         ?? throw new FileNotFoundException($"{name} doesn't seem to be installed on the HTPC");
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            for (var i = 0; i < 30 && running == null; i++) { await Task.Delay(500); running = Window(); }
        }
        if (running != null) WinShell.Focus(running.MainWindowHandle, maximize: true);
    }

    async Task FocusBrowser(bool maximize)
    {
        var name = Path.GetFileNameWithoutExtension(Browser);
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(350);
            var w = Process.GetProcessesByName(name).Where(p => p.MainWindowHandle != IntPtr.Zero)
                .OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } }).FirstOrDefault();
            if (w != null) { WinShell.Focus(w.MainWindowHandle, maximize); return; }
        }
    }

    /// Full-screen homefront pages run in a separate Brave instance (own profile) so kiosk flags apply
    /// and closing it never touches the everyday browser.
    public async Task OpenKiosk(string page, string path)
    {
        await CloseKiosk(notify: false);
        var url = $"http://127.0.0.1:{_cfg.Value.Port}{path}";
        var args = string.Join(' ',
            $"--user-data-dir=\"{_kioskProfile}\"", "--kiosk", "--no-first-run", "--no-default-browser-check",
            "--autoplay-policy=no-user-gesture-required", "--disable-features=Translate,MediaRouter",
            "--disable-session-crashed-bubble", "--hide-crash-restore-bubble", $"\"{url}\"");
        Process.Start(new ProcessStartInfo(Browser, args) { UseShellExecute = false });
        KioskPage = page;
        KioskChanged?.Invoke(page);
        // Brave can take a few seconds to show its window; keep at it until the kiosk is really in front,
        // otherwise Windows parks it behind and just flashes the taskbar.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            var h = KioskProcesses().Select(p => p.MainWindowHandle).FirstOrDefault(w => w != IntPtr.Zero);
            if (h != IntPtr.Zero && WinShell.Focus(h)) { await Task.Delay(800); if (WinShell.Focus(h)) return; }
        }
        Log.Warn($"kiosk {page}: couldn't bring the window to the front");
    }

    public Task CloseKiosk(bool notify = true)
    {
        foreach (var p in KioskProcesses()) { try { p.Kill(entireProcessTree: true); } catch { } }
        if (KioskPage != null)
        {
            KioskPage = null;
            if (notify) KioskChanged?.Invoke(null);
        }
        return Task.CompletedTask;
    }

    List<Process> KioskProcesses()
    {
        var list = new List<Process>();
        try
        {
            var exe = Path.GetFileName(Browser).Replace("'", "''");
            using var q = new ManagementObjectSearcher($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{exe}'");
            foreach (ManagementObject o in q.Get())
            {
                var cmd = o["CommandLine"] as string ?? "";
                if (cmd.Contains(_kioskProfile, StringComparison.OrdinalIgnoreCase) && !cmd.Contains("--type="))
                    try { list.Add(Process.GetProcessById(Convert.ToInt32(o["ProcessId"]))); } catch { }
            }
        }
        catch (Exception e) { Log.Warn($"kiosk scan: {e.Message}"); }
        return list;
    }

    // ---- LG TV via LGTV Companion's CLI (reuses its pairing) ----

    /// Set by Program: whether Home Assistant currently sees the TV as on (null when it can't tell).
    public Func<bool?>? TvIsOn { get; set; }

    /// A TV in deep standby often sleeps through the first wake-up, so keep knocking (LGTV Companion's wake
    /// plus our own magic packets) until the TV reports it's on, for up to 25 seconds.
    public async Task<bool> TvOn()
    {
        if (TvIsOn?.Invoke() == true) return true;
        var mac = TvMac();
        var until = DateTime.UtcNow.AddSeconds(25);
        var attempt = 0;
        while (DateTime.UtcNow < until)
        {
            if (mac != null) await MagicPacket(mac);
            if (attempt++ % 2 == 0) { try { await Tv("-poweron"); } catch (Exception e) { Log.Warn($"tv on: {e.Message}"); } }
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(500);
                if (TvIsOn?.Invoke() == true) { if (attempt > 1) Log.Info($"TV woke after {attempt} tries"); return true; }
            }
            if (TvIsOn?.Invoke() == null && attempt >= 2) return true;   // can't check; two rounds is the best we can do
        }
        Log.Warn("TV didn't wake up within 25 s");
        return false;
    }

    // The TV's MAC, from LGTV Companion's pairing.
    static byte[]? TvMac()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LGTV Companion", "config.json");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var dev in doc.RootElement.EnumerateObject())
                if (dev.Value.ValueKind == System.Text.Json.JsonValueKind.Object && dev.Value.TryGetProperty("MAC", out var macs) && macs.GetArrayLength() > 0)
                    return Convert.FromHexString(macs[0].GetString()!.Replace(":", "").Replace("-", ""));
        }
        catch { }
        return null;
    }

    static async Task MagicPacket(byte[] mac)
    {
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 1; i <= 16; i++) Buffer.BlockCopy(mac, 0, packet, i * 6, 6);
        using var udp = new System.Net.Sockets.UdpClient { EnableBroadcast = true };
        // Every IPv4 network the HTPC is on, plus the global broadcast, on both usual ports.
        var targets = new List<System.Net.IPAddress> { System.Net.IPAddress.Broadcast };
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || u.IPv4Mask == null || System.Net.IPAddress.IsLoopback(u.Address)) continue;
                var ip = u.Address.GetAddressBytes(); var mask = u.IPv4Mask.GetAddressBytes();
                if (ip[0] == 100 && ip[1] >= 64 && ip[1] < 128) continue;   // Tailscale
                targets.Add(new System.Net.IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        }
        foreach (var t in targets.Distinct())
            foreach (var port in new[] { 9, 7 })
                try { await udp.SendAsync(packet, packet.Length, new System.Net.IPEndPoint(t, port)); } catch { }
    }

    public async Task<string> Tv(string args)
    {
        var psi = new ProcessStartInfo(_cfg.Value.Pc.LgtvCli, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        // The TV sometimes never answers (e.g. a notice with a picture); don't let that hang the caller.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return output;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"The TV didn't answer: {args.Split(' ')[0]}");
        }
    }
}
