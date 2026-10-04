using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homefront;

public class Jellyfin
{
    const string Fields = "Overview,PrimaryImageAspectRatio,MediaSources,Genres,ProductionYear,OfficialRating,CommunityRating,RunTimeTicks,UserData,SeriesInfo,ParentId,People";
    readonly ConfigStore _cfg;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    public HttpClient Raw { get; } = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public Jellyfin(ConfigStore cfg) => _cfg = cfg;

    string Url => _cfg.Value.Jellyfin.Url.TrimEnd('/');
    string User => _cfg.Value.Jellyfin.UserId;
    public bool Configured => !string.IsNullOrEmpty(_cfg.Value.Jellyfin.ApiKey) && !string.IsNullOrEmpty(User);

    public string AuthHeader =>
        $"MediaBrowser Client=\"homefront\", Device=\"HTPC\", DeviceId=\"homefront-htpc\", Version=\"1.0\", Token=\"{_cfg.Value.Jellyfin.ApiKey}\"";

    async Task<JsonElement> Get(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url + path);
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader);
        using var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public async Task Post(string path, object? body = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url + path);
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader);
        req.Content = new StringContent(body == null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task<bool> Ping()
    {
        try { await Get("/System/Info/Public"); return true; } catch { return false; }
    }

    public async Task<object> Home()
    {
        var resume = Get($"/Users/{User}/Items/Resume?Limit=12&MediaTypes=Video&Fields={Fields}&EnableImageTypes=Primary,Backdrop,Thumb");
        var nextUp = Get($"/Shows/NextUp?UserId={User}&Limit=12&Fields={Fields}&EnableImageTypes=Primary,Backdrop,Thumb");
        var movies = Get($"/Users/{User}/Items/Latest?IncludeItemTypes=Movie&Limit=16&Fields={Fields}");
        var shows = Get($"/Users/{User}/Items/Latest?IncludeItemTypes=Episode&Limit=20&GroupItems=true&Fields={Fields}");
        await Task.WhenAll(resume, nextUp, movies, shows);
        return new
        {
            resume = Slim(resume.Result.GetProperty("Items")),
            nextUp = Slim(nextUp.Result.GetProperty("Items")),
            movies = Slim(movies.Result),
            shows = Slim(shows.Result),
        };
    }

    public async Task<object> Browse(string type, int start, int limit, string? search)
    {
        var q = $"/Users/{User}/Items?IncludeItemTypes={type}&Recursive=true&SortBy=SortName&SortOrder=Ascending&StartIndex={start}&Limit={limit}&Fields={Fields}";
        if (!string.IsNullOrWhiteSpace(search)) q += "&SearchTerm=" + Uri.EscapeDataString(search);
        var r = await Get(q);
        return new { items = Slim(r.GetProperty("Items")), total = r.GetProperty("TotalRecordCount").GetInt32() };
    }

    public async Task<object> Search(string term)
    {
        var r = await Get($"/Users/{User}/Items?SearchTerm={Uri.EscapeDataString(term)}&IncludeItemTypes=Movie,Series,Episode&Recursive=true&Limit=30&Fields={Fields}");
        return Slim(r.GetProperty("Items"));
    }

    public async Task<object> Item(string id)
    {
        var item = await Get($"/Users/{User}/Items/{id}?Fields={Fields}");
        object? seasons = null;
        if (item.GetProperty("Type").GetString() == "Series")
            seasons = Slim((await Get($"/Shows/{id}/Seasons?UserId={User}&Fields={Fields}")).GetProperty("Items"));
        return new { item = SlimOne(item), seasons };
    }

    public async Task<object> Episodes(string seriesId, string seasonId) =>
        Slim((await Get($"/Shows/{seriesId}/Episodes?SeasonId={seasonId}&UserId={User}&Fields={Fields}")).GetProperty("Items"));

    public async Task<JsonElement> RawItem(string id) => await Get($"/Users/{User}/Items/{id}?Fields={Fields}");

    public async Task<List<(string title, string? series, DateTimeOffset added)>> RecentlyAdded(int limit)
    {
        var r = await Get($"/Users/{User}/Items?SortBy=DateCreated&SortOrder=Descending&IncludeItemTypes=Movie,Episode&Recursive=true&Limit={limit}&Fields=DateCreated");
        return r.GetProperty("Items").EnumerateArray()
            .Where(i => i.TryGetProperty("DateCreated", out _))
            .Select(i => (i.GetProperty("Name").GetString() ?? "",
                          i.TryGetProperty("SeriesName", out var s) ? s.GetString() : null,
                          DateTimeOffset.Parse(i.GetProperty("DateCreated").GetString()!)))
            .ToList();
    }

    /// Next episode after this one (for autoplay), or null.
    public async Task<string?> NextEpisode(string episodeId)
    {
        var ep = await RawItem(episodeId);
        if (ep.GetProperty("Type").GetString() != "Episode") return null;
        var seriesId = ep.GetProperty("SeriesId").GetString();
        var r = await Get($"/Shows/{seriesId}/Episodes?UserId={User}&StartItemId={episodeId}&Limit=2");
        var items = r.GetProperty("Items");
        return items.GetArrayLength() > 1 ? items[1].GetProperty("Id").GetString() : null;
    }

    static List<object> Slim(JsonElement arr) => arr.EnumerateArray().Select(SlimOne).ToList();

    static object SlimOne(JsonElement i)
    {
        string? S(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? N(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        double? D(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        long? L(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        bool HasImg(string t) => i.TryGetProperty("ImageTags", out var tags) && tags.TryGetProperty(t, out _);
        var ud = i.TryGetProperty("UserData", out var u) ? u : default;
        return new
        {
            id = S("Id"), name = S("Name"), type = S("Type"), year = N("ProductionYear"), overview = S("Overview"),
            rating = S("OfficialRating"), score = D("CommunityRating"),
            runtime = L("RunTimeTicks") is { } t ? t / 10_000_000.0 : (double?)null,
            seriesId = S("SeriesId"), seriesName = S("SeriesName"), season = N("ParentIndexNumber"), episode = N("IndexNumber"),
            seasonId = S("SeasonId"),
            genres = i.TryGetProperty("Genres", out var g) ? g.EnumerateArray().Select(x => x.GetString()).Take(3).ToList() : null,
            hasPrimary = HasImg("Primary"), hasThumb = HasImg("Thumb"),
            hasBackdrop = i.TryGetProperty("BackdropImageTags", out var bt) && bt.GetArrayLength() > 0,
            parentBackdropId = S("ParentBackdropItemId"), parentThumbId = S("ParentThumbItemId"), seriesPrimaryTag = S("SeriesPrimaryImageTag"),
            position = ud.ValueKind == JsonValueKind.Object && ud.TryGetProperty("PlaybackPositionTicks", out var pp) ? pp.GetInt64() / 10_000_000.0 : 0,
            played = ud.ValueKind == JsonValueKind.Object && ud.TryGetProperty("Played", out var pl) && pl.GetBoolean(),
            unplayed = ud.ValueKind == JsonValueKind.Object && ud.TryGetProperty("UnplayedItemCount", out var uc) ? uc.GetInt32() : 0,
            childCount = N("ChildCount"),
        };
    }

    // ---- Playback (homefront kiosk player on the HTPC) ----

    static readonly HashSet<string> TextSubs = ["subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text", "ttml", "microdvd", "smi"];

    static string LanguageName(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "Unknown";
        try
        {
            var c = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures)
                .FirstOrDefault(x => x.ThreeLetterISOLanguageName == code || x.TwoLetterISOLanguageName == code || x.ThreeLetterWindowsLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (c != null) return c.EnglishName;
        }
        catch { }
        return code switch { "chi" => "Chinese", "fre" => "French", "ger" => "German", "dut" => "Dutch", "gre" => "Greek", "per" => "Persian", _ => code };
    }

    // Track titles are often release-group noise; keep only the parts that help pick a track.
    static string TrackQualifier(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.ToLowerInvariant();
        var bits = new List<string>();
        if (t.Contains("sdh") || t.Contains("hearing")) bits.Add("SDH");
        if (t.Contains("latin")) bits.Add("Latin American");
        else if (t.Contains("brasil") || t.Contains("brazil")) bits.Add("Brazil");
        else if (t.Contains("portugal") || t.Contains("european")) bits.Add("Europe");
        if (t.Contains("simplified")) bits.Add("Simplified");
        if (t.Contains("traditional")) bits.Add("Traditional");
        if (t.Contains("commentary")) bits.Add("Commentary");
        if (t.Contains("signs") || t.Contains("songs")) bits.Add("Signs & songs");
        return bits.Count > 0 ? $" ({string.Join(", ", bits)})" : "";
    }

    /// Subtitle and audio tracks of an item, plus which subtitle to show by default.
    static (List<object> subs, List<object> audio, int? defaultSub, int? defaultAudio) Tracks(string itemId, string msId, JsonElement source, string prefLang)
    {
        var subs = new List<object>(); var audio = new List<object>();
        int? defSub = null, defAudio = null, firstPref = null, firstForced = null;
        if (!source.TryGetProperty("MediaStreams", out var streams)) return (subs, audio, null, null);
        foreach (var s in streams.EnumerateArray())
        {
            var type = s.GetProperty("Type").GetString();
            var index = s.GetProperty("Index").GetInt32();
            string? Str(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Bool(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            var lang = Str("Language");
            var name = LanguageName(lang) + TrackQualifier(Str("Title"));
            if (type == "Subtitle")
            {
                var codec = (Str("Codec") ?? "").ToLowerInvariant();
                var isText = TextSubs.Contains(codec) || Bool("IsTextSubtitleStream");
                var forced = Bool("IsForced");
                subs.Add(new
                {
                    index, name = name + (forced ? " (forced)" : ""), lang, codec, isText, forced, isDefault = Bool("IsDefault"),
                    vtt = isText ? $"/jf/Videos/{itemId}/{msId}/Subtitles/{index}/0/Stream.vtt" : null,
                });
                if (Bool("IsDefault") && defSub == null) defSub = index;
                if (lang == prefLang && !forced && firstPref == null) firstPref = index;
                if (forced && lang == prefLang && firstForced == null) firstForced = index;
            }
            else if (type == "Audio")
            {
                var ch = s.TryGetProperty("Channels", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                audio.Add(new { index, name = name + (ch >= 6 ? " 5.1" : ch == 2 ? " stereo" : ""), lang, isDefault = Bool("IsDefault") });
                if (Bool("IsDefault") && defAudio == null) defAudio = index;
            }
        }
        // Preferred language first, then whatever the file marks as default.
        return (subs, audio, firstPref ?? firstForced ?? defSub, defAudio);
    }

    public string HlsUrl(string itemId, string msId, string playSession, int? audio, int? burnSub) =>
        // Copy streams the browser can decode, let Jellyfin (Quick Sync) transcode the rest.
        $"/jf/Videos/{itemId}/master.m3u8?MediaSourceId={msId}&PlaySessionId={playSession}&DeviceId=homefront-htpc" +
        "&VideoCodec=h264,hevc&AudioCodec=aac,mp3,ac3,eac3&AllowVideoStreamCopy=true&AllowAudioStreamCopy=true" +
        "&TranscodingMaxAudioChannels=6&MaxStreamingBitrate=60000000&SegmentContainer=mp4&MinSegments=1&BreakOnNonKeyFrames=true" +
        "&h264-profile=high,main,baseline&h264-level=52&hevc-profile=main,main10&RequireAvc=false" +
        (audio is { } a ? $"&AudioStreamIndex={a}" : "") +
        // Picture-based subtitles (DVD/PGS) can't be a browser track, so Jellyfin draws them into the video.
        (burnSub is { } b ? $"&SubtitleStreamIndex={b}&SubtitleMethod=Encode&AllowVideoStreamCopy=false" : "");

    public async Task<object> PlaybackSource(string itemId, int? audio = null, int? burnSub = null, string prefLang = "eng", bool subsOn = true)
    {
        var item = await RawItem(itemId);
        var source = item.GetProperty("MediaSources")[0];
        var msId = source.GetProperty("Id").GetString()!;
        var playSession = Guid.NewGuid().ToString("N");
        var (subs, audios, defSub, defAudio) = Tracks(itemId, msId, source, prefLang);
        var hls = HlsUrl(itemId, msId, playSession, audio, burnSub);
        double resume = item.TryGetProperty("UserData", out var ud) && ud.TryGetProperty("PlaybackPositionTicks", out var p) ? p.GetInt64() / 10_000_000.0 : 0;
        var runtime = item.TryGetProperty("RunTimeTicks", out var rt) ? rt.GetInt64() / 10_000_000.0 : 0;
        if (runtime > 0 && resume > runtime * 0.95) resume = 0;
        return new
        {
            itemId, mediaSourceId = msId, playSessionId = playSession, hls, resume, runtime,
            item = SlimOne(item),
            subtitles = subs, audio = audios,
            defaultSub = subsOn ? defSub : null, defaultAudio = defAudio, audioIndex = audio ?? defAudio, burnSub,
        };
    }

    public Task Report(string evt, string itemId, string msId, string playSession, double position, bool paused)
    {
        var body = new
        {
            ItemId = itemId, MediaSourceId = msId, PlaySessionId = playSession,
            PositionTicks = (long)(position * 10_000_000), IsPaused = paused, CanSeek = true, PlayMethod = "Transcode",
            EventName = paused ? "Pause" : "TimeUpdate",
        };
        return evt switch
        {
            "start" => Post("/Sessions/Playing", body),
            "stop" => Post("/Sessions/Playing/Stopped", body),
            _ => Post("/Sessions/Playing/Progress", body),
        };
    }
}
