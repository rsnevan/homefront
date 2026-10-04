using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homefront;

public class AppConfig
{
    public string HomeName { get; set; } = "Home";
    public string OwnerName { get; set; } = "";
    public int Port { get; set; } = 80;

    public AuthConfig Auth { get; set; } = new();
    public HaConfig Ha { get; set; } = new();
    public JellyfinConfig Jellyfin { get; set; } = new();
    public LocationConfig Location { get; set; } = new();
    public PrayerConfig Prayer { get; set; } = new();
    public CinemaConfig Cinema { get; set; } = new();
    public PcConfig Pc { get; set; } = new();

    public List<Room> Rooms { get; set; } =
    [
        new() { Name = "Living Room", Icon = "sofa" },
        new() { Name = "Kitchen", Icon = "utensils" },
        new() { Name = "Bedroom", Icon = "bed-double" },
        new() { Name = "Study", Icon = "book-open" },
    ];

    public List<Shortcut> Shortcuts { get; set; } =
    [
        new() { Name = "Netflix",  Url = "https://www.netflix.com/browse", Color = "#e50914" },
        new() { Name = "YouTube",  Url = "https://www.youtube.com/tv",       Color = "#ff0033" },
        new() { Name = "DStv",     Url = "https://www.dstv.com/stream",      Color = "#0094ff" },
        new() { Name = "Showmax",  Url = "https://www.showmax.com",          Color = "#ff2d55" },
        new() { Name = "Disney+",  Url = "https://www.disneyplus.com",       Color = "#1f80e0" },
        new() { Name = "Prime",    Url = "https://www.primevideo.com",       Color = "#00a8e1" },
        new() { Name = "Apple TV", Url = "https://tv.apple.com",             Color = "#a2aaad" },
        new() { Name = "Spotify",  Url = "app:spotify",                      Color = "#1ed760" },
    ];
}

public class AuthConfig
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
    public string Secret { get; set; } = "";
    public List<GuestPass> Guests { get; set; } = [];
}

public class GuestPass
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public bool Enabled { get; set; } = true;
}

public class HaConfig
{
    // Tried in order; the first that answers wins (mDNS name survives DHCP changes).
    public List<string> Urls { get; set; } = ["http://homeassistant.local:8123", "http://homeassistant.local"];
    public string Token { get; set; } = "";
    public string TvEntity { get; set; } = "";
}

public class JellyfinConfig
{
    public string Url { get; set; } = "http://127.0.0.1:8096";
    public string ApiKey { get; set; } = "";
    public string UserId { get; set; } = "";
}

public class LocationConfig
{
    public string Name { get; set; } = "Johannesburg";
    public double Lat { get; set; } = -26.2;
    public double Lon { get; set; } = 28.05;
}

public class PrayerConfig
{
    public bool Enabled { get; set; } = true;
    public int Method { get; set; } = 1;          // Aladhan: 1 = University of Islamic Sciences, Karachi
    public int SehriOffsetMinutes { get; set; } = 5;
}

public class CinemaConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> Rooms { get; set; } = ["Living Room"];
    public int PlayingBrightness { get; set; } = 8;
    public int PausedBrightness { get; set; } = 35;
}

public class PcConfig
{
    public string Browser { get; set; } = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
    public string LgtvCli { get; set; } = @"C:\Program Files\LGTV Companion\LGTVcli.exe";
    public int TvPcInput { get; set; } = 3;
}

public class Room
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "lamp";
    public List<string> Lights { get; set; } = [];
}

public class Shortcut
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "#ffc94d";
}

public class ConfigStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    readonly string _path;
    readonly object _lock = new();
    public AppConfig Value { get; private set; }
    public event Action? Changed;

    public ConfigStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "config.json");
        Value = File.Exists(_path)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Json) ?? new()
            : new();
        if (string.IsNullOrEmpty(Value.Auth.Secret))
            Value.Auth.Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        if (string.IsNullOrEmpty(Value.Auth.PasswordHash))
            Value.Auth.PasswordHash = Auth.HashPassword("homefront");   // first-run default; change it in Settings
        Save();
    }

    public void Update(Action<AppConfig> change)
    {
        lock (_lock)
        {
            change(Value);
            Save();
        }
        Changed?.Invoke();
    }

    void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
