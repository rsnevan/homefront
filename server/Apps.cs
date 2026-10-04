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
        await Task.Delay(1500);
        foreach (var p in KioskProcesses()) { if (p.MainWindowHandle != IntPtr.Zero) { WinShell.Focus(p.MainWindowHandle); break; } }
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

    public async Task<string> Tv(string args)
    {
        var psi = new ProcessStartInfo(_cfg.Value.Pc.LgtvCli, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}
