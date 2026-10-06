using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homefront;

// ---------------- settings (all opt-in) ----------------

public class RoutineTask
{
    public string Name { get; set; } = "";
    public int Minutes { get; set; } = 5;
}

public class MorningConfig
{
    public bool Enabled { get; set; }
    public List<string> Days { get; set; } = ["mon", "tue", "wed", "thu", "fri"];
    public string UpBy { get; set; } = "07:00";
    public string LeaveBy { get; set; } = "08:00";
    public bool KeepAsking { get; set; } = true;        // "Are you up?" until you say so
    public int AskEveryMinutes { get; set; } = 5;
    public bool BrightenBedroom { get; set; } = true;   // from the second ask, the bedroom goes full daylight
    public bool MotionMeansUp { get; set; } = true;     // movement on a camera counts as up
    public bool TvRunThrough { get; set; } = true;      // the TV wakes with the list once you're up
    public bool TimeChecks { get; set; } = true;        // "Leave in 15 minutes" on the phone and TV
    public List<RoutineTask> Tasks { get; set; } =
    [
        new() { Name = "Shower", Minutes = 15 },
        new() { Name = "Get dressed", Minutes = 10 },
        new() { Name = "Breakfast", Minutes = 15 },
        new() { Name = "Meds", Minutes = 2 },
        new() { Name = "Brush teeth", Minutes = 5 },
        new() { Name = "Pack your bag", Minutes = 5 },
    ];
    public List<string> Remember { get; set; } = ["Keys", "Wallet", "Phone", "Charger"];
}

public class EveningConfig
{
    public bool Enabled { get; set; }
    public List<string> Days { get; set; } = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    public string WindDownAt { get; set; } = "22:15";
    public string BedAt { get; set; } = "23:00";
    public bool SoftenLights { get; set; } = true;      // lights that are on go warm and no brighter than 35%
    public bool TimeChecks { get; set; } = true;
    public List<RoutineTask> Tasks { get; set; } =
    [
        new() { Name = "Meds", Minutes = 2 },
        new() { Name = "Lay out tomorrow's clothes", Minutes = 5 },
        new() { Name = "Phone on charge", Minutes = 1 },
        new() { Name = "Brush teeth", Minutes = 5 },
    ];
}

public class RoutinesConfig
{
    public MorningConfig Morning { get; set; } = new();
    public EveningConfig Evening { get; set; } = new();
    public string NotifyService { get; set; } = "";     // older single-phone setting, still honoured
    public List<string> Phones { get; set; } = [];       // notify services ("mobile_app_..."); empty = every phone
    public bool LiveActivity { get; set; } = true;       // lock-screen countdown (iOS Live Activity / Android Live Update)
}

// ---------------- the engine ----------------

/// Helps with time blindness and getting started: keeps asking until you're up, puts the morning list and the clock
/// on the TV, says how long is left at the moments that matter, and winds the evening down.
public class Routines
{
    readonly HomeAssistant _ha; readonly ConfigStore _cfg; readonly Apps _apps; readonly TvNotices _tv; readonly Lights _lights; readonly Hub _hub;
    readonly string _ranFile;
    public Routines(HomeAssistant ha, ConfigStore cfg, Apps apps, TvNotices tv, Lights lights, Hub hub, string dataDir)
    {
        _ha = ha; _cfg = cfg; _apps = apps; _tv = tv; _lights = lights; _hub = hub;
        // Which days each routine already ran, so a restart mid-morning doesn't start asking again.
        _ranFile = Path.Combine(dataDir, "routines-ran.json");
        try { foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_ranFile)) ?? []) _ranOn[kv.Key] = DateOnly.Parse(kv.Value); } catch { }
        ha.NotificationAction += a => _ = OnAction(a);
        ha.EntityChanged += (id, s) => _ = OnEntity(id, s);
    }

    class Run
    {
        public string Kind = "";           // morning | evening
        public string Phase = "";          // waking | running | done
        public DateTime Started, EndsAt;
        public List<RoutineTask> Tasks = [];
        public HashSet<int> Done = [];
        public int Current;                // first task not done
        public DateTime CurrentSince;
        public int Asks; public DateTime NextAsk;
        public HashSet<int> ChecksSent = [];
        public HashSet<int> OverrunSent = [];
        public bool Test;
    }
    Run? _run;
    readonly Dictionary<string, DateOnly> _ranOn = new();
    void MarkRan(string kind, DateOnly day)
    {
        _ranOn[kind] = day;
        try { File.WriteAllText(_ranFile, JsonSerializer.Serialize(_ranOn.ToDictionary(k => k.Key, k => k.Value.ToString("yyyy-MM-dd")))); } catch { }
    }
    DateTime _lastTick = DateTime.MinValue;
    List<string>? _allPhones; DateTime _phonesAt = DateTime.MinValue;

    static readonly int[] Checkpoints = [30, 15, 10, 5, 0];

    public object View()
    {
        var r = _run;
        if (r == null || r.Phase == "done") return new { active = false };
        var m = _cfg.Value.Routines.Morning;
        return new
        {
            active = true, kind = r.Kind, phase = r.Phase, test = r.Test,
            started = Ms(r.Started), endsAt = Ms(r.EndsAt), currentSince = Ms(r.CurrentSince),
            current = r.Current, asks = r.Asks,
            tasks = r.Tasks.Select((t, i) => new { name = t.Name, minutes = t.Minutes, done = r.Done.Contains(i) }),
            remember = r.Kind == "morning" ? m.Remember : [],
        };
    }
    static long Ms(DateTime local) => local == DateTime.MinValue ? 0 : new DateTimeOffset(local).ToUnixTimeMilliseconds();
    void Changed() => _hub.Broadcast("routine", View());

    static DateTime At(string hhmm, DateTime day) =>
        TimeOnly.TryParse(hhmm, out var t) ? day.Date + t.ToTimeSpan() : day.Date.AddHours(7);
    static string Dow(DateTime d) => d.DayOfWeek.ToString()[..3].ToLowerInvariant();

    // ---- clock: every 10 seconds ----
    public async Task Tick()
    {
        if (DateTime.UtcNow - _lastTick < TimeSpan.FromSeconds(10)) return;
        _lastTick = DateTime.UtcNow;
        try
        {
            var now = DateTime.Now;
            var rc = _cfg.Value.Routines;
            var today = DateOnly.FromDateTime(now);

            if (_run == null || _run.Phase == "done")
            {
                var m = rc.Morning;
                if (m.Enabled && m.Days.Contains(Dow(now)) && _ranOn.GetValueOrDefault("morning") != today)
                {
                    var up = At(m.UpBy, now); var leave = At(m.LeaveBy, now);
                    if (now >= up && now < leave) { MarkRan("morning", today); await StartMorning(up, leave, test: false); }
                }
                var e = rc.Evening;
                if (e.Enabled && e.Days.Contains(Dow(now)) && _ranOn.GetValueOrDefault("evening") != today)
                {
                    var wind = At(e.WindDownAt, now); var bed = At(e.BedAt, now);
                    if (bed <= wind) bed = bed.AddDays(1);
                    if (now >= wind && now < bed) { MarkRan("evening", today); await StartEvening(wind, bed, test: false); }
                }
                return;
            }

            var r = _run;
            if (r.Phase == "waking")
            {
                if (now >= r.EndsAt) { await Finish("You were meant to leave by now. The morning list is off for today."); return; }
                if (now >= r.NextAsk) await AskUp();
                return;
            }

            // running
            var left = (int)Math.Ceiling((r.EndsAt - now).TotalMinutes);
            var timeChecks = r.Kind == "morning" ? rc.Morning.TimeChecks : rc.Evening.TimeChecks;
            if (timeChecks)
                foreach (var cp in Checkpoints)
                    if (left <= cp && !r.ChecksSent.Contains(cp) && (r.EndsAt - r.Started).TotalMinutes > cp)
                    {
                        r.ChecksSent.Add(cp);
                        foreach (var older in Checkpoints.Where(x => x > cp)) r.ChecksSent.Add(older);   // never send stale ones late
                        await TimeCheck(cp);
                        break;
                    }

            // a task running long gets one gentle nudge
            if (r.Current < r.Tasks.Count && !r.OverrunSent.Contains(r.Current))
            {
                var t = r.Tasks[r.Current];
                if ((now - r.CurrentSince).TotalMinutes > t.Minutes + 2)
                {
                    r.OverrunSent.Add(r.Current);
                    var next = NextOpen(r, r.Current + 1);
                    await Notify($"Still on {t.Name.ToLowerInvariant()}?", next >= 0 ? $"Next up: {r.Tasks[next].Name}. {LeftText(left)}" : LeftText(left),
                        "hf-routine", actions: [("HF_R_DONE", $"{t.Name} done"), ("HF_R_SKIP", "Skip it")]);
                }
            }

            var grace = r.Kind == "morning" ? 45 : 30;
            if (now > r.EndsAt.AddMinutes(grace)) await Finish(null);
        }
        catch (Exception ex) { Log.Warn($"routines: {ex.Message}"); }
    }

    static string LeftText(int left) => left > 0 ? $"{left} min left." : "Time's up.";
    static int NextOpen(Run r, int from) { for (var i = from; i < r.Tasks.Count; i++) if (!r.Done.Contains(i)) return i; for (var i = 0; i < from && i < r.Tasks.Count; i++) if (!r.Done.Contains(i)) return i; return -1; }

    // ---- morning ----
    public async Task StartMorning(DateTime upBy, DateTime leaveBy, bool test)
    {
        var m = _cfg.Value.Routines.Morning;
        _run = new Run { Kind = "morning", Started = DateTime.Now, EndsAt = leaveBy, Tasks = m.Tasks.Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList(), Test = test };
        Log.Info($"routine: morning {(test ? "(test) " : "")}until {leaveBy:HH:mm}");
        if (m.KeepAsking && !test) { _run.Phase = "waking"; _run.NextAsk = DateTime.Now; await AskUp(); }
        else await BeginRunning(fromPhone: false);
    }

    async Task AskUp()
    {
        var r = _run!; var m = _cfg.Value.Routines.Morning;
        r.Asks++;
        r.NextAsk = DateTime.Now.AddMinutes(Math.Max(2, m.AskEveryMinutes));
        var left = (int)Math.Round((r.EndsAt - DateTime.Now).TotalMinutes);
        string[] lines = ["Time to get up.", "Still in bed?", "Feet on the floor.", "Up you get.", "Sit up, then stand up."];
        var title = lines[Math.Min(r.Asks - 1, lines.Length - 1)];
        await Notify(title, $"It's {DateTime.Now:HH:mm}. You leave in {left} min.", "hf-wake", timeSensitive: true,
            actions: [("HF_R_UP", "I'm up"), ("HF_R_SNOOZE", "5 more minutes")]);
        if (r.Asks == 2 && m.BrightenBedroom)
        {
            var bed = _lights.InRooms(["Bedroom"]);
            if (bed.Count > 0) await _lights.Set(bed, true, 100, 5000, 2);
        }
        Changed();
    }

    async Task BeginRunning(bool fromPhone)
    {
        var r = _run!; var m = _cfg.Value.Routines.Morning;
        r.Phase = "running"; r.Current = NextOpen(r, 0); r.CurrentSince = DateTime.Now;
        await Live();
        if (r.Kind == "morning")
        {
            var left = (int)Math.Round((r.EndsAt - DateTime.Now).TotalMinutes);
            await Notify("Good morning", r.Tasks.Count > 0 ? $"{r.Tasks.Count} things before {r.EndsAt:HH:mm}. First: {r.Tasks[0].Name}." : $"Leave by {r.EndsAt:HH:mm}.", "hf-wake");
            if (m.TvRunThrough && (_apps.KioskPage == null || _apps.KioskPage == "ambient"))
            {
                try { await _apps.TvOn(); await _apps.Tv($"-sethdmi {_cfg.Value.Pc.TvPcInput}"); } catch (Exception e) { Log.Warn($"routine tv: {e.Message}"); }
                await _apps.OpenKiosk("routine", "/routine");
            }
        }
        Changed();
    }

    // ---- evening ----
    public async Task StartEvening(DateTime windDown, DateTime bed, bool test)
    {
        var e = _cfg.Value.Routines.Evening;
        _run = new Run { Kind = "evening", Phase = "running", Started = DateTime.Now, EndsAt = bed, Tasks = e.Tasks.Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList(), Test = test, CurrentSince = DateTime.Now };
        _run.Current = NextOpen(_run, 0);
        Log.Info($"routine: evening {(test ? "(test) " : "")}until {bed:HH:mm}");
        if (e.SoftenLights)
        {
            foreach (var l in _lights.All.Select(x => x.GetProperty("entity_id").GetString()!).Where(_lights.IsOn))
            {
                var snap = _lights.Capture(l);
                var pct = snap.Brightness is { } b ? (int)Math.Round(b / 2.55) : 100;
                await _lights.Set([l], true, Math.Min(pct, 35), 2200, 30);
            }
        }
        var list = _run.Tasks.Count > 0 ? string.Join(", ", _run.Tasks.Select(t => t.Name)) + "." : "";
        await Notify($"Wind-down time. Bed at {bed:HH:mm}.", list, "hf-routine");
        await Live();
        await _tv.Show($"Wind-down time. Bed at {bed:HH:mm}.");
        Changed();
    }

    // ---- reminders ----
    async Task TimeCheck(int minutes)
    {
        var r = _run!;
        string title, body;
        var next = r.Current >= 0 && r.Current < r.Tasks.Count ? r.Tasks[r.Current].Name : null;
        if (r.Kind == "morning")
        {
            var remember = _cfg.Value.Routines.Morning.Remember;
            var rem = remember.Count > 0 ? string.Join(", ", remember) : "";
            title = minutes == 0 ? "Time to leave." : $"Leave in {minutes} minutes.";
            body = minutes <= 10 && rem != "" ? $"Grab: {rem}." : next != null ? $"Now: {next}." : "";
        }
        else
        {
            title = minutes == 0 ? "Bedtime." : $"Bed in {minutes} minutes.";
            body = next != null ? $"Still to do: {next}." : "Screens off soon.";
        }
        await Notify(title, body, "hf-routine", timeSensitive: minutes <= 5);
        await _tv.Show($"{title} {body}".Trim());
        await Live();
        Changed();
    }

    // ---- actions: phone buttons, the TV, homefront ----
    async Task OnAction(string action)
    {
        var r = _run;
        if (r == null || r.Phase == "done") return;
        switch (action)
        {
            case "HF_R_UP": if (r.Phase == "waking") await BeginRunning(fromPhone: true); break;
            case "HF_R_SNOOZE": if (r.Phase == "waking") { r.NextAsk = DateTime.Now.AddMinutes(5); Changed(); } break;
            case "HF_R_DONE": if (r.Phase == "running" && r.Current >= 0) await Complete(r.Current); break;
            case "HF_R_SKIP": if (r.Phase == "running" && r.Current >= 0) await Complete(r.Current, skipped: true); break;
        }
    }

    public async Task<bool> Command(string cmd, int? index)
    {
        var r = _run;
        var now = DateTime.Now;
        switch (cmd)
        {
            case "start-morning": { var m = _cfg.Value.Routines.Morning; var leave = At(m.LeaveBy, now); if (leave <= now) leave = now.AddMinutes(60); await StartMorning(now, leave, test: true); return true; }
            case "start-evening": { var e = _cfg.Value.Routines.Evening; var bed = At(e.BedAt, now); if (bed <= now) bed = now.AddMinutes(45); await StartEvening(now, bed, test: true); return true; }
            case "stop": if (r != null) await Finish(null); return true;
        }
        if (r == null || r.Phase == "done") return false;
        switch (cmd)
        {
            case "up": if (r.Phase == "waking") await BeginRunning(fromPhone: true); return true;
            case "snooze": if (r.Phase == "waking") { r.NextAsk = now.AddMinutes(5); Changed(); } return true;
            case "done": await Complete(index ?? r.Current); return true;
            case "skip": await Complete(index ?? r.Current, skipped: true); return true;
            case "undo":
                var last = index ?? r.Done.DefaultIfEmpty(-1).Max();
                if (last >= 0 && r.Done.Remove(last)) { r.Current = NextOpen(r, 0); r.CurrentSince = now; await Live(); Changed(); }
                return true;
        }
        return false;
    }

    async Task Complete(int i, bool skipped = false)
    {
        var r = _run!;
        if (i < 0 || i >= r.Tasks.Count) return;
        if (r.Phase == "waking") await BeginRunning(fromPhone: false);   // ticking something off means you're up
        r.Done.Add(i);
        if (i == r.Current) { r.Current = NextOpen(r, i + 1); r.CurrentSince = DateTime.Now; }
        if (r.Current < 0)
        {
            var left = (int)Math.Round((r.EndsAt - DateTime.Now).TotalMinutes);
            await Notify(r.Kind == "morning" ? "All done." : "All done. Sleep well.",
                r.Kind == "morning" ? (left > 0 ? $"{left} min to spare before you leave." : "Out the door.") : "", "hf-routine");
        }
        await Live();
        Changed();
    }

    async Task Finish(string? message)
    {
        var r = _run;
        if (r == null) return;
        r.Phase = "done";
        if (message != null) await Notify("Morning", message, "hf-wake");
        await Send(new JsonObject { ["message"] = "clear_notification", ["data"] = new JsonObject { ["tag"] = LiveTag } });
        if (_apps.KioskPage == "routine") await _apps.CloseKiosk();
        Log.Info($"routine: {r.Kind} finished");
        Changed();
    }

    async Task OnEntity(string id, JsonElement? s)
    {
        var r = _run;
        if (r == null || s == null) return;
        var state = s.Value.GetProperty("state").GetString();
        // movement on a camera while being asked: you're up
        if (r.Phase == "waking" && _cfg.Value.Routines.Morning.MotionMeansUp && id.StartsWith("binary_sensor.") && id.Contains("motion") && state == "on")
        {
            Log.Info("routine: movement seen, counting it as up");
            await BeginRunning(fromPhone: false);
            return;
        }
        // everyone left: the morning's over
        if (r.Phase == "running" && r.Kind == "morning" && id.StartsWith("person.") && state != "home"
            && _ha.Entities.Where(e => e.Key.StartsWith("person.")).All(e => e.Value.GetProperty("state").GetString() != "home"))
            await Finish(null);
    }

    // ---- phone notifications through Home Assistant's mobile app ----
    /// Every phone with the Home Assistant app signed in ("mobile_app_..." notify services).
    public async Task<List<string>> AllPhones()
    {
        if (_allPhones != null && DateTime.UtcNow - _phonesAt < TimeSpan.FromMinutes(10)) return _allPhones;
        var list = new List<string>();
        try
        {
            var services = await _ha.Rest(HttpMethod.Get, "/api/services");
            foreach (var d in services.EnumerateArray())
                if (d.GetProperty("domain").GetString() == "notify")
                    foreach (var svc in d.GetProperty("services").EnumerateObject())
                        if (svc.Name.StartsWith("mobile_app_")) list.Add(svc.Name);
            _allPhones = list; _phonesAt = DateTime.UtcNow;
        }
        catch (Exception e) { Log.Warn($"routine phone lookup: {e.Message}"); }
        return list;
    }

    async Task<List<string>> Targets()
    {
        var rc = _cfg.Value.Routines;
        if (rc.Phones.Count > 0) return rc.Phones.Select(p => p.Replace("notify.", "")).ToList();
        if (!string.IsNullOrWhiteSpace(rc.NotifyService)) return [rc.NotifyService.Replace("notify.", "")];
        return await AllPhones();
    }

    async Task Send(JsonObject payload)
    {
        foreach (var target in await Targets())
        {
            try { await _ha.CallService("notify", target, payload.DeepClone()); }
            catch (Exception e) { Log.Warn($"routine notify {target}: {e.Message}"); }
        }
    }

    const string LiveTag = "hf_routine_live";

    /// The lock-screen countdown: "Leave by 08:00", a timer ticking to it, the step you're on and how many are done.
    /// The phone runs the timer itself, so this only needs sending when something changes.
    async Task Live()
    {
        var r = _run;
        if (r == null || r.Phase != "running" || !_cfg.Value.Routines.LiveActivity) return;
        var morning = r.Kind == "morning";
        var cur = r.Current >= 0 && r.Current < r.Tasks.Count ? r.Tasks[r.Current] : null;
        var done = r.Done.Count;
        var title = (r.Test ? "[Test] " : "") + (morning ? $"Leave by {r.EndsAt:HH:mm}" : $"Bed at {r.EndsAt:HH:mm}");
        var message = cur != null ? $"Now: {cur.Name}. {done} of {r.Tasks.Count} done." : morning ? "All done. Out the door." : "All done. Sleep well.";
        var data = new JsonObject
        {
            ["tag"] = LiveTag, ["live_update"] = true,
            ["critical_text"] = cur?.Name ?? "Done",
            ["progress"] = done, ["progress_max"] = Math.Max(1, r.Tasks.Count),
            ["chronometer"] = true, ["when"] = new DateTimeOffset(r.EndsAt).ToUnixTimeSeconds(),
            ["notification_icon"] = morning ? "mdi:run-fast" : "mdi:weather-night",
            ["notification_icon_color"] = "#ffc94d", ["color"] = "#ffc94d",
            ["alert_once"] = true, ["silent"] = true,
        };
        await Send(new JsonObject { ["title"] = title, ["message"] = message, ["data"] = data });
    }

    async Task Notify(string title, string message, string tag, bool timeSensitive = false, (string id, string title)[]? actions = null)
    {
        if (_run?.Test == true) title = "[Test] " + title;
        var data = new JsonObject { ["tag"] = tag };
        if (timeSensitive) data["push"] = new JsonObject { ["interruption-level"] = "time-sensitive" };
        if (actions != null) data["actions"] = new JsonArray(actions.Select(a => (JsonNode)new JsonObject { ["action"] = a.id, ["title"] = a.title }).ToArray());
        await Send(new JsonObject { ["title"] = title, ["message"] = string.IsNullOrWhiteSpace(message) ? " " : message, ["data"] = data });
    }
}
