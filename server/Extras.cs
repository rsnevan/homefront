using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homefront;

/// "Off in 45 minutes": runs the Goodnight scene when it expires, with a one-minute warning on the TV.
public class SleepTimer
{
    readonly Scenes _scenes; readonly TvNotices _tv;
    public DateTimeOffset? EndsAt { get; private set; }
    bool _warned;
    public event Action? Changed;

    public SleepTimer(Scenes scenes, TvNotices tv) { _scenes = scenes; _tv = tv; }

    public void Set(int? minutes)
    {
        EndsAt = minutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes.Value) : null;
        _warned = false;
        Changed?.Invoke();
    }

    public object View() => new { endsAt = EndsAt?.ToUnixTimeMilliseconds() };

    public async Task Tick()
    {
        if (EndsAt is not { } end) return;
        var left = end - DateTimeOffset.UtcNow;
        if (!_warned && left <= TimeSpan.FromSeconds(60))
        {
            _warned = true;
            await _tv.Show("Turning off in 1 minute. Open homefront to cancel the sleep timer.");
        }
        if (left <= TimeSpan.Zero)
        {
            EndsAt = null;
            Changed?.Invoke();
            Log.Info("Sleep timer: Goodnight");
            try { await _scenes.Run("goodnight"); } catch (Exception e) { Log.Warn($"sleep timer: {e.Message}"); }
        }
    }
}

/// Notifications drawn by the TV itself, on top of whatever input is showing.
public class TvNotices
{
    readonly Apps _apps; readonly HomeAssistant _ha; readonly Func<string?> _tvEntity;
    public TvNotices(Apps apps, HomeAssistant ha, Func<string?> tvEntity) { _apps = apps; _ha = ha; _tvEntity = tvEntity; }

    public bool TvOn => _tvEntity() is { } id && _ha.Entities.TryGetValue(id, out var e) && e.GetProperty("state").GetString() is "on" or "playing" or "paused" or "idle";

    public async Task Show(string message, byte[]? jpeg = null)
    {
        if (!TvOn) return;
        var payload = new JsonObject { ["message"] = message };
        if (jpeg != null) { payload["iconData"] = Convert.ToBase64String(jpeg); payload["iconExtension"] = "jpg"; }
        var json = payload.ToJsonString().Replace("\"", "\\\"");
        // Windows caps a command line at 32K characters; never let a picture stop the message.
        if (json.Length > 30000) json = new JsonObject { ["message"] = message }.ToJsonString().Replace("\"", "\\\"");
        try { await _apps.Tv($"-request_with_param system.notifications/createToast \"{json}\""); }
        catch (Exception e) { Log.Warn($"tv notice: {e.Message}"); }
    }
}

/// Prayer-time notices, optional pause, and the Ramadan Iftar countdown on the TV.
public class PrayerWatch
{
    readonly Feeds _feeds; readonly ConfigStore _cfg; readonly TvNotices _tv; readonly WinMedia _media; readonly Hub _hub;
    readonly Apps _apps; readonly Func<string> _playerStatus;
    readonly HashSet<string> _fired = [];

    public PrayerWatch(Feeds feeds, ConfigStore cfg, TvNotices tv, WinMedia media, Hub hub, Apps apps, Func<string> playerStatus)
    { _feeds = feeds; _cfg = cfg; _tv = tv; _media = media; _hub = hub; _apps = apps; _playerStatus = playerStatus; }

    static readonly (string key, string name)[] Prayers = [("fajr", "Fajr"), ("dhuhr", "Dhuhr"), ("asr", "Asr"), ("maghrib", "Maghrib"), ("isha", "Isha")];

    public async Task Tick()
    {
        var p = _cfg.Value.Prayer;
        if (!p.Enabled || _feeds.Prayer == null || !(p.TvNotice || p.PauseAtPrayer || p.IftarOnTv)) return;
        var today = JsonSerializer.SerializeToElement(_feeds.Prayer, Hub.Json).GetProperty("today");
        var now = DateTime.Now;
        var hhmm = now.ToString("HH:mm");
        var playing = _playerStatus() == "playing" || (_media.State.Active && _media.State.Status == "playing");

        foreach (var (key, name) in Prayers)
        {
            var at = today.GetProperty(key).GetString();
            var id = $"{now:yyyyMMdd}-{key}";
            if (at != hhmm || !_fired.Add(id)) continue;
            Log.Info($"Prayer time: {name}");
            if (p.PauseAtPrayer && playing)
            {
                await _media.Control("pause");
                _hub.ToPlayers("cmd", new { cmd = "pause" });
            }
            if (p.TvNotice) await _tv.Show($"It's time for {name} ({at}).");
        }

        // Ramadan: 15 minutes before Maghrib, show the Iftar countdown (or a notice if something's playing).
        var ramadan = today.GetProperty("hijri").GetProperty("month").GetInt32() == 9;
        if (ramadan && p.IftarOnTv && TimeOnly.TryParse(today.GetProperty("maghrib").GetString(), out var maghrib))
        {
            var cue = maghrib.AddMinutes(-15).ToString("HH:mm");
            if (cue == hhmm && _fired.Add($"{now:yyyyMMdd}-iftar"))
            {
                if (playing) await _tv.Show($"Iftar in 15 minutes ({maghrib:HH\\:mm}).");
                else if (_tv.TvOn) await _apps.OpenKiosk("ambient", "/ambient");
            }
        }
        if (_fired.Count > 40) _fired.RemoveWhere(x => !x.StartsWith($"{now:yyyyMMdd}"));
    }
}

/// Cameras from Home Assistant: snapshots, live MJPEG, and motion alerts on the TV while watching.
public class Cameras
{
    readonly HomeAssistant _ha; readonly ConfigStore _cfg; readonly TvNotices _tv; readonly Hub _hub;
    readonly Func<bool> _watching;

    public Cameras(HomeAssistant ha, ConfigStore cfg, TvNotices tv, Hub hub, Func<bool> watching)
    {
        _ha = ha; _cfg = cfg; _tv = tv; _hub = hub; _watching = watching;
        ha.EntityChanged += OnEntity;
    }

    public IEnumerable<string> Ids => _ha.Entities.Keys.Where(k => k.StartsWith("camera."));

    DateTime _lastAlert = DateTime.MinValue;

    void OnEntity(string id, JsonElement? s)
    {
        if (s == null || !id.StartsWith("binary_sensor.")) return;
        var st = s.Value;
        if (st.GetProperty("state").GetString() != "on") return;
        var attrs = st.GetProperty("attributes");
        var cls = attrs.TryGetProperty("device_class", out var dc) ? dc.GetString() : null;
        if (cls is not ("motion" or "occupancy" or "presence")) return;
        if (DateTime.UtcNow - _lastAlert < TimeSpan.FromSeconds(45)) return;
        _lastAlert = DateTime.UtcNow;
        var name = attrs.TryGetProperty("friendly_name", out var fn) ? fn.GetString() ?? "Camera" : "Camera";
        var camera = CameraFor(id);
        _hub.Broadcast("motion", new { sensor = id, name, camera });
        if (_cfg.Value.Camera.MotionOnTv && _watching()) _ = AlertTv(name, camera);
    }

    // Motion sensors and cameras from the same device share a name prefix, e.g. camera.front / binary_sensor.front_motion.
    string? CameraFor(string sensorId)
    {
        var stem = sensorId["binary_sensor.".Length..];
        return Ids.OrderByDescending(c => CommonPrefix(c["camera.".Length..], stem)).FirstOrDefault();
        static int CommonPrefix(string a, string b) { var i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i; }
    }

    async Task AlertTv(string name, string? camera)
    {
        byte[]? icon = null;
        if (camera != null) try { icon = Thumb(await Snapshot(camera), 160); } catch { }
        await _tv.Show($"{name.Replace(" Motion", "", StringComparison.OrdinalIgnoreCase)}: motion detected", icon);
    }

    public async Task<(byte[] bytes, string type)> SnapshotRaw(string id)
    {
        using var res = await StreamOf($"/api/camera_proxy/{id}", HttpCompletionOption.ResponseContentRead);
        return (await res.Content.ReadAsByteArrayAsync(), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    async Task<byte[]> Snapshot(string id) => (await SnapshotRaw(id)).bytes;

    public async Task<HttpResponseMessage> StreamOf(string path, HttpCompletionOption mode = HttpCompletionOption.ResponseHeadersRead, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, (_ha.BaseUrl ?? _cfg.Value.Ha.Urls.First()) + path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _cfg.Value.Ha.Token);
        var res = await Http.SendAsync(req, mode, ct);
        res.EnsureSuccessStatusCode();
        return res;
    }

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static byte[] Thumb(byte[] jpeg, int width)
    {
        using var src = Image.FromStream(new MemoryStream(jpeg));
        var h = (int)(width * (double)src.Height / src.Width);
        using var bmp = new Bitmap(width, h);
        using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, width, h); }
        // Small JPEG: it travels to the TV inside a command-line argument.
        var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var p = new System.Drawing.Imaging.EncoderParameters(1);
        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
        using var ms = new MemoryStream();
        bmp.Save(ms, enc, p);
        return ms.ToArray();
    }
}

/// Publishes HTPC state into Home Assistant (for automations and phone alerts) and announces new library items.
public class HaBridge
{
    readonly HomeAssistant _ha; readonly WinMedia _media; readonly Jellyfin _jf; readonly SleepTimer _timer; readonly Func<string> _playerStatus;
    DateTime _lastPublish = DateTime.MinValue, _lastLibraryCheck = DateTime.MinValue;
    DateTimeOffset? _newestSeen;
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;   // HA wants "7.1", not "7,1"

    public HaBridge(HomeAssistant ha, WinMedia media, Jellyfin jf, SleepTimer timer, Func<string> playerStatus)
    {
        _ha = ha; _media = media; _jf = jf; _timer = timer; _playerStatus = playerStatus;
        media.Changed += m => { _ = PublishPlaying(); };
        timer.Changed += () => _ = PublishTimer();
    }

    Task State(string entity, string state, object attributes) =>
        _ha.Connected ? _ha.Rest(HttpMethod.Post, $"/api/states/{entity}", new { state, attributes }) : Task.CompletedTask;

    async Task PublishPlaying()
    {
        try
        {
            var m = _media.State;
            var on = _playerStatus() == "playing" || (m.Active && m.Status == "playing");
            await State("binary_sensor.homefront_playing", on ? "on" : "off", new { friendly_name = "HTPC playing", device_class = "running", title = m.Title, app = m.AppName, video = m.IsVideo });
        }
        catch (Exception e) { Log.Warn($"bridge playing: {e.Message}"); }
    }

    async Task PublishTimer()
    {
        try
        {
            var mins = _timer.EndsAt is { } end ? (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalMinutes) : 0;
            await State("sensor.homefront_sleep_timer", mins.ToString(Inv), new { friendly_name = "Sleep timer", unit_of_measurement = "min", icon = "mdi:timer-outline" });
        }
        catch (Exception e) { Log.Warn($"bridge timer: {e.Message}"); }
    }

    public async Task Tick(object stats)
    {
        if (!_ha.Connected) return;
        if (DateTime.UtcNow - _lastPublish >= TimeSpan.FromSeconds(60))
        {
            _lastPublish = DateTime.UtcNow;
            try
            {
                var s = JsonSerializer.SerializeToElement(stats, Hub.Json);
                await State("sensor.homefront_htpc_cpu", s.GetProperty("cpu").GetDouble().ToString("0", Inv), new { friendly_name = "HTPC CPU", unit_of_measurement = "%", state_class = "measurement", icon = "mdi:cpu-64-bit" });
                await State("sensor.homefront_htpc_memory", s.GetProperty("memUsed").GetDouble().ToString("0.0", Inv), new { friendly_name = "HTPC memory used", unit_of_measurement = "GB", state_class = "measurement", icon = "mdi:memory" });
                foreach (var d in s.GetProperty("disks").EnumerateArray())
                {
                    var letter = d.GetProperty("name").GetString()!.TrimEnd(':').ToLowerInvariant();
                    await State($"sensor.homefront_disk_{letter}_free", d.GetProperty("free").GetDouble().ToString("0.0", Inv), new { friendly_name = $"HTPC {letter.ToUpper()}: free", unit_of_measurement = "GB", state_class = "measurement", icon = "mdi:harddisk" });
                }
                await State("sensor.homefront_heartbeat", DateTimeOffset.UtcNow.ToString("o"), new { friendly_name = "homefront heartbeat", device_class = "timestamp" });
                await PublishPlaying();
                await PublishTimer();
            }
            catch (Exception e) { Log.Warn($"bridge publish: {e.Message}"); }
        }
        if (_jf.Configured && DateTime.UtcNow - _lastLibraryCheck >= TimeSpan.FromMinutes(10))
        {
            _lastLibraryCheck = DateTime.UtcNow;
            await CheckLibrary();
        }
    }

    async Task CheckLibrary()
    {
        try
        {
            var recent = await _jf.RecentlyAdded(40);
            var newest = recent.Count > 0 ? recent.Max(r => r.added) : (DateTimeOffset?)null;
            if (_newestSeen == null) { _newestSeen = newest ?? DateTimeOffset.UtcNow; return; }   // first run: baseline only
            var fresh = recent.Where(r => r.added > _newestSeen).ToList();
            if (newest > _newestSeen) _newestSeen = newest;
            if (fresh.Count == 0) return;
            foreach (var g in fresh.GroupBy(r => r.series ?? r.title))
            {
                var list = g.ToList();
                var msg = list[0].series != null
                    ? (list.Count == 1 ? $"New episode of {g.Key}: {list[0].title}" : $"{list.Count} new episodes of {g.Key}")
                    : $"New movie: {g.Key}";
                await _ha.Rest(HttpMethod.Post, "/api/events/homefront_new_media", new { message = msg, title = g.Key, count = list.Count });
                Log.Info($"New media: {msg}");
            }
        }
        catch (Exception e) { Log.Warn($"library check: {e.Message}"); }
    }
}
