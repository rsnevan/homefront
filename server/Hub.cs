using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Homefront;

/// Live channel to every open dashboard. Server pushes typed messages; clients send pointer/keyboard input.
public class Hub
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    class Client
    {
        public required WebSocket Socket;
        public required Principal Who;
        public required string Kind;            // "dashboard" | "player"
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    readonly ConcurrentDictionary<Guid, Client> _clients = new();
    public event Func<Principal, JsonElement, Task>? InputReceived;
    public event Func<JsonElement, Task>? PlayerReport;

    public int PlayerCount => _clients.Values.Count(c => c.Kind == "player");

    public async Task Run(WebSocket ws, Principal who, string kind, Func<Task<object>> snapshot, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var client = new Client { Socket = ws, Who = who, Kind = kind };
        _clients[id] = client;
        try
        {
            if (kind == "dashboard") await Send(client, new { t = "snapshot", d = await snapshot() });
            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                JsonElement msg;
                try { msg = JsonDocument.Parse(ms.ToArray()).RootElement; } catch { continue; }
                try
                {
                    if (kind == "player") { if (PlayerReport != null) await PlayerReport(msg); }
                    else if (InputReceived != null) await InputReceived(who, msg);
                }
                catch (Exception e) { Log.Warn($"ws handler: {e.Message}"); }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        }
    }

    public void Broadcast(string type, object data, Func<Principal, bool>? filter = null) =>
        _ = BroadcastAsync(type, data, "dashboard", filter);

    public void ToPlayers(string type, object data) => _ = BroadcastAsync(type, data, "player", null);

    async Task BroadcastAsync(string type, object data, string kind, Func<Principal, bool>? filter)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = type, d = data }, Json));
        foreach (var c in _clients.Values)
        {
            if (c.Kind != kind || (filter != null && !filter(c.Who))) continue;
            await SendRaw(c, bytes);
        }
    }

    static Task Send(Client c, object msg) => SendRaw(c, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, Json)));

    static async Task SendRaw(Client c, byte[] bytes)
    {
        if (c.Socket.State != WebSocketState.Open) return;
        if (!await c.SendLock.WaitAsync(2000)) return;
        try { await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { c.SendLock.Release(); }
    }
}

public static class Log
{
    static readonly object L = new();
    static string? _file;
    public static void Init(string dir) => _file = Path.Combine(dir, "homefront.log");
    public static void Info(string m) => Write("INF", m);
    public static void Warn(string m) => Write("WRN", m);
    static void Write(string lvl, string m)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lvl} {m}";
        Console.WriteLine(line);
        if (_file == null) return;
        lock (L)
        {
            try
            {
                if (File.Exists(_file) && new FileInfo(_file).Length > 2_000_000) File.Move(_file, _file + ".1", true);
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
