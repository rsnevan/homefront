using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homefront;

public class HomeAssistant
{
    static readonly HashSet<string> Domains = ["light", "media_player", "switch", "fan", "climate", "cover", "camera", "scene", "script", "input_boolean", "sun", "weather", "binary_sensor"];

    readonly ConfigStore _cfg;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    ClientWebSocket? _ws;
    int _nextId = 1;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);

    public ConcurrentDictionary<string, JsonElement> Entities { get; } = new();
    public bool Connected { get; private set; }
    public string? BaseUrl { get; private set; }
    public string? Version { get; private set; }
    public event Action<string, JsonElement?>? EntityChanged;
    public event Action<bool>? ConnectionChanged;
    public event Func<JsonElement, Task>? CommandReceived;

    public HomeAssistant(ConfigStore cfg) => _cfg = cfg;

    public void Start(CancellationToken ct) => _ = Task.Run(() => Loop(ct), ct);

    async Task Loop(CancellationToken ct)
    {
        var delay = 2;
        while (!ct.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(_cfg.Value.Ha.Token)) { await Task.Delay(5000, ct); continue; }
            foreach (var url in _cfg.Value.Ha.Urls)
            {
                try
                {
                    await Session(url, ct);
                    delay = 2;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    if (Connected) Log.Warn($"HA {url}: {e.Message}");
                }
                SetConnected(false);
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            delay = Math.Min(delay * 2, 30);
        }
    }

    async Task Session(string url, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        await ws.ConnectAsync(new Uri(url.Replace("http", "ws") + "/api/websocket"), cts.Token);
        _ws = ws;

        var hello = await Receive(ws, ct);
        await SendRaw(new JsonObject { ["type"] = "auth", ["access_token"] = _cfg.Value.Ha.Token });
        var auth = await Receive(ws, ct);
        if (auth?.GetProperty("type").GetString() != "auth_ok") throw new Exception("auth rejected");
        Version = auth.Value.TryGetProperty("ha_version", out var v) ? v.GetString() : null;
        BaseUrl = url;

        var pump = Task.Run(() => Pump(ws, ct), ct);
        var states = await Command(new JsonObject { ["type"] = "get_states" });
        Entities.Clear();
        foreach (var s in states.EnumerateArray()) if (Relevant(s.GetProperty("entity_id").GetString()!)) Entities[s.GetProperty("entity_id").GetString()!] = s.Clone();
        await Command(new JsonObject { ["type"] = "subscribe_events", ["event_type"] = "state_changed" });
        // Scripts and voice assistants in HA drive homefront by firing this event.
        await Command(new JsonObject { ["type"] = "subscribe_events", ["event_type"] = "homefront_command" });
        Log.Info($"HA connected at {url} (v{Version}), {Entities.Count} entities");
        SetConnected(true);
        await pump;
    }

    async Task Pump(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await Receive(ws, ct);
            if (msg == null) break;
            var m = msg.Value;
            var type = m.GetProperty("type").GetString();
            if (type == "result" && m.TryGetProperty("id", out var idEl) && _pending.TryRemove(idEl.GetInt32(), out var tcs))
            {
                if (m.GetProperty("success").GetBoolean()) tcs.TrySetResult(m.TryGetProperty("result", out var r) ? r.Clone() : default);
                else tcs.TrySetException(new Exception(m.GetProperty("error").GetProperty("message").GetString()));
            }
            else if (type == "event")
            {
                var ev = m.GetProperty("event");
                var data = ev.GetProperty("data");
                if (ev.GetProperty("event_type").GetString() == "homefront_command")
                {
                    var copy = data.Clone();
                    _ = Task.Run(() => CommandReceived?.Invoke(copy));
                    continue;
                }
                var id = data.GetProperty("entity_id").GetString()!;
                if (!Relevant(id)) continue;
                var ns = data.GetProperty("new_state");
                if (ns.ValueKind == JsonValueKind.Null) { Entities.TryRemove(id, out _); EntityChanged?.Invoke(id, null); }
                else { var c = ns.Clone(); Entities[id] = c; EntityChanged?.Invoke(id, c); }
            }
        }
        throw new Exception("socket closed");
    }

    static bool Relevant(string id) => Domains.Contains(id[..id.IndexOf('.')]);

    void SetConnected(bool v)
    {
        if (Connected == v) return;
        Connected = v;
        foreach (var t in _pending.Values) t.TrySetException(new Exception("disconnected"));
        _pending.Clear();
        ConnectionChanged?.Invoke(v);
    }

    public async Task<JsonElement> Command(JsonObject cmd)
    {
        var ws = _ws ?? throw new Exception("Home Assistant not connected");
        var id = Interlocked.Increment(ref _nextId);
        cmd["id"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await SendRaw(cmd);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(15000));
        if (done != tcs.Task) { _pending.TryRemove(id, out _); throw new Exception("Home Assistant timed out"); }
        return await tcs.Task;
    }

    public Task<JsonElement> CallService(string domain, string service, JsonNode? data = null, JsonNode? target = null)
    {
        var cmd = new JsonObject { ["type"] = "call_service", ["domain"] = domain, ["service"] = service };
        if (data != null) cmd["service_data"] = data;
        if (target != null) cmd["target"] = target;
        return Command(cmd);
    }

    async Task SendRaw(JsonObject obj)
    {
        var ws = _ws ?? throw new Exception("not connected");
        var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    static async Task<JsonElement?> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    // ---- REST (config flows: Tuya setup) ----

    public async Task<JsonElement> Rest(HttpMethod method, string path, object? body = null)
    {
        var url = BaseUrl ?? _cfg.Value.Ha.Urls.First();
        using var req = new HttpRequestMessage(method, url + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.Value.Ha.Token);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"HA {(int)res.StatusCode}: {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
