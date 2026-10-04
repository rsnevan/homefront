using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homefront;

public class Lights
{
    readonly HomeAssistant _ha;
    readonly ConfigStore _cfg;
    public Lights(HomeAssistant ha, ConfigStore cfg) { _ha = ha; _cfg = cfg; }

    public IEnumerable<JsonElement> All => _ha.Entities.Where(e => e.Key.StartsWith("light.")).Select(e => e.Value);

    static string Name(JsonElement e) =>
        e.GetProperty("attributes").TryGetProperty("friendly_name", out var n) ? n.GetString() ?? "" : e.GetProperty("entity_id").GetString()!;

    /// Explicit assignments win; otherwise a light belongs to the room its name mentions.
    public Dictionary<string, List<string>> Rooms()
    {
        var map = new Dictionary<string, List<string>>();
        var assigned = new HashSet<string>();
        foreach (var r in _cfg.Value.Rooms) { map[r.Name] = r.Lights.Where(_ha.Entities.ContainsKey).ToList(); assigned.UnionWith(map[r.Name]); }
        foreach (var l in All)
        {
            var id = l.GetProperty("entity_id").GetString()!;
            if (assigned.Contains(id)) continue;
            var name = Name(l);
            var room = _cfg.Value.Rooms.FirstOrDefault(r => r.Lights.Count == 0 &&
                (name.Contains(r.Name, StringComparison.OrdinalIgnoreCase) || id.Contains(r.Name.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase)));
            var key = room?.Name ?? "Other";
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(id);
        }
        return map;
    }

    public List<string> InRooms(IEnumerable<string> rooms)
    {
        var map = Rooms();
        return rooms.SelectMany(r => map.TryGetValue(r, out var l) ? l : []).Distinct().ToList();
    }

    public bool IsOn(string id) => _ha.Entities.TryGetValue(id, out var e) && e.GetProperty("state").GetString() == "on";

    public async Task Set(IEnumerable<string> ids, bool on, int? brightnessPct = null, int? kelvin = null, double transition = 1.5, JsonNode? extra = null)
    {
        var list = ids.Where(_ha.Entities.ContainsKey).ToList();
        if (list.Count == 0) return;
        if (!on)
        {
            await _ha.CallService("light", "turn_off", new JsonObject { ["transition"] = transition }, new JsonObject { ["entity_id"] = new JsonArray(list.Select(x => (JsonNode)x!).ToArray()) });
            return;
        }
        // Group lights by what they support so one bad attribute doesn't fail the whole call.
        foreach (var group in list.GroupBy(id => SupportsCt(id)))
        {
            var data = new JsonObject { ["transition"] = transition };
            if (brightnessPct != null) data["brightness_pct"] = brightnessPct;
            if (kelvin != null && group.Key) data["color_temp_kelvin"] = kelvin;
            if (extra is JsonObject x) foreach (var kv in x) data[kv.Key] = kv.Value?.DeepClone();
            await _ha.CallService("light", "turn_on", data, new JsonObject { ["entity_id"] = new JsonArray(group.Select(i => (JsonNode)i!).ToArray()) });
        }
    }

    bool SupportsCt(string id) =>
        _ha.Entities.TryGetValue(id, out var e) && e.GetProperty("attributes").TryGetProperty("supported_color_modes", out var m) &&
        m.ValueKind == JsonValueKind.Array && m.EnumerateArray().Any(x => x.GetString() is "color_temp" or "rgbww" or "rgbw");

    public record Snapshot(string Id, bool On, int? Brightness, int? Kelvin, JsonNode? Hs);

    public Snapshot Capture(string id)
    {
        var e = _ha.Entities[id];
        var a = e.GetProperty("attributes");
        int? I(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;
        var mode = a.TryGetProperty("color_mode", out var cm) ? cm.GetString() : null;
        JsonNode? hs = mode is "hs" or "xy" or "rgb" && a.TryGetProperty("hs_color", out var h) && h.ValueKind == JsonValueKind.Array ? JsonNode.Parse(h.GetRawText()) : null;
        return new Snapshot(id, e.GetProperty("state").GetString() == "on", I("brightness"), mode == "color_temp" ? I("color_temp_kelvin") : null, hs);
    }

    public async Task Restore(IEnumerable<Snapshot> snaps)
    {
        foreach (var s in snaps)
        {
            if (!_ha.Entities.ContainsKey(s.Id)) continue;
            if (!s.On) { await Set([s.Id], false, transition: 2); continue; }
            var data = new JsonObject { ["transition"] = 2 };
            if (s.Brightness != null) data["brightness"] = s.Brightness;
            if (s.Kelvin != null) data["color_temp_kelvin"] = s.Kelvin;
            else if (s.Hs != null) data["hs_color"] = s.Hs.DeepClone();
            await _ha.CallService("light", "turn_on", data, new JsonObject { ["entity_id"] = s.Id });
        }
    }
}

public class Scenes
{
    public record SceneDef(string Id, string Name, string Icon, string Description);
    public static readonly SceneDef[] All =
    [
        new("movie", "Movie", "popcorn", "TV on, PC input, cinema rooms dimmed warm"),
        new("evening", "Evening", "sun-dim", "Every light at a soft 50%"),
        new("bright", "Bright", "sun", "Full, neutral white"),
        new("off", "Lights off", "lightbulb-off", "Every light off"),
        new("goodnight", "Goodnight", "moon-star", "Pause, TV off, lights off"),
    ];

    readonly Lights _lights; readonly ConfigStore _cfg; readonly Apps _apps; readonly WinMedia _media;
    public Scenes(Lights lights, ConfigStore cfg, Apps apps, WinMedia media) { _lights = lights; _cfg = cfg; _apps = apps; _media = media; }

    public async Task Run(string id)
    {
        var all = _lights.All.Select(e => e.GetProperty("entity_id").GetString()!).ToList();
        switch (id)
        {
            case "movie":
                var cinema = _lights.InRooms(_cfg.Value.Cinema.Rooms);
                await Task.WhenAll(
                    _lights.Set(cinema, true, _cfg.Value.Cinema.PlayingBrightness + 4, 2200, 3),
                    _lights.Set(all.Except(cinema), false, transition: 3),
                    TvOnToPc());
                break;
            case "evening": await _lights.Set(all, true, 50, 2700, 2); break;
            case "bright": await _lights.Set(all, true, 100, 4000, 1); break;
            case "off": await _lights.Set(all, false, transition: 2); break;
            case "goodnight":
                await _media.Control("pause");
                await _apps.CloseKiosk();
                await _lights.Set(all, false, transition: 4);
                await _apps.Tv("-poweroff");
                break;
            default: throw new ArgumentException("unknown scene");
        }
    }

    async Task TvOnToPc()
    {
        await _apps.Tv("-poweron");
        await _apps.Tv($"-sethdmi {_cfg.Value.Pc.TvPcInput}");
    }
}

/// Lights follow what's playing on the HTPC.
public class Cinema
{
    readonly Lights _lights; readonly ConfigStore _cfg; readonly WinMedia _media;
    List<Lights.Snapshot>? _saved;
    string _applied = "idle";
    string _pending = "idle";
    CancellationTokenSource? _debounce;
    public string Mode => _applied;
    public event Action<string>? ModeChanged;
    public Func<string>? PlayerStatus;   // homefront kiosk player: "playing" | "paused" | "stopped"

    public Cinema(Lights lights, ConfigStore cfg, WinMedia media)
    {
        _lights = lights; _cfg = cfg; _media = media;
        media.Changed += _ => Evaluate();
    }

    public void Evaluate()
    {
        var player = PlayerStatus?.Invoke() ?? "stopped";
        var m = _media.State;
        var want = player != "stopped" ? player
                 : m.Active && m.IsVideo && m.Status is "playing" or "paused" ? m.Status
                 : "idle";
        if (want == _pending) return;
        _pending = want;
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        // Stopping waits longer so skipping between episodes doesn't bounce the lights.
        var wait = want == "idle" ? 8000 : 1500;
        _ = Task.Delay(wait, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _ = Apply(want); });
    }

    public async Task Apply(string want)
    {
        var c = _cfg.Value.Cinema;
        if (!c.Enabled && want != "idle") return;
        if (want == _applied) return;
        try
        {
            var ids = _lights.InRooms(c.Rooms);
            if (want == "idle")
            {
                if (_saved != null) await _lights.Restore(_saved);
                _saved = null;
            }
            else
            {
                if (_saved == null)
                {
                    var on = ids.Where(_lights.IsOn).ToList();
                    if (on.Count == 0) { SetMode("idle"); return; }   // daytime / lights already off: leave them alone
                    _saved = on.Select(_lights.Capture).ToList();
                }
                var target = _saved.Select(s => s.Id).ToList();
                await _lights.Set(target, true, want == "playing" ? c.PlayingBrightness : c.PausedBrightness, 2200, want == "playing" ? 3 : 1.5);
            }
            SetMode(want);
        }
        catch (Exception e) { Log.Warn($"cinema: {e.Message}"); }
    }

    void SetMode(string m) { _applied = m; ModeChanged?.Invoke(m); }
}
