using System.Security.Cryptography;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Homefront;

public record MediaState(
    bool Active, string? App, string? AppName, string? Title, string? Artist, string? Album,
    string Status, bool IsVideo, double Position, double Duration, long UpdatedAt,
    string? ArtVersion, bool CanPrev, bool CanNext, bool CanSeek);

/// Windows' global media session: whatever is playing on the HTPC (Spotify, Brave tabs, players).
public class WinMedia
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly SemaphoreSlim _refresh = new(1, 1);
    public MediaState State { get; private set; } = Empty;
    string? _winTitle;
    bool _winPaused;
    public byte[]? Art { get; private set; }
    public string? ArtType { get; private set; }
    public event Action<MediaState>? Changed;

    static readonly MediaState Empty = new(false, null, null, null, null, null, "stopped", false, 0, 0, 0, null, false, false, false);

    public async Task Start()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => { Attach(); _ = Refresh(); };
            _mgr.SessionsChanged += (_, _) => { Attach(); _ = Refresh(); };
            Attach();
            await Refresh();
            Log.Info("Media session watcher started");
        }
        catch (Exception e) { Log.Warn($"Media sessions unavailable: {e.Message}"); }
    }

    void Attach()
    {
        var s = PickSession();
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

    // Prefer a session that's actually playing over Windows' notion of "current".
    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_mgr == null) return null;
        var all = _mgr.GetSessions();
        return all.FirstOrDefault(x => x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               ?? _mgr.GetCurrentSession() ?? all.FirstOrDefault();
    }

    void OnChange(GlobalSystemMediaTransportControlsSession s, object _) { Attach(); _ = Refresh(); }

    public async Task Refresh()
    {
        if (!await _refresh.WaitAsync(3000)) return;
        try
        {
            Attach();
            var s = _session;
            var idle = s == null || s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (idle && PlayerWindow() is { } win)
            {
                // VLC (and friends) don't publish a media session; read the window instead.
                Art = null;
                if (win.title != _winTitle) { _winTitle = win.title; _winPaused = false; }
                Publish(new MediaState(true, "window:" + win.proc, win.app, win.title, null, null, _winPaused ? "paused" : "playing", true, 0, 0,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, true, true, false));
                return;
            }
            if (s == null) { Publish(Empty); Art = null; return; }

            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
            try { props = await s.TryGetMediaPropertiesAsync(); } catch { }

            var status = info?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                _ => "stopped",
            };
            var app = s.SourceAppUserModelId ?? "";
            var isMusicApp = app.Contains("spotify", StringComparison.OrdinalIgnoreCase);
            var isVideo = info?.PlaybackType == Windows.Media.MediaPlaybackType.Video || (!isMusicApp && info?.PlaybackType != Windows.Media.MediaPlaybackType.Music);

            string? artVersion = State.ArtVersion;
            if (props?.Thumbnail != null && (props.Title != State.Title || Art == null))
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
        catch (Exception e) { Log.Warn($"media refresh: {e.Message}"); }
        finally { _refresh.Release(); }
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

    static (string proc, string app, string title, IntPtr hwnd)? PlayerWindow()
    {
        foreach (var (proc, suffix, app) in WindowPlayers)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
            {
                var t = p.MainWindowTitle;
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && t.Length > suffix.Length)
                    return (proc, app, Tidy(t[..^suffix.Length]), p.MainWindowHandle);
            }
        return null;
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

    public async Task<bool> Control(string action, double? seconds = null)
    {
        if (State.App?.StartsWith("window:") == true && PlayerWindow() is { } win)
        {
            // Player hotkeys: Space play/pause, N next, P previous (VLC defaults).
            if ((action == "play" && !_winPaused) || (action == "pause" && _winPaused)) return true;
            var key = action switch { "toggle" or "play" or "pause" => "space", "next" => "n", "prev" => "p", "stop" => "s", _ => null };
            if (key == null) return false;
            if (key == "space") _winPaused = !_winPaused;
            if (key is "n" or "p") _winPaused = false;
            _ = Task.Delay(200).ContinueWith(_ => Refresh());
            WinShell.Focus(win.hwnd);
            await Task.Delay(120);
            WinInput.Combo(key);
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
