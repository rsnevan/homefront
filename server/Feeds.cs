using System.Globalization;
using System.Text.Json;

namespace Homefront;

/// Weather (Open-Meteo, no key) and prayer times (Aladhan), cached.
public class Feeds
{
    readonly ConfigStore _cfg;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public object? Weather { get; private set; }
    public object? Prayer { get; private set; }
    public event Action? Changed;

    public Feeds(ConfigStore cfg) { _cfg = cfg; cfg.Changed += () => _ = RefreshAll(); }

    public void Start(CancellationToken ct) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAll();
            await Task.Delay(TimeSpan.FromMinutes(15), ct);
        }
    }, ct);

    DateOnly _prayerDay;
    double _lat, _lon;

    public async Task RefreshAll()
    {
        var loc = _cfg.Value.Location;
        var moved = loc.Lat != _lat || loc.Lon != _lon;
        _lat = loc.Lat; _lon = loc.Lon;
        await Task.WhenAll(RefreshWeather(), RefreshPrayer(moved));
        Changed?.Invoke();
    }

    async Task RefreshWeather()
    {
        var l = _cfg.Value.Location;
        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={l.Lat}&longitude={l.Lon}") +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day,precipitation" +
            "&hourly=temperature_2m,weather_code,precipitation_probability,is_day&forecast_hours=24" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max,uv_index_max" +
            "&timezone=auto&forecast_days=6";
        try
        {
            var j = JsonDocument.Parse(await _http.GetStringAsync(url)).RootElement;
            var c = j.GetProperty("current");
            var h = j.GetProperty("hourly");
            var d = j.GetProperty("daily");
            Weather = new
            {
                place = l.Name,
                now = new
                {
                    temp = c.GetProperty("temperature_2m").GetDouble(),
                    feels = c.GetProperty("apparent_temperature").GetDouble(),
                    humidity = c.GetProperty("relative_humidity_2m").GetDouble(),
                    wind = c.GetProperty("wind_speed_10m").GetDouble(),
                    code = c.GetProperty("weather_code").GetInt32(),
                    isDay = c.GetProperty("is_day").GetInt32() == 1,
                    rain = c.GetProperty("precipitation").GetDouble(),
                },
                hourly = Enumerable.Range(0, h.GetProperty("time").GetArrayLength()).Select(i => new
                {
                    time = h.GetProperty("time")[i].GetString(),
                    temp = h.GetProperty("temperature_2m")[i].GetDouble(),
                    code = h.GetProperty("weather_code")[i].GetInt32(),
                    rain = h.GetProperty("precipitation_probability")[i].ValueKind == JsonValueKind.Number ? h.GetProperty("precipitation_probability")[i].GetInt32() : 0,
                    isDay = h.GetProperty("is_day")[i].GetInt32() == 1,
                }).ToList(),
                daily = Enumerable.Range(0, d.GetProperty("time").GetArrayLength()).Select(i => new
                {
                    date = d.GetProperty("time")[i].GetString(),
                    code = d.GetProperty("weather_code")[i].GetInt32(),
                    max = d.GetProperty("temperature_2m_max")[i].GetDouble(),
                    min = d.GetProperty("temperature_2m_min")[i].GetDouble(),
                    rain = d.GetProperty("precipitation_probability_max")[i].ValueKind == JsonValueKind.Number ? d.GetProperty("precipitation_probability_max")[i].GetInt32() : 0,
                    sunrise = d.GetProperty("sunrise")[i].GetString(),
                    sunset = d.GetProperty("sunset")[i].GetString(),
                    uv = d.GetProperty("uv_index_max")[i].ValueKind == JsonValueKind.Number ? d.GetProperty("uv_index_max")[i].GetDouble() : 0,
                }).ToList(),
                updated = DateTimeOffset.Now,
            };
        }
        catch (Exception e) { Log.Warn($"weather: {e.Message}"); }
    }

    async Task RefreshPrayer(bool force)
    {
        var p = _cfg.Value.Prayer;
        if (!p.Enabled) { Prayer = null; return; }
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (!force && _prayerDay == today && Prayer != null) return;
        try
        {
            var days = await Task.WhenAll(FetchDay(today), FetchDay(today.AddDays(1)));
            Prayer = new { method = p.Method, sehriOffset = p.SehriOffsetMinutes, today = days[0], tomorrow = days[1] };
            _prayerDay = today;
        }
        catch (Exception e) { Log.Warn($"prayer: {e.Message}"); }
    }

    async Task<object> FetchDay(DateOnly day)
    {
        var l = _cfg.Value.Location;
        var url = FormattableString.Invariant(
            $"https://api.aladhan.com/v1/timings/{day:dd-MM-yyyy}?latitude={l.Lat}&longitude={l.Lon}&method={_cfg.Value.Prayer.Method}");
        var j = JsonDocument.Parse(await _http.GetStringAsync(url)).RootElement.GetProperty("data");
        var t = j.GetProperty("timings");
        var hijri = j.GetProperty("date").GetProperty("hijri");
        string T(string k) => t.GetProperty(k).GetString()![..5];
        return new
        {
            date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            fajr = T("Fajr"), sunrise = T("Sunrise"), dhuhr = T("Dhuhr"), asr = T("Asr"), maghrib = T("Maghrib"), isha = T("Isha"),
            hijri = new
            {
                day = int.Parse(hijri.GetProperty("day").GetString()!),
                month = hijri.GetProperty("month").GetProperty("number").GetInt32(),
                monthName = hijri.GetProperty("month").GetProperty("en").GetString(),
                year = int.Parse(hijri.GetProperty("year").GetString()!),
            },
        };
    }
}
