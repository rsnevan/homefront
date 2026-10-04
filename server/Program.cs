using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Homefront;
using QRCoder;

var baseDir = AppContext.BaseDirectory;
var dataDir = Path.Combine(baseDir, "data");
Directory.CreateDirectory(dataDir);
Log.Init(dataDir);

// One instance only: a second launch (login + manual start) just exits.
using var single = new Mutex(true, @"Local\homefront-server", out var first);
if (!first) return;

var cfg = new ConfigStore(dataDir);
var hub = new Hub();
var ha = new HomeAssistant(cfg);
var jf = new Jellyfin(cfg);
var media = new WinMedia();
var apps = new Apps(cfg, dataDir);
var feeds = new Feeds(cfg);
var lights = new Lights(ha, cfg);
var scenes = new Scenes(lights, cfg, apps, media);
var cinema = new Cinema(lights, cfg, media);
var player = new PlayerState();
cinema.PlayerStatus = () => player.Status;
string? tvEntityRef() => TvEntity();
var tvNotices = new TvNotices(apps, ha, tvEntityRef);
var sleepTimer = new SleepTimer(scenes, tvNotices);
var prayerWatch = new PrayerWatch(feeds, cfg, tvNotices, media, hub, apps, () => player.Status);
bool Watching() => player.Status == "playing" || (media.State.Active && media.State.IsVideo && media.State.Status == "playing");
var cameras = new Cameras(ha, cfg, tvNotices, hub, Watching);
var bridge = new HaBridge(ha, media, jf, sleepTimer, () => player.Status);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir, WebRootPath = Path.Combine(baseDir, "wwwroot") });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, cfg.Value.Port));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();
var stop = app.Lifetime.ApplicationStopping;

// ---------------- auth gate ----------------

string[] open = ["/api/login", "/api/me", "/g/"];
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    var who = Auth.Resolve(ctx, cfg.Value);
    ctx.Items["who"] = who;
    var needsAuth = path.StartsWith("/api/") || path.StartsWith("/jf/");
    if (needsAuth && who == null && !open.Any(path.StartsWith)) { ctx.Response.StatusCode = 401; return; }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

Principal Who(HttpContext c) => (Principal)c.Items["who"]!;
IResult OwnerOnly(HttpContext c) => Results.Json(new { error = "Only the owner can do that" }, statusCode: 403);

// ---------------- state ----------------

object Snapshot(Principal who) => new
{
    me = new { role = who.Role, name = who.Name },
    home = new { name = cfg.Value.HomeName, owner = cfg.Value.OwnerName },
    ha = new { connected = ha.Connected, version = ha.Version, url = ha.BaseUrl },
    entities = ha.Entities.ToDictionary(e => e.Key, e => e.Value),
    rooms = RoomsView(),
    scenes = Scenes.All,
    media = media.State,
    volume = VolumeView(),
    stats = Stats.Current,
    weather = feeds.Weather,
    prayer = feeds.Prayer,
    cinema = new { mode = cinema.Mode, enabled = cfg.Value.Cinema.Enabled, rooms = cfg.Value.Cinema.Rooms },
    kiosk = apps.KioskPage,
    player = player.View(),
    jellyfin = new { ok = jf.Configured },
    tvEntity = TvEntity(),
    shortcuts = cfg.Value.Shortcuts,
    screen = new { w = WinScreen.Size.w, h = WinScreen.Size.h },
    timer = sleepTimer.View(),
    cameras = cameras.Ids.ToList(),
};

object RoomsView()
{
    var map = lights.Rooms();
    var rooms = cfg.Value.Rooms.Select(r => new { name = r.Name, icon = r.Icon, lights = map.GetValueOrDefault(r.Name) ?? [] }).ToList();
    if (map.TryGetValue("Other", out var other) && other.Count > 0) rooms.Add(new { name = "Other", icon = "lamp", lights = other });
    return rooms;
}

object VolumeView() { var (l, m) = WinAudio.Get(); return new { level = l, muted = m }; }

string? TvEntity()
{
    var conf = cfg.Value.Ha.TvEntity;
    if (!string.IsNullOrEmpty(conf) && ha.Entities.ContainsKey(conf)) return conf;
    return ha.Entities.Keys.FirstOrDefault(k => k.StartsWith("media_player.lg_webos")) ?? ha.Entities.Keys.FirstOrDefault(k => k.StartsWith("media_player.") && k.Contains("tv"));
}

ha.EntityChanged += (id, s) =>
{
    hub.Broadcast("entity", new { id, state = s });
    if (id.StartsWith("light.") && s == null) hub.Broadcast("rooms", RoomsView());
};
ha.ConnectionChanged += c => { hub.Broadcast("ha", new { connected = c, version = ha.Version, url = ha.BaseUrl }); if (c) { hub.Broadcast("entities", ha.Entities.ToDictionary(e => e.Key, e => e.Value)); hub.Broadcast("rooms", RoomsView()); hub.Broadcast("tvEntity", TvEntity() ?? ""); } };
media.Changed += m => hub.Broadcast("media", m);
feeds.Changed += () => hub.Broadcast("feeds", new { weather = feeds.Weather, prayer = feeds.Prayer });
cinema.ModeChanged += m => hub.Broadcast("cinema", new { mode = m, enabled = cfg.Value.Cinema.Enabled, rooms = cfg.Value.Cinema.Rooms });
apps.KioskChanged += k => { hub.Broadcast("kiosk", k ?? ""); if (k != "player") { player.Clear(); hub.Broadcast("player", player.View()); cinema.Evaluate(); } };
cfg.Changed += () => hub.Broadcast("config", new { home = new { name = cfg.Value.HomeName, owner = cfg.Value.OwnerName }, shortcuts = cfg.Value.Shortcuts, rooms = RoomsView() });

// ---------------- live socket ----------------

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Map("/api/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
    var kind = ctx.Request.Query["kind"] == "player" ? "player" : "dashboard";
    if (kind == "player" && !Auth.IsLocal(ctx)) return Results.StatusCode(403);
    var who = Who(ctx);
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.Run(ws, who, kind, () => Task.FromResult(Snapshot(who)), stop);
    return Results.Empty;
});

hub.InputReceived += (who, m) =>
{
    double D(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    string S(string k) => m.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
    switch (S("t"))
    {
        case "move": WinInput.MoveBy(D("dx"), D("dy")); break;
        case "moveTo": WinInput.MoveTo(D("x"), D("y")); break;
        case "click": WinInput.Click(S("b") == "right" ? "right" : "left", m.TryGetProperty("dbl", out var d) && d.GetBoolean()); break;
        case "tapAt": WinInput.MoveTo(D("x"), D("y")); WinInput.Click(); break;
        case "button": WinInput.Button(S("b"), m.GetProperty("down").GetBoolean()); break;
        case "scroll": WinInput.Scroll(D("dy"), D("dx")); break;
        case "key": WinInput.Combo(S("k")); break;
        case "type": WinInput.Type(S("s")); break;
    }
    return Task.CompletedTask;
};

// ---------------- auth endpoints ----------------

app.MapPost("/api/login", async (HttpContext ctx, LoginReq req) =>
{
    var a = cfg.Value.Auth;
    if (!string.Equals(req.Username?.Trim(), a.Username, StringComparison.OrdinalIgnoreCase) || !Auth.VerifyPassword(req.Password ?? "", a.PasswordHash))
    {
        await Task.Delay(700);
        return Results.Json(new { error = "That username and password don't match" }, statusCode: 401);
    }
    var life = TimeSpan.FromDays(365);
    Auth.SetCookie(ctx, Auth.Issue(cfg.Value, "owner", null, life), life);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/logout", (HttpContext ctx) => { ctx.Response.Cookies.Delete(Auth.Cookie); return Results.Ok(); });
app.MapGet("/api/me", (HttpContext ctx) => ctx.Items["who"] is Principal p ? Results.Ok(new { role = p.Role, name = p.Name, home = cfg.Value.HomeName }) : Results.Json(new { role = (string?)null, home = cfg.Value.HomeName }));

app.MapGet("/g/{token}", (HttpContext ctx, string token) =>
{
    var p = Auth.ResolveToken(token, cfg.Value);
    if (p == null || p.Role != "guest") return Results.Redirect("/?guest=expired");
    var pass = cfg.Value.Auth.Guests.First(g => g.Id == p.GuestId);
    var life = pass.Expires is { } e ? e - DateTimeOffset.UtcNow : TimeSpan.FromDays(365);
    Auth.SetCookie(ctx, token, life);
    return Results.Redirect("/");
});

// ---------------- guests ----------------

app.MapGet("/api/guests", (HttpContext ctx) => Who(ctx).IsOwner ? Results.Ok(cfg.Value.Auth.Guests) : OwnerOnly(ctx));
app.MapPost("/api/guests", (HttpContext ctx, GuestReq req) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    var pass = new GuestPass
    {
        Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
        Name = string.IsNullOrWhiteSpace(req.Name) ? "Guest" : req.Name.Trim(),
        Created = DateTimeOffset.UtcNow,
        Expires = req.Hours is > 0 ? DateTimeOffset.UtcNow.AddHours(req.Hours.Value) : null,
    };
    cfg.Update(c => c.Auth.Guests.Add(pass));
    var token = Auth.Issue(cfg.Value, "guest", pass.Id, pass.Expires is { } e ? e - DateTimeOffset.UtcNow : TimeSpan.FromDays(365));
    return Results.Ok(new { pass, path = "/g/" + token });
});
app.MapPost("/api/guests/{id}/toggle", (HttpContext ctx, string id) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    cfg.Update(c => { var g = c.Auth.Guests.FirstOrDefault(x => x.Id == id); if (g != null) g.Enabled = !g.Enabled; });
    return Results.Ok(cfg.Value.Auth.Guests);
});
app.MapDelete("/api/guests/{id}", (HttpContext ctx, string id) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    cfg.Update(c => c.Auth.Guests.RemoveAll(x => x.Id == id));
    return Results.Ok(cfg.Value.Auth.Guests);
});

app.MapGet("/api/qr.svg", (string data) =>
{
    using var gen = new QRCodeGenerator();
    using var qr = gen.CreateQrCode(data, QRCodeGenerator.ECCLevel.Q);
    return Results.Text(new SvgQRCode(qr).GetGraphic(8, "#0f1726", "#f3f6fa", drawQuietZones: true), "image/svg+xml");
});

// ---------------- Home Assistant ----------------

app.MapPost("/api/ha/call", async (HaCall c) =>
{
    var allowed = new[] { "light", "media_player", "switch", "fan", "scene", "script", "input_boolean", "cover", "climate" };
    if (!allowed.Contains(c.Domain)) return Results.BadRequest(new { error = "domain not allowed" });
    await ha.CallService(c.Domain, c.Service, c.Data == null ? null : JsonNode.Parse(c.Data.Value.GetRawText()), c.Target == null ? null : JsonNode.Parse(c.Target.Value.GetRawText()));
    return Results.Ok();
});

app.MapPost("/api/lights/room", async (RoomLightReq r) =>
{
    var ids = lights.InRooms([r.Room]);
    if (r.On == false) await lights.Set(ids, false);
    else await lights.Set(ids, true, r.Brightness, r.Kelvin);
    return Results.Ok();
});

app.MapPost("/api/scene/{id}", async (string id) => { await scenes.Run(id); return Results.Ok(); });

// ---------------- TV ----------------

app.MapPost("/api/tv/{action}", async (string action, TvReq? r) =>
{
    var tv = TvEntity();
    JsonObject Target() => new() { ["entity_id"] = tv };
    switch (action)
    {
        case "on": await apps.Tv("-poweron"); break;
        case "off": await apps.Tv("-poweroff"); break;
        case "screen_off": await apps.Tv("-screenoff"); break;
        case "screen_on": await apps.Tv("-screenon"); break;
        case "pc": await apps.Tv($"-sethdmi {cfg.Value.Pc.TvPcInput}"); break;
        case "volume" when r?.Level != null && tv != null:
            await ha.CallService("media_player", "volume_set", new JsonObject { ["volume_level"] = Math.Clamp(r.Level.Value, 0, 100) / 100.0 }, Target()); break;
        case "volume" when r?.Level != null:
            await apps.Tv($"-volume {Math.Clamp(r.Level.Value, 0, 100)}"); break;
        case "mute" when tv != null:
            await ha.CallService("media_player", "volume_mute", new JsonObject { ["is_volume_muted"] = r?.Muted ?? true }, Target()); break;
        case "source" when tv != null && r?.Source != null:
            await ha.CallService("media_player", "select_source", new JsonObject { ["source"] = r.Source }, Target()); break;
        default: return Results.BadRequest(new { error = "unknown action" });
    }
    return Results.Ok();
});

// ---------------- PC ----------------

app.MapGet("/api/pc/screen.jpg", (int? w) =>
{
    var bytes = WinScreen.Capture(Math.Clamp(w ?? 960, 160, 1920));
    return Results.Bytes(bytes, "image/jpeg");
});

app.MapGet("/api/pc/art", (HttpContext ctx) =>
{
    if (media.Art == null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.Bytes(media.Art, media.ArtType ?? "image/png");
});

// Diagnostics: which apps are making sound right now (owner only).
app.MapGet("/api/pc/audio", (HttpContext ctx) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    return Results.Ok(WinAudio.SessionPeaks().Select(kv =>
    {
        string name; try { name = System.Diagnostics.Process.GetProcessById(kv.Key).ProcessName; } catch { name = "?"; }
        return new { pid = kv.Key, process = name, peak = Math.Round(kv.Value, 4) };
    }));
});

app.MapPost("/api/pc/media/{action}", async (string action, SeekReq? r) => Results.Ok(new { ok = await media.Control(action, r?.Position) }));

app.MapPost("/api/pc/volume", (VolReq r) =>
{
    if (r.Level != null) WinAudio.Set(r.Level.Value);
    if (r.Muted != null) WinAudio.Mute(r.Muted.Value);
    var v = VolumeView();
    hub.Broadcast("volume", v);
    return Results.Ok(v);
});

app.MapPost("/api/pc/open", async (OpenReq r) => { await apps.OpenUrl(r.Url); return Results.Ok(); });

app.MapPost("/api/pc/power/{action}", (HttpContext ctx, string action) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    switch (action)
    {
        case "sleep": _ = Task.Delay(1500).ContinueWith(_ => WinShell.Sleep()); break;
        case "lock": WinShell.Lock(); break;
        case "restart": WinShell.Restart(); break;
        default: return Results.BadRequest();
    }
    return Results.Ok();
});

app.MapPost("/api/kiosk/{page}", async (string page) =>
{
    switch (page)
    {
        case "ambient": await apps.OpenKiosk("ambient", "/ambient"); break;
        case "close": await apps.CloseKiosk(); break;
        default: return Results.BadRequest();
    }
    return Results.Ok();
});

// ---------------- Jellyfin ----------------

app.MapGet("/api/jf/home", async () => jf.Configured ? Results.Ok(await jf.Home()) : Results.Ok(new { }));
app.MapGet("/api/jf/browse", async (string type, int? start, int? limit, string? q) => Results.Ok(await jf.Browse(type == "Series" ? "Series" : "Movie", start ?? 0, Math.Clamp(limit ?? 60, 1, 200), q)));
app.MapGet("/api/jf/search", async (string q) => Results.Ok(await jf.Search(q)));
app.MapGet("/api/jf/item/{id}", async (string id) => Results.Ok(await jf.Item(id)));
app.MapGet("/api/jf/episodes/{series}/{season}", async (string series, string season) => Results.Ok(await jf.Episodes(series, season)));

app.MapGet("/api/jf/img/{id}/{type}", async (HttpContext ctx, string id, string type, int? w) =>
{
    var t = type is "Primary" or "Backdrop" or "Thumb" or "Logo" ? type : "Primary";
    using var res = await jf.Raw.GetAsync($"{cfg.Value.Jellyfin.Url}/Items/{id}/Images/{t}?fillWidth={Math.Clamp(w ?? 400, 50, 1920)}&quality=85", stop);
    if (!res.IsSuccessStatusCode) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.Bytes(await res.Content.ReadAsByteArrayAsync(), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
});

// HLS for the kiosk player is proxied so the browser only ever talks to homefront.
app.Map("/jf/{**path}", async (HttpContext ctx, string path) =>
{
    var url = $"{cfg.Value.Jellyfin.Url}/{path}{ctx.Request.QueryString}";
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.TryAddWithoutValidation("Authorization", jf.AuthHeader);
    if (ctx.Request.Headers.Range.Count > 0) req.Headers.TryAddWithoutValidation("Range", ctx.Request.Headers.Range.ToString());
    using var res = await jf.Raw.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    ctx.Response.StatusCode = (int)res.StatusCode;
    foreach (var h in res.Content.Headers) if (h.Key is "Content-Type" or "Content-Length" or "Content-Range") ctx.Response.Headers[h.Key] = h.Value.ToArray();
    ctx.Response.Headers.CacheControl = "no-store";
    await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

// ---------------- player (homefront kiosk on the HTPC) ----------------

app.MapPost("/api/jf/play", async (PlayReq r) =>
{
    if (!jf.Configured) return Results.BadRequest(new { error = "Jellyfin isn't connected" });
    player.Pending = r.Id;
    player.StartAt = r.FromStart == true ? 0 : null;
    if (apps.KioskPage == "player" && hub.PlayerCount > 0) hub.ToPlayers("load", new { id = r.Id, fromStart = r.FromStart == true });
    else await apps.OpenKiosk("player", "/player?id=" + Uri.EscapeDataString(r.Id) + (r.FromStart == true ? "&start=0" : ""));
    _ = apps.Tv($"-sethdmi {cfg.Value.Pc.TvPcInput}");
    return Results.Ok();
});

app.MapGet("/api/player/source", async (string id, int? audio, int? burn) =>
    Results.Ok(await jf.PlaybackSource(id, audio, burn, cfg.Value.Player.SubtitleLanguage, cfg.Value.Player.SubtitlesOn)));

app.MapPost("/api/player/{cmd}", async (string cmd, PlayerCmd? r) =>
{
    if (cmd == "stop") { await apps.CloseKiosk(); return Results.Ok(); }
    hub.ToPlayers("cmd", new { cmd, position = r?.Position, index = r?.Index });
    return Results.Ok();
});

hub.PlayerReport += async m =>
{
    var t = m.GetProperty("t").GetString();
    if (t == "state")
    {
        var prev = player.Status;
        player.Update(m);
        hub.Broadcast("player", player.View());
        if (prev != player.Status) cinema.Evaluate();
        if (player.ItemId == null || player.MediaSourceId == null || player.PlaySessionId == null) return;
        var evt = prev == "stopped" && player.Status != "stopped" ? "start"
                : player.Status == "stopped" && prev != "stopped" ? "stop"
                : (DateTime.UtcNow - player.LastReport).TotalSeconds > 10 || prev != player.Status ? "progress" : null;
        if (evt == null) return;
        player.LastReport = DateTime.UtcNow;
        try { await jf.Report(evt, player.ItemId, player.MediaSourceId, player.PlaySessionId, player.Position, player.Status == "paused"); }
        catch (Exception e) { Log.Warn($"jf report: {e.Message}"); }
    }
    else if (t == "ended" && player.ItemId != null)
    {
        var next = await jf.NextEpisode(player.ItemId);
        if (next != null) hub.ToPlayers("next", new { id = next });
        else await apps.CloseKiosk();
    }
};

// ---------------- sleep timer ----------------

app.MapPost("/api/timer", (TimerReq r) => { sleepTimer.Set(r.Minutes); return Results.Ok(sleepTimer.View()); });
sleepTimer.Changed += () => hub.Broadcast("timer", sleepTimer.View());

// ---------------- cameras (via Home Assistant) ----------------

app.MapGet("/api/camera/{id}/snapshot", async (HttpContext ctx, string id) =>
{
    if (!id.StartsWith("camera.") || !ha.Entities.ContainsKey(id)) return Results.NotFound();
    var (bytes, type) = await cameras.SnapshotRaw(id);
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Bytes(bytes, type);
});

app.MapGet("/api/camera/{id}/stream", async (HttpContext ctx, string id) =>
{
    if (!id.StartsWith("camera.") || !ha.Entities.ContainsKey(id)) { ctx.Response.StatusCode = 404; return; }
    using var res = await cameras.StreamOf($"/api/camera_proxy_stream/{id}", ct: ctx.RequestAborted);
    ctx.Response.ContentType = res.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace";
    ctx.Response.Headers.CacheControl = "no-store";
    try { await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted); } catch (OperationCanceledException) { }
});

// ---------------- commands from Home Assistant (scripts, Assist, Siri via the HA app) ----------------

ha.CommandReceived += async d =>
{
    string S(string k) => d.TryGetProperty(k, out var v) ? v.ToString() : "";
    Log.Info($"HA command: {d.GetRawText()}");
    try
    {
        switch (S("command"))
        {
            case "scene": await scenes.Run(S("scene")); break;
            case "sleep_timer": sleepTimer.Set(int.TryParse(S("minutes"), out var m) ? m : null); break;
            case "ambient": await apps.OpenKiosk("ambient", "/ambient"); break;
            case "close_kiosk": await apps.CloseKiosk(); break;
            case "open": await apps.OpenUrl(S("url")); break;
            case "pause": await media.Control("pause"); hub.ToPlayers("cmd", new { cmd = "pause" }); break;
            case "play": await media.Control("play"); hub.ToPlayers("cmd", new { cmd = "play" }); break;
        }
    }
    catch (Exception e) { Log.Warn($"HA command failed: {e.Message}"); }
};

// ---------------- settings ----------------

app.MapGet("/api/settings", async (HttpContext ctx) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    var c = cfg.Value;
    return Results.Ok(new
    {
        homeName = c.HomeName, ownerName = c.OwnerName, username = c.Auth.Username,
        location = c.Location, prayer = c.Prayer, cinema = c.Cinema, rooms = c.Rooms, shortcuts = c.Shortcuts, player = c.Player, camera = c.Camera,
        tvEntity = c.Ha.TvEntity, tvPcInput = c.Pc.TvPcInput,
        lights = lights.All.Select(l => new { id = l.GetProperty("entity_id").GetString(), name = l.GetProperty("attributes").TryGetProperty("friendly_name", out var n) ? n.GetString() : null }),
        status = new { ha = ha.Connected, haUrl = ha.BaseUrl, haVersion = ha.Version, jellyfin = await jf.Ping() },
    });
});

app.MapPost("/api/settings", (HttpContext ctx, JsonElement body) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    T? Get<T>(string k) => body.TryGetProperty(k, out var v) ? v.Deserialize<T>(o) : default;
    cfg.Update(c =>
    {
        if (Get<string>("homeName") is { Length: > 0 } hn) c.HomeName = hn.Trim();
        if (Get<string>("ownerName") is { Length: > 0 } on) c.OwnerName = on.Trim();
        if (Get<LocationConfig>("location") is { } l) c.Location = l;
        if (Get<PrayerConfig>("prayer") is { } p) c.Prayer = p;
        if (Get<PlayerConfig>("player") is { } pl) c.Player = pl;
        if (Get<CameraConfig>("camera") is { } cam) c.Camera = cam;
        if (Get<CinemaConfig>("cinema") is { } ci) c.Cinema = ci;
        if (Get<List<Room>>("rooms") is { } r) c.Rooms = r.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        if (Get<List<Homefront.Shortcut>>("shortcuts") is { } s) c.Shortcuts = s.Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Url)).ToList();
        if (Get<int?>("tvPcInput") is { } i) c.Pc.TvPcInput = i;
    });
    hub.Broadcast("cinema", new { mode = cinema.Mode, enabled = cfg.Value.Cinema.Enabled, rooms = cfg.Value.Cinema.Rooms });
    return Results.Ok();
});

app.MapPost("/api/settings/password", (HttpContext ctx, PasswordReq r) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    if (!Auth.VerifyPassword(r.Current ?? "", cfg.Value.Auth.PasswordHash)) return Results.Json(new { error = "Current password is wrong" }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(r.Next)) return Results.Json(new { error = "New password can't be empty" }, statusCode: 400);
    cfg.Update(c => { c.Auth.PasswordHash = Auth.HashPassword(r.Next); if (!string.IsNullOrWhiteSpace(r.Username)) c.Auth.Username = r.Username.Trim(); });
    return Results.Ok();
});

// ---------------- light setup (Tuya / Smart Life via Home Assistant) ----------------

app.MapPost("/api/setup/tuya/start", async (HttpContext ctx, TuyaStart r) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    var flow = await ha.Rest(HttpMethod.Post, "/api/config/config_entries/flow", new { handler = "tuya", show_advanced_options = false });
    var id = flow.GetProperty("flow_id").GetString()!;
    var step = await ha.Rest(HttpMethod.Post, $"/api/config/config_entries/flow/{id}", new { user_code = r.UserCode.Trim() });
    return Results.Ok(TuyaView(id, step));
});

app.MapPost("/api/setup/tuya/poll", async (HttpContext ctx, TuyaPoll r) =>
{
    if (!Who(ctx).IsOwner) return OwnerOnly(ctx);
    var step = await ha.Rest(HttpMethod.Post, $"/api/config/config_entries/flow/{r.FlowId}", new { });
    return Results.Ok(TuyaView(r.FlowId, step));
});

object TuyaView(string flowId, JsonElement step)
{
    var type = step.GetProperty("type").GetString();
    if (type == "create_entry") return new { flowId, status = "done", title = step.TryGetProperty("title", out var t) ? t.GetString() : "Tuya" };
    if (type == "abort") return new { flowId, status = "error", error = step.TryGetProperty("reason", out var rs) ? rs.GetString() : "aborted" };
    string? qr = null;
    if (step.TryGetProperty("data_schema", out var schema) && schema.ValueKind == JsonValueKind.Array)
        foreach (var f in schema.EnumerateArray())
            if (f.TryGetProperty("selector", out var sel) && sel.TryGetProperty("qr_code", out var q) && q.TryGetProperty("data", out var d)) qr = d.GetString();
    var err = step.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Any() ? e.EnumerateObject().First().Value.GetString() : null;
    var stepId = step.TryGetProperty("step_id", out var s) ? s.GetString() : null;
    return new { flowId, status = stepId == "user" ? "code" : "scan", qr, error = err == "login_error" ? null : err };
}

// ---------------- static app ----------------

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
    {
        var p = c.Context.Request.Path.Value ?? "";
        c.Context.Response.Headers.CacheControl = p.StartsWith("/vendor/") || p.StartsWith("/icons/") ? "public, max-age=604800" : "no-cache";
    },
});
app.MapFallbackToFile("index.html");

// ---------------- background loops ----------------

ha.Start(stop);
feeds.Start(stop);
await media.Start();

_ = Task.Run(async () =>
{
    object? lastVol = null;
    var tick = 0;
    while (!stop.IsCancellationRequested)
    {
        try
        {
            var v = VolumeView();
            var vs = JsonSerializer.Serialize(v);
            if (vs != (lastVol as string)) { lastVol = vs; hub.Broadcast("volume", v); }
            if (tick++ % 2 == 0) { Stats.Sample(); hub.Broadcast("stats", Stats.Current); }
            await sleepTimer.Tick();
            if (tick % 10 == 0) await prayerWatch.Tick();
            await bridge.Tick(Stats.Current);
            await media.Refresh();
        }
        catch (Exception e) { Log.Warn($"loop: {e.Message}"); }
        await Task.Delay(1500, stop);
    }
}, stop);

Log.Info($"homefront listening on :{cfg.Value.Port}");
app.Run();

// ---------------- request/model types ----------------

record LoginReq(string? Username, string? Password);
record GuestReq(string? Name, int? Hours);
record HaCall(string Domain, string Service, JsonElement? Data, JsonElement? Target);
record RoomLightReq(string Room, bool? On, int? Brightness, int? Kelvin);
record TvReq(int? Level, bool? Muted, string? Source);
record SeekReq(double? Position);
record PlayerCmd(double? Position, int? Index);
record TimerReq(int? Minutes);
record VolReq(int? Level, bool? Muted);
record OpenReq(string Url);
record PlayReq(string Id, bool? FromStart);
record PasswordReq(string? Current, string? Next, string? Username);
record TuyaStart(string UserCode);
record TuyaPoll(string FlowId);

class PlayerState
{
    public string Status = "stopped";
    public string? ItemId, MediaSourceId, PlaySessionId, Title, Subtitle, Pending;
    public double Position, Duration;
    public double? StartAt;
    public string? ImageId;
    public DateTime LastReport = DateTime.MinValue;

    public JsonElement? Tracks;
    public void Update(JsonElement m)
    {
        if (m.TryGetProperty("tracks", out var tr)) Tracks = tr.Clone();
        string? S(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double N(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        Status = S("status") ?? "stopped";
        ItemId = S("itemId"); MediaSourceId = S("mediaSourceId"); PlaySessionId = S("playSessionId");
        Title = S("title"); Subtitle = S("subtitle"); ImageId = S("imageId");
        Position = N("position"); Duration = N("duration");
    }

    public void Clear() { Status = "stopped"; ItemId = null; Title = null; Subtitle = null; Position = Duration = 0; Tracks = null; }

    public object View() => new { status = Status, itemId = ItemId, title = Title, subtitle = Subtitle, imageId = ImageId, position = Position, duration = Duration, tracks = Tracks, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
}

static class Stats
{
    public static object Current { get; private set; } = new { };
    static readonly DateTime Boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static void Sample()
    {
        var (used, total) = WinShell.Memory();
        var (title, proc) = WinShell.Foreground();
        Current = new
        {
            cpu = WinShell.Cpu(),
            memUsed = used, memTotal = total,
            uptime = (DateTime.Now - Boot).TotalSeconds,
            disks = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => new { name = d.Name.TrimEnd('\\'), free = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1), total = Math.Round(d.TotalSize / 1073741824.0, 1) }),
            foreground = new { title, process = proc },
        };
    }
}
