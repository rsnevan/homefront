using System.Collections.Concurrent;
using System.Security.Cryptography;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Homefront;

public record MediaState(
    bool Active, string? App, string? AppName, string? Title, string? Artist, string? Album,
    string Status, bool IsVideo, double Position, double Duration, long UpdatedAt,
    string? ArtVersion, bool CanPrev, bool CanNext, bool CanSeek);

/// "What's playing right now" on the HTPC. Candidates are every app in Windows' media session
/// (Spotify, browser tabs...) plus player windows that don't publish one (VLC...). Whatever is
/// playing wins; if several are, the one that started most recently. Nothing playing: the last one.
public class WinMedia
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;   // chosen media-session app, if that's what's showing
    PlayerWin? _window;                                    // chosen player window, if that's what's showing
    readonly SemaphoreSlim _refresh = new(1, 1);
    public MediaState State { get; private set; } = Empty;
    public byte[]? Art { get; private set; }
    public string? ArtType { get; private set; }
    public event Action<MediaState>? Changed;
    public ArtFinder? Finder { get; set; }

    // Artwork for sources that don't provide any, looked up once per track in the background.
    string? _artKey, _artVer;
    string? ArtFor(bool video, string? title, string? artist)
    {
        var key = $"{video}|{title}|{artist}";
        if (key == _artKey) return _artVer;
        _artKey = key; _artVer = null; Art = null;
        if (Finder == null || string.IsNullOrWhiteSpace(title)) return null;
        _ = Task.Run(async () =>
        {
            var r = await Finder.Find(video, title, artist);
            if (_artKey != key || r == null) return;
            Art = r.Value.bytes; ArtType = r.Value.type;
            _artVer = Convert.ToHexString(SHA1.HashData(Art))[..12];
            await Refresh();
        });
        return null;
    }

    static readonly MediaState Empty = new(false, null, null, null, null, null, "stopped", false, 0, 0, 0, null, false, false, false);

    record PlayerWin(string Proc, string App, string Title, IntPtr Hwnd, int Pid) { public string Key => $"window:{Proc}:{Pid}"; }
    class Seen { public string Status = "paused"; public DateTime PlayingSince = DateTime.MinValue, LastPlaying = DateTime.MinValue; }
    readonly Dictionary<string, Seen> _seen = new();

    // Audio activity per process, sampled 4x a second. A player counts as playing while it has made
    // sound in the last few seconds (quiet scenes shouldn't flip it to paused).
    readonly ConcurrentDictionary<int, DateTime> _loudAt = new();
    readonly ConcurrentDictionary<string, (string status, DateTime until)> _override = new();
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(6);

    public async Task Start()
    {
        // Its own thread: on the shared pool a busy moment could delay sampling past the grace period,
        // which looked like silence and briefly flipped playing videos to "paused".
        new Thread(SampleAudio) { IsBackground = true, Name = "audio sampler" }.Start();
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => _ = Refresh();
            _mgr.SessionsChanged += (_, _) => _ = Refresh();
            await Refresh();
            Log.Info("Media session watcher started");
        }
        catch (Exception e) { Log.Warn($"Media sessions unavailable: {e.Message}"); }
    }

    DateTime _sampledAt = DateTime.UtcNow;

    void SampleAudio()
    {
        while (true)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var (pid, peak) in WinAudio.SessionPeaks())
                    if (peak > 0.0015f) _loudAt[pid] = now;
                _sampledAt = now;
            }
            catch { }
            Thread.Sleep(250);
        }
    }

    // Measured against the last successful sample, not the wall clock: if sampling stalls, nothing goes quiet.
    bool Loud(int pid) => _loudAt.TryGetValue(pid, out var at) && _sampledAt - at < Grace;

    string WindowStatus(PlayerWin w) =>
        _override.TryGetValue(w.Key, out var o) && o.until > DateTime.UtcNow ? o.status : Loud(w.Pid) ? "playing" : "paused";

    static string SessionStatus(GlobalSystemMediaTransportControlsSession s) => s.GetPlaybackInfo()?.PlaybackStatus switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
        _ => "stopped",
    };

    public async Task Refresh()
    {
        if (!await _refresh.WaitAsync(3000)) return;
        try
        {
            var now = DateTime.UtcNow;
            var cands = new List<(string key, string status, object src)>();
            if (_mgr != null)
                foreach (var s in _mgr.GetSessions())
                    cands.Add(("smtc:" + (s.SourceAppUserModelId ?? "?"), SessionStatus(s), s));
            var windows = PlayerWindows().ToList();
            foreach (var w in windows)
                cands.Add((w.Key, WindowStatus(w), w));
            // Apps that make sound without telling Windows what they're playing (Spotify here, IPTVnator...).
            foreach (var a in AudioApps(cands, windows))
                cands.Add((a.Key, Loud(a.Pid) ? "playing" : "paused", a));

            foreach (var (key, status, _) in cands)
            {
                if (!_seen.TryGetValue(key, out var seen)) _seen[key] = seen = new Seen();
                if (status == "playing")
                {
                    if (seen.Status != "playing") seen.PlayingSince = now;
                    seen.LastPlaying = now;
                }
                seen.Status = status;
            }
            foreach (var gone in _seen.Keys.Except(cands.Select(c => c.key)).ToList()) _seen.Remove(gone);

            var pick = cands.Where(c => c.status == "playing").OrderByDescending(c => _seen[c.key].PlayingSince).FirstOrDefault();
            if (pick.key == null) pick = cands.Where(c => _seen[c.key].LastPlaying > DateTime.MinValue).OrderByDescending(c => _seen[c.key].LastPlaying).FirstOrDefault();
            if (pick.key == null && _mgr?.GetCurrentSession() is { } cur) pick = cands.FirstOrDefault(c => ReferenceEquals(c.src, cur) || c.key == "smtc:" + cur.SourceAppUserModelId);
            if (pick.key == null) pick = cands.FirstOrDefault();

            _audioApp = null;
            switch (pick.src)
            {
                case AudioApp a:
                    Watch(null); _window = null; _audioApp = a;
                    var status = _override.TryGetValue(a.Key, out var ov) && ov.until > DateTime.UtcNow ? ov.status : pick.status;
                    var aart = a.Title != null ? ArtFor(a.IsVideo, a.Title, a.Artist) : null;
                    Publish(new MediaState(true, a.Key, a.App, a.Title ?? a.App, a.Artist, null, status, a.IsVideo, 0, 0,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), aart, true, true, false));
                    break;
                case PlayerWin w:
                    Watch(null); _window = w;
                    Publish(new MediaState(true, "window:" + w.Proc, w.App, w.Title, null, null, pick.status, true, 0, 0,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ArtFor(true, w.Title, null), true, true, false));
                    break;
                case GlobalSystemMediaTransportControlsSession s:
                    _window = null; Watch(s); _artKey = null;
                    await PublishSession(s);
                    break;
                default:
                    Watch(null); _window = null; Art = null; _artKey = null;
                    Publish(Empty);
                    break;
            }
        }
        catch (Exception e) { Log.Warn($"media refresh: {e.Message}"); }
        finally { _refresh.Release(); }
    }

    // Live updates (track changes, seeking) from whichever media-session app is showing.
    void Watch(GlobalSystemMediaTransportControlsSession? s)
    {
        if (ReferenceEquals(s, _session)) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChange;
            _session.PlaybackInfoChanged -= OnChange;
            _session.TimelinePropertiesChanged -= OnChange;
        }
        _session = s;
        if (s != null)
        {
            s.MediaPropertiesChanged += OnChange;
            s.PlaybackInfoChanged += OnChange;
            s.TimelinePropertiesChanged += OnChange;
        }
    }

    void OnChange(GlobalSystemMediaTransportControlsSession s, object _) => _ = Refresh();

    async Task PublishSession(GlobalSystemMediaTransportControlsSession s)
    {
        var info = s.GetPlaybackInfo();
        var tl = s.GetTimelineProperties();
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try { props = await s.TryGetMediaPropertiesAsync(); } catch { }
        var status = SessionStatus(s);
        var app = s.SourceAppUserModelId ?? "";
        var isVideo = IsVideoSession(app, info?.PlaybackType, props);

        string? artVersion = State.App == app ? State.ArtVersion : null;
        if (props?.Thumbnail != null && (props.Title != State.Title || State.App != app || Art == null))
        {
            var (bytes, type) = await ReadThumb(props.Thumbnail);
            Art = bytes; ArtType = type;
            artVersion = bytes == null ? null : Convert.ToHexString(SHA1.HashData(bytes))[..12];
        }
        else if (props?.Thumbnail == null) { Art = null; artVersion = null; }

        var position = tl.Position.TotalSeconds;
        if (status == "playing") position += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
        var duration = (tl.EndTime - tl.StartTime).TotalSeconds;

        Publish(new MediaState(
            true, app, FriendlyApp(app), Blank(props?.Title), Blank(props?.Artist), Blank(props?.AlbumTitle),
            status, isVideo, Math.Max(0, Math.Min(position, duration > 0 ? duration : position)), Math.Max(0, duration),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), artVersion,
            info?.Controls.IsPreviousEnabled ?? false, info?.Controls.IsNextEnabled ?? false,
            info?.Controls.IsPlaybackPositionEnabled ?? false));
    }

    // Only things you watch count as video (they drive follow-what's-playing and the sunset fade);
    // music, games and calls don't.
    static readonly string[] MusicApps = ["spotify", "applemusic", "itunes", "deezer", "tidal", "foobar", "amazonmusic", "ytmdesktop", "youtube-music"];
    static readonly string[] Browsers = ["brave", "chrome", "msedge", "firefox", "opera", "vivaldi"];
    static readonly string[] VideoApps = ["iptvnator", "vlc", "jellyfin", "plex", "kodi", "stremio", "mpv", "mpc-hc", "mpc-be", "potplayer", "smplayer", "zunevideo", "video.ui", "microsoft.media.player"];
    static bool Has(string id, string[] list) => list.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase));

    static bool IsVideoSession(string app, Windows.Media.MediaPlaybackType? type, GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (Has(app, MusicApps)) return false;
        if (type == Windows.Media.MediaPlaybackType.Video) return true;
        // Chromium browsers report every tab as "music"; music sites (YouTube Music, Spotify Web...) fill in an album, video sites don't.
        if (Has(app, Browsers)) return string.IsNullOrWhiteSpace(props?.AlbumTitle);
        return Has(app, VideoApps);
    }

    void Publish(MediaState s)
    {
        var old = State;
        State = s;
        // Timeline ticks arrive constantly; only push when something a viewer would notice changed.
        if (old with { Position = 0, UpdatedAt = 0 } == s with { Position = 0, UpdatedAt = 0 } && Math.Abs(old.Position - s.Position) < 3) return;
        Changed?.Invoke(s);
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static string FriendlyApp(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("jellyfin")) return "Jellyfin";
        if (l.Contains("iptvnator")) return "IPTVnator";
        var name = Path.GetFileNameWithoutExtension(id.Split('!')[0]);
        return string.IsNullOrEmpty(name) ? "PC" : char.ToUpper(name[0]) + name[1..];
    }

    static async Task<(byte[]?, string?)> ReadThumb(IRandomAccessStreamReference r)
    {
        try
        {
            using var stream = await r.OpenReadAsync();
            using var input = stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            var size = (uint)stream.Size;
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return (bytes, string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType);
        }
        catch { return (null, null); }
    }

    static readonly (string proc, string suffix, string app)[] WindowPlayers =
    [
        ("vlc", " - VLC media player", "VLC"),
        ("mpc-hc64", " - MPC-HC", "MPC-HC"),
        ("PotPlayerMini64", " - PotPlayer", "PotPlayer"),
    ];

    static IEnumerable<PlayerWin> PlayerWindows()
    {
        foreach (var (proc, suffix, app) in WindowPlayers)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
            {
                var t = p.MainWindowTitle;
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && t.Length > suffix.Length)
                    yield return new PlayerWin(proc, app, Tidy(t[..^suffix.Length]), p.MainWindowHandle, p.Id);
            }
    }

    // "Some.Show.S02E05.1080p.WEB-DL.x264-GROUP" -> "Some Show S02E05"
    static string Tidy(string name)
    {
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\.(mkv|mp4|avi|m4v|mov|wmv|webm)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var cut = System.Text.RegularExpressions.Regex.Match(name, @"(?i)[ ._\-\[(]+(19|20)\d{2}[ ._\-\])]|[ ._\-]+(2160p|1080p|720p|480p|webrip|web-dl|bluray|hdtv|x264|x265|hevc)");
        if (cut.Success && cut.Index > 0) name = name[..cut.Index];
        var ep = System.Text.RegularExpressions.Regex.Match(name, @"(?i)S\d{1,2}E\d{1,3}");
        if (ep.Success) name = name[..(ep.Index + ep.Length)];
        return System.Text.RegularExpressions.Regex.Replace(name.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim();
    }

    record AudioApp(string Proc, string App, string? Title, string? Artist, int Pid, bool IsVideo) { public string Key => $"audio:{Proc.ToLowerInvariant()}:{Pid}"; }
    AudioApp? _audioApp;
    readonly Dictionary<string, (string? title, string? artist)> _lastTrack = new();
    static readonly HashSet<string> NotPlayers = new(StringComparer.OrdinalIgnoreCase) { "homefront", "audiodg", "explorer", "ShellExperienceHost", "SystemSettings", "LGTVdaemon", "LGTVsvc", "svchost", "RemoteServerWin" };

    // Processes that made sound in the last 10 minutes, have a window, and aren't already covered.
    IEnumerable<AudioApp> AudioApps(List<(string key, string status, object src)> cands, List<PlayerWin> windows)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var (pid, at) in _loudAt.ToArray())
        {
            if (at < cutoff || windows.Any(w => w.Pid == pid)) continue;
            System.Diagnostics.Process p;
            try { p = System.Diagnostics.Process.GetProcessById(pid); } catch { _loudAt.TryRemove(pid, out _); continue; }
            if (NotPlayers.Contains(p.ProcessName)) continue;
            // Browsers etc. spread audio over child processes; walk up to the one that owns the window.
            var owner = p.MainWindowHandle != IntPtr.Zero ? p : MainWindowOwner(p.ProcessName);
            if (owner == null) continue;
            if (cands.Any(c => c.key.StartsWith("smtc:") && c.key.Contains(owner.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
            var key = $"audio:{owner.ProcessName.ToLowerInvariant()}:{pid}";
            var (title, artist) = TrackFromWindow(owner.ProcessName, owner.MainWindowTitle);
            if (title != null) _lastTrack[key] = (title, artist);
            else if (_lastTrack.TryGetValue(key, out var last)) (title, artist) = last;
            var app = FriendlyApp(owner.ProcessName);
            yield return new AudioApp(owner.ProcessName, app, title, artist, pid, Has(owner.ProcessName, VideoApps) && !Has(owner.ProcessName, MusicApps));
        }
    }

    static System.Diagnostics.Process? MainWindowOwner(string name) =>
        System.Diagnostics.Process.GetProcessesByName(name).FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);

    // Spotify's window reads "Artist - Song" while playing and just "Spotify Premium" when paused.
    static (string? title, string? artist) TrackFromWindow(string proc, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return (null, null);
        if (proc.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            if (windowTitle.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase)) return (null, null);
            var i = windowTitle.IndexOf(" - ", StringComparison.Ordinal);
            return i > 0 ? (windowTitle[(i + 3)..].Trim(), windowTitle[..i].Trim()) : (windowTitle, null);
        }
        var t = System.Text.RegularExpressions.Regex.Replace(windowTitle, @"\s+[-–—]\s+(Brave|Google Chrome|Microsoft​? Edge|Mozilla Firefox)$", "");
        return (t, null);
    }

    /// Controls whatever is showing as "now playing".
    public async Task<bool> Control(string action, double? seconds = null)
    {
        if (_audioApp is { } a)
        {
            // These apps listen to the keyboard media keys (Spotify does, system-wide).
            var playing = (_override.TryGetValue(a.Key, out var o) && o.until > DateTime.UtcNow ? o.status : Loud(a.Pid) ? "playing" : "paused") == "playing";
            if ((action == "play" && playing) || (action == "pause" && !playing)) return true;
            var key = action switch { "toggle" or "play" or "pause" => "playpause", "next" => "next", "prev" => "prev", _ => null };
            if (key == null) return false;
            if (key == "playpause") _override[a.Key] = (playing ? "paused" : "playing", DateTime.UtcNow + Grace);
            WinInput.Combo(key);
            _ = Task.Delay(300).ContinueWith(_ => Refresh());
            return true;
        }
        if (_window is { } w)
        {
            // Player hotkeys: Space play/pause, N next, P previous (VLC defaults).
            var playing = WindowStatus(w) == "playing";
            if ((action == "play" && playing) || (action == "pause" && !playing)) return true;
            var key = action switch { "toggle" or "play" or "pause" => "space", "next" => "n", "prev" => "p", "stop" => "s", _ => null };
            if (key == null) return false;
            // Show the new state straight away; audio detection takes over after the grace period.
            if (key == "space") _override[w.Key] = (playing ? "paused" : "playing", DateTime.UtcNow + Grace);
            WinShell.Focus(w.Hwnd);
            await Task.Delay(120);
            WinInput.Combo(key);
            _ = Task.Delay(200).ContinueWith(_ => Refresh());
            return true;
        }
        var s = _session;
        if (s == null) return false;
        var ok = action switch
        {
            "play" => await s.TryPlayAsync(),
            "pause" => await s.TryPauseAsync(),
            "toggle" => await s.TryTogglePlayPauseAsync(),
            "next" => await s.TrySkipNextAsync(),
            "prev" => await s.TrySkipPreviousAsync(),
            "stop" => await s.TryStopAsync(),
            "seek" when seconds != null => await s.TryChangePlaybackPositionAsync((long)(seconds.Value * 10_000_000)),
            _ => false,
        };
        _ = Task.Delay(250).ContinueWith(_ => Refresh());
        return ok;
    }
}
