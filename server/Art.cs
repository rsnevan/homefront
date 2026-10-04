using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Homefront;

/// Finds artwork for things that play without publishing any: songs (iTunes, then Deezer; no keys)
/// and VLC episodes/movies (your Jellyfin library). Results are cached per title.
public class ArtFinder
{
    readonly Jellyfin _jf;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly ConcurrentDictionary<string, (byte[] bytes, string type)?> _cache = new();

    public ArtFinder(Jellyfin jf)
    {
        _jf = jf;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("homefront/1.0");
    }

    public async Task<(byte[] bytes, string type)?> Find(bool video, string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{(video ? "v" : "m")}|{artist}|{title}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var hit)) return hit;
        (byte[], string)? result = null;
        try { result = video ? await FromJellyfin(title) : await FromItunes(title, artist) ?? await FromDeezer(title, artist); }
        catch (Exception e) { Log.Warn($"art lookup '{title}': {e.Message}"); }
        if (_cache.Count > 500) _cache.Clear();
        _cache[key] = result;
        return result;
    }

    // "Sweet Music (feat. Saara Maria)" -> "Sweet Music": featured artists and remix tags confuse searches.
    static string Clean(string s) => Regex.Replace(s, @"\s*[\(\[](feat\.?|ft\.?|with|prod\.?)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase).Trim();

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string Norm(string? s) => Regex.Replace(Clean(s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    // Confidence that a search hit is this song: the artist must match as a whole name (so "SiR" is not
    // "Sleeping With Sirens"), and the title must match too. 3 or more is good enough to show.
    static int Score(string? hitArtist, string? hitTitle, string title, string? artist)
    {
        var score = 0;
        if (artist != null && hitArtist != null)
        {
            var want = Norm(artist);
            var names = Regex.Split(hitArtist, @"\s*(?:,|&| and | x | feat\.? | ft\.? )\s*", RegexOptions.IgnoreCase).Select(Norm);
            if (Norm(hitArtist) == want) score += 2;
            else if (names.Contains(want)) score += 1;
            else return 0;
        }
        var t = Norm(title); var ht = Norm(hitTitle);
        if (ht == t) score += 2;
        else if (ht.StartsWith(t) || t.StartsWith(ht)) score += 1;
        else return 0;
        return score;
    }

    async Task<(byte[], string)?> FromItunes(string title, string? artist)
    {
        var term = Uri.EscapeDataString($"{artist} {Clean(title)}".Trim());
        var j = JsonDocument.Parse(await _http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=8")).RootElement;
        var best = j.GetProperty("results").EnumerateArray()
            .Select(r => (r, score: Score(Str(r, "artistName"), Str(r, "trackName"), title, artist)))
            .Where(x => x.score >= 3).OrderByDescending(x => x.score).Select(x => x.r).FirstOrDefault();
        if (best.ValueKind == JsonValueKind.Undefined || !best.TryGetProperty("artworkUrl100", out var art)) return null;
        return await Download(art.GetString()!.Replace("100x100bb", "600x600bb"));
    }

    async Task<(byte[], string)?> FromDeezer(string title, string? artist)
    {
        // Plain search: Deezer's strict artist:/track: syntax misses songs it finds this way; Score() rejects wrong hits.
        var q = Uri.EscapeDataString($"{artist} {Clean(title)}".Trim());
        var j = JsonDocument.Parse(await _http.GetStringAsync($"https://api.deezer.com/search?q={q}&limit=8")).RootElement;
        var best = j.GetProperty("data").EnumerateArray()
            .Select(r => (r, score: Score(r.TryGetProperty("artist", out var a) ? Str(a, "name") : null, Str(r, "title"), title, artist)))
            .Where(x => x.score >= 3).OrderByDescending(x => x.score).Select(x => x.r).FirstOrDefault();
        if (best.ValueKind == JsonValueKind.Undefined) return null;
        return await Download(best.GetProperty("album").GetProperty("cover_xl").GetString()!);
    }

    // "Bob Hearts Abishola S04E07" -> that episode's still (or the show's art); "Some Movie" -> its poster.
    async Task<(byte[], string)?> FromJellyfin(string title)
    {
        if (!_jf.Configured) return null;
        var ep = Regex.Match(title, @"^(?<show>.+?)\s+S(?<s>\d{1,2})E(?<e>\d{1,3})", RegexOptions.IgnoreCase);
        if (ep.Success)
        {
            var series = (await _jf.Find(ep.Groups["show"].Value, "Series")).FirstOrDefault();
            if (series == null) return null;
            var still = await _jf.EpisodeImageId(series, int.Parse(ep.Groups["s"].Value), int.Parse(ep.Groups["e"].Value));
            return await _jf.Image(still ?? series, still != null ? "Primary" : "Backdrop", 800) ?? await _jf.Image(series, "Primary", 600);
        }
        var movie = (await _jf.Find(title, "Movie")).FirstOrDefault();
        return movie == null ? null : await _jf.Image(movie, "Backdrop", 800) ?? await _jf.Image(movie, "Primary", 600);
    }

    async Task<(byte[], string)?> Download(string url)
    {
        using var res = await _http.GetAsync(url);
        if (!res.IsSuccessStatusCode) return null;
        return (await res.Content.ReadAsByteArrayAsync(), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }
}
