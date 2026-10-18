package app.homefront

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import app.homefront.widget.Widgets
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import kotlin.math.roundToInt

data class Scene(val id: String, val name: String, val icon: String)

data class Room(
    val name: String, val icon: String, val total: Int, val on: Int,
    /** Average brightness of the lit bulbs, 1–100. */
    val brightness: Int,
    /** What the room glows like right now (ARGB), or null when it's dark. */
    val glow: Int?,
    /** Average colour temperature of the lit bulbs, when they're on white rather than a colour. */
    val kelvin: Int? = null,
    /** At least one lit bulb is showing a colour rather than a white. */
    val colour: Boolean = false,
) {
    val lit get() = on > 0
    /** "Warm white", "Daylight", "Colour"… */
    val tone: String? get() = when {
        !lit -> null
        colour -> "Colour"
        kelvin == null -> null
        kelvin < 2500 -> "Candlelight"
        kelvin < 3200 -> "Warm white"
        kelvin < 4500 -> "Neutral white"
        else -> "Daylight"
    }
}

data class Media(
    val active: Boolean, val app: String?, val title: String?, val artist: String?, val status: String,
    val isVideo: Boolean, val position: Double, val duration: Double, val updatedAt: Long,
    val artVersion: String?, val canPrev: Boolean, val canNext: Boolean,
) {
    val playing get() = status == "playing"
    companion object { val NONE = Media(false, null, null, null, "stopped", false, 0.0, 0.0, 0, null, false, false) }
}

data class Tv(val present: Boolean, val on: Boolean, val volume: Int?, val muted: Boolean, val source: String?) {
    companion object { val NONE = Tv(false, false, null, false, null) }
}

data class HomeState(
    val configured: Boolean = false,
    val signedIn: Boolean = false,
    /** Got a snapshot from the server recently and nothing has failed since. */
    val online: Boolean = false,
    val updatedAt: Long = 0,
    val home: String = "Home",
    val rooms: List<Room> = emptyList(),
    val scenes: List<Scene> = DEFAULT_SCENES,
    val media: Media = Media.NONE,
    val art: Bitmap? = null,
    val pcVolume: Int = 0,
    val pcMuted: Boolean = false,
    val tv: Tv = Tv.NONE,
    val temp: Double? = null,
    /** Width of the HTPC's screen in pixels, for touchpad speed. */
    val screenW: Int = 1920,
    /** Touchpad widget's quick controls are unlocked until this time. */
    val padArmedUntil: Long = 0,
    /** Scene tapped on a widget, shown as running for a little while. */
    val lastScene: String? = null,
    val lastSceneAt: Long = 0,
    /** Widget button that is waiting on the server, shown pressed. */
    val pending: String? = null,
) {
    companion object {
        val DEFAULT_SCENES = listOf(
            Scene("movie", "Movie", "popcorn"), Scene("evening", "Evening", "sun-dim"), Scene("bright", "Bright", "sun"),
            Scene("latenight", "Late night", "lamp"), Scene("off", "Lights off", "lightbulb-off"), Scene("goodnight", "Goodnight", "moon-star"),
        )
    }
}

/**
 * The server's live state, kept the way the web app keeps it (snapshot + patches off the socket),
 * boiled down to what the widgets show. Widgets only redraw when that boiled-down state changes,
 * so a chatty temperature sensor doesn't wake them.
 */
object Store {
    private const val TAG = "homefront"
    private lateinit var app: Context
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val lock = Mutex()

    private val _state = MutableStateFlow(HomeState())
    val state: StateFlow<HomeState> = _state

    // Raw server state, same shapes as the snapshot.
    private var entities = JSONObject()
    private var rooms = JSONArray()
    private var media = JSONObject()
    private var volume = JSONObject()
    private var weather: JSONObject? = null
    private var scenes: JSONArray? = null
    private var tvEntity: String? = null
    private var home = "Home"
    private var online = false
    private var updatedAt = 0L
    private var art: Bitmap? = null
    private var artVersion: String? = null
    private var lastScene: String? = null
    private var lastSceneAt = 0L
    private var pending: String? = null
    private var screenW = 1920
    private var padArmedUntil = 0L

    private val cacheFile get() = File(app.filesDir, "state.json")
    private val artFile get() = File(app.filesDir, "art.png")

    fun init(ctx: Context) {
        app = ctx.applicationContext
        runCatching {
            val c = JSONObject(cacheFile.readText())
            entities = c.optJSONObject("entities") ?: JSONObject()
            rooms = c.optJSONArray("rooms") ?: JSONArray()
            media = c.optJSONObject("media") ?: JSONObject()
            volume = c.optJSONObject("volume") ?: JSONObject()
            weather = c.optJSONObject("weather")
            scenes = c.optJSONArray("scenes")
            tvEntity = c.optString("tvEntity").ifEmpty { null }
            home = c.optString("home", "Home")
            screenW = c.optInt("screenW", 1920)
            updatedAt = c.optLong("updatedAt")
            artVersion = c.optString("artVersion").ifEmpty { null }
            art = if (artVersion != null) BitmapFactory.decodeFile(artFile.path) else null
        }
        _state.value = derive()
    }

    // ---------------- feeding it ----------------

    /** One message off the live socket (or a snapshot fetched on its own). */
    @Synchronized fun apply(msg: JSONObject) {
        val d = msg.opt("d")
        when (msg.optString("t")) {
            "snapshot" -> (d as? JSONObject)?.let { s ->
                entities = s.optJSONObject("entities") ?: JSONObject()
                rooms = s.optJSONArray("rooms") ?: JSONArray()
                media = s.optJSONObject("media") ?: JSONObject()
                volume = s.optJSONObject("volume") ?: JSONObject()
                weather = s.optJSONObject("weather")
                scenes = s.optJSONArray("scenes")
                tvEntity = s.optString("tvEntity").ifEmpty { null }
                home = s.optJSONObject("home")?.optString("name")?.ifEmpty { null } ?: home
                s.optJSONObject("screen")?.optInt("w")?.takeIf { it > 0 }?.let { screenW = it }
            }
            "entity" -> (d as? JSONObject)?.let { e ->
                val id = e.optString("id")
                // Only lights and media players matter here; everything else is ignored for speed.
                if (!id.startsWith("light.") && !id.startsWith("media_player.")) return
                val st = e.optJSONObject("state")
                if (st == null) entities.remove(id) else entities.put(id, st)
            }
            "entities" -> (d as? JSONObject)?.let { entities = it }
            "rooms" -> (d as? JSONArray)?.let { rooms = it }
            "media" -> (d as? JSONObject)?.let { media = it }
            "volume" -> (d as? JSONObject)?.let { volume = it }
            "feeds" -> (d as? JSONObject)?.let { weather = it.optJSONObject("weather") }
            "tvEntity" -> tvEntity = (d as? String)?.ifEmpty { null }
            "config" -> (d as? JSONObject)?.let { c ->
                c.optJSONArray("rooms")?.let { rooms = it }
                c.optJSONObject("home")?.optString("name")?.ifEmpty { null }?.let { home = it }
            }
            else -> return
        }
        online = true
        updatedAt = System.currentTimeMillis()
        publish()
    }

    @Synchronized fun setOnline(v: Boolean) { if (online != v) { online = v; publish() } }

    /** Fetch a fresh snapshot over HTTP-upgraded socket; used when the live connection isn't running. */
    suspend fun refresh(): Boolean = lock.withLock {
        if (!Prefs.configured || !Prefs.signedIn) { publish(); return false }
        try {
            apply(Api.snapshot())
            true
        } catch (e: Exception) {
            Log.i(TAG, "refresh failed: ${e.message}")
            online = false; publish(); false
        }
    }

    // ---------------- optimistic edits from widget taps ----------------

    @Synchronized fun sceneTapped(id: String) { lastScene = id; lastSceneAt = System.currentTimeMillis(); publish() }

    @Synchronized fun setPending(key: String?) { pending = key; publish() }

    private var relock: kotlinx.coroutines.Job? = null

    /** Unlock (or keep unlocked) the touchpad widget's quick controls; they lock again after 20 idle seconds. */
    @Synchronized fun armPad(on: Boolean) {
        padArmedUntil = if (on) System.currentTimeMillis() + PAD_UNLOCK_MS else 0
        relock?.cancel()
        if (on) relock = scope.launch { delay(PAD_UNLOCK_MS); armPad(false) }
        publish()
    }
    const val PAD_UNLOCK_MS = 20_000L

    @Synchronized fun patchMedia(status: String) { media.put("status", status); publish() }

    @Synchronized fun patchPcVolume(level: Int?, muted: Boolean?) {
        level?.let { volume.put("level", it) }; muted?.let { volume.put("muted", it) }; publish()
    }

    @Synchronized fun patchRoom(room: String, on: Boolean, brightnessPct: Int? = null) {
        for (id in roomLights(room)) {
            val e = entities.optJSONObject(id) ?: continue
            e.put("state", if (on) "on" else "off")
            val a = e.optJSONObject("attributes") ?: JSONObject().also { e.put("attributes", it) }
            if (!on) a.remove("brightness")
            else if (brightnessPct != null) a.put("brightness", (brightnessPct * 255 / 100.0).roundToInt())
            else if (!a.has("brightness")) a.put("brightness", 255)
        }
        publish()
    }

    @Synchronized fun patchTv(on: Boolean? = null, volume: Int? = null, muted: Boolean? = null) {
        val e = tvEntity?.let { entities.optJSONObject(it) } ?: return
        val a = e.optJSONObject("attributes") ?: JSONObject().also { e.put("attributes", it) }
        on?.let { e.put("state", if (it) "on" else "off") }
        volume?.let { a.put("volume_level", it / 100.0) }
        muted?.let { a.put("is_volume_muted", it) }
        publish()
    }

    @Synchronized fun roomLights(room: String): List<String> {
        for (i in 0 until rooms.length()) {
            val r = rooms.optJSONObject(i) ?: continue
            if (r.optString("name") == room) return r.optJSONArray("lights")?.let { l -> (0 until l.length()).map { l.optString(it) } } ?: emptyList()
        }
        return emptyList()
    }

    // ---------------- boiling down ----------------

    private var publishJob: kotlinx.coroutines.Job? = null

    @Synchronized private fun publish() {
        val next = derive()
        if (next == _state.value) return
        _state.value = next
        maybeFetchArt()
        // Coalesce bursts (a scene changes a dozen bulbs) into one widget redraw and one disk write.
        publishJob?.cancel()
        publishJob = scope.launch {
            delay(150)
            Widgets.updateAll(app)
            save()
        }
    }

    private fun derive(): HomeState {
        val roomList = (0 until rooms.length()).mapNotNull { rooms.optJSONObject(it) }.map { r ->
            val ids = r.optJSONArray("lights")?.let { l -> (0 until l.length()).map { l.optString(it) } } ?: emptyList()
            val lit = ids.mapNotNull { entities.optJSONObject(it) }.filter { it.optString("state") == "on" }
            val pct = lit.map { (it.optJSONObject("attributes")?.optDouble("brightness", 255.0) ?: 255.0) / 255.0 * 100 }
            val attrs = lit.map { it.optJSONObject("attributes") ?: JSONObject() }
            val whites = attrs.filter { it.optString("color_mode") in WHITE_MODES && it.has("color_temp_kelvin") }
            Room(
                name = r.optString("name"), icon = r.optString("icon", "lightbulb"),
                total = ids.size, on = lit.size,
                brightness = if (pct.isEmpty()) 0 else pct.average().roundToInt().coerceIn(1, 100),
                glow = if (lit.isEmpty()) null else glowOf(lit),
                kelvin = if (whites.isEmpty()) null else whites.map { it.optInt("color_temp_kelvin") }.average().roundToInt(),
                colour = attrs.any { it.optString("color_mode") in COLOUR_MODES },
            )
        }.filter { it.total > 0 }

        val m = media
        val mediaState = if (m.length() == 0) Media.NONE else Media(
            active = m.optBoolean("active"), app = m.optString("appName").ifEmpty { m.optString("app").ifEmpty { null } },
            title = m.optString("title").ifEmpty { null }, artist = m.optString("artist").ifEmpty { null },
            status = m.optString("status", "stopped"), isVideo = m.optBoolean("isVideo"),
            position = m.optDouble("position", 0.0), duration = m.optDouble("duration", 0.0), updatedAt = m.optLong("updatedAt"),
            artVersion = m.optString("artVersion").ifEmpty { null }, canPrev = m.optBoolean("canPrev"), canNext = m.optBoolean("canNext"),
        )

        val tv = tvEntity?.let { entities.optJSONObject(it) }?.let { e ->
            val a = e.optJSONObject("attributes") ?: JSONObject()
            Tv(
                present = true,
                on = e.optString("state") in setOf("on", "playing", "paused", "idle"),
                volume = if (a.has("volume_level")) (a.optDouble("volume_level") * 100).roundToInt() else null,
                muted = a.optBoolean("is_volume_muted"),
                source = a.optString("source").ifEmpty { null },
            )
        } ?: Tv.NONE

        val sc = scenes?.let { a -> (0 until a.length()).mapNotNull { a.optJSONObject(it) }.map { Scene(it.optString("id"), it.optString("name"), it.optString("icon")) } }
            ?.takeIf { it.isNotEmpty() } ?: HomeState.DEFAULT_SCENES

        return HomeState(
            configured = Prefs.configured, signedIn = Prefs.signedIn, online = online && Prefs.signedIn, updatedAt = updatedAt,
            home = home, rooms = roomList, scenes = sc, media = mediaState,
            art = if (artVersion != null && artVersion == mediaState.artVersion) art else null,
            pcVolume = volume.optInt("level"), pcMuted = volume.optBoolean("muted"), tv = tv,
            temp = weather?.optJSONObject("now")?.optDouble("temp"),
            screenW = screenW, padArmedUntil = padArmedUntil,
            lastScene = lastScene, lastSceneAt = lastSceneAt, pending = pending,
        )
    }

    private val WHITE_MODES = setOf("color_temp", "white", "brightness", "onoff", "")
    private val COLOUR_MODES = setOf("hs", "xy", "rgb", "rgbw", "rgbww")

    /** Average colour of the lit bulbs, from Home Assistant's rgb_color, or warm white by colour temperature. */
    private fun glowOf(lit: List<JSONObject>): Int {
        var r = 0.0; var g = 0.0; var b = 0.0
        for (e in lit) {
            val a = e.optJSONObject("attributes") ?: JSONObject()
            val rgb = a.optJSONArray("rgb_color")
            val c = if (rgb != null && rgb.length() == 3) Triple(rgb.optInt(0), rgb.optInt(1), rgb.optInt(2))
            else kelvinRgb(a.optInt("color_temp_kelvin", 2700))
            r += c.first; g += c.second; b += c.third
        }
        val n = lit.size
        return (0xFF shl 24) or ((r / n).toInt() shl 16) or ((g / n).toInt() shl 8) or (b / n).toInt()
    }

    private fun kelvinRgb(k: Int): Triple<Int, Int, Int> {
        // Same stops the web floor plan uses for warm-to-cool white.
        val stops = listOf(2000 to Triple(255, 160, 70), 2700 to Triple(255, 196, 130), 4000 to Triple(255, 228, 200), 6500 to Triple(240, 244, 255))
        val kk = k.coerceIn(2000, 6500)
        val (lo, hi) = stops.zipWithNext().first { kk <= it.second.first }
        val t = (kk - lo.first).toDouble() / (hi.first - lo.first)
        fun mix(a: Int, b: Int) = (a + (b - a) * t).roundToInt()
        return Triple(mix(lo.second.first, hi.second.first), mix(lo.second.second, hi.second.second), mix(lo.second.third, hi.second.third))
    }

    private var artJob: kotlinx.coroutines.Job? = null

    private fun maybeFetchArt() {
        val want = _state.value.media.artVersion ?: return
        if (want == artVersion || artJob?.isActive == true) return
        artJob = scope.launch {
            val bmp = runCatching { Api.art() }.getOrNull()
            artVersion = want; art = bmp
            runCatching { if (bmp != null) artFile.outputStream().use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) } }
            publish()
        }
    }

    private fun save() = runCatching {
        // Lights and media players only: that's all the widgets read, and it keeps the cache small.
        val ents = JSONObject()
        entities.keys().forEach { k -> if (k.startsWith("light.") || k.startsWith("media_player.")) ents.put(k, entities.opt(k)) }
        val c = JSONObject().put("entities", ents).put("rooms", rooms).put("media", media).put("volume", volume)
            .put("weather", weather).put("scenes", scenes).put("tvEntity", tvEntity).put("home", home)
            .put("updatedAt", updatedAt).put("artVersion", artVersion).put("screenW", screenW)
        cacheFile.writeText(c.toString())
    }
}
