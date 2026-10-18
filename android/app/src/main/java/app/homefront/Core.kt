package app.homefront

import android.content.Context
import android.content.SharedPreferences
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume

/** Where homefront is and the session cookie the app signed in with. */
object Prefs {
    private lateinit var sp: SharedPreferences
    fun init(ctx: Context) { sp = ctx.getSharedPreferences("homefront", Context.MODE_PRIVATE) }

    var url: String
        get() = sp.getString("url", null) ?: ""
        set(v) = sp.edit().putString("url", v.trim().trimEnd('/')).apply()

    /** Value of the hf_session cookie, copied out of the WebView once you've signed in there. */
    var session: String?
        get() = sp.getString("session", null)
        set(v) = sp.edit().putString("session", v).apply()

    /** Keep a connection open while the screen is on so widgets update the moment anything changes. */
    var live: Boolean
        get() = sp.getBoolean("live", true)
        set(v) = sp.edit().putBoolean("live", v).apply()

    val configured get() = url.isNotEmpty()
    val signedIn get() = session != null
}

class SignedOut : Exception("signed out")

/** The bits of homefront's HTTP API and live socket the widgets need. */
object Api {
    const val COOKIE = "hf_session"
    private val json = "application/json".toMediaType()

    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(4, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()

    private fun req(path: String) = Request.Builder().url(Prefs.url + path).apply {
        Prefs.session?.let { header("Cookie", "$COOKIE=$it") }
        header("User-Agent", "homefront-android/${BuildConfig.VERSION_NAME}")
    }

    private fun check(r: Response): Response {
        if (r.code == 401) { r.close(); Prefs.session = null; throw SignedOut() }
        return r
    }

    suspend fun post(path: String, body: JSONObject? = null): Boolean = withContext(Dispatchers.IO) {
        val rb = (body?.toString() ?: "").toRequestBody(json)
        check(http.newCall(req(path).post(rb).build()).execute()).use { it.isSuccessful }
    }

    suspend fun bytes(path: String): ByteArray? = withContext(Dispatchers.IO) {
        check(http.newCall(req(path).get().build()).execute()).use { if (it.isSuccessful) it.body?.bytes() else null }
    }

    /** /api/me works without a session: used to check an address really is homefront. */
    suspend fun probe(url: String): Boolean = withContext(Dispatchers.IO) {
        runCatching {
            http.newCall(Request.Builder().url(url.trimEnd('/') + "/api/me").build()).execute().use { r ->
                r.isSuccessful && JSONObject(r.body!!.string()).has("home")
            }
        }.getOrDefault(false)
    }

    suspend fun art(maxPx: Int = 320): Bitmap? = bytes("/api/pc/art")?.let { b ->
        val full = BitmapFactory.decodeByteArray(b, 0, b.size) ?: return null
        val scale = maxPx.toFloat() / maxOf(full.width, full.height)
        if (scale >= 1f) full else Bitmap.createScaledBitmap(full, (full.width * scale).toInt(), (full.height * scale).toInt(), true)
    }

    fun socketUrl() = Prefs.url.replaceFirst("http", "ws") + "/api/ws"

    fun openSocket(listener: WebSocketListener): WebSocket = http.newWebSocket(req("/api/ws").url(socketUrl()).build(), listener)

    /** One-off state: open the live socket, take the snapshot it sends first, close it. */
    suspend fun snapshot(): JSONObject = withTimeout(10_000) {
        suspendCancellableCoroutine { cont ->
            val ws = openSocket(object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) {
                    val m = JSONObject(text)
                    if (m.optString("t") == "snapshot" && cont.isActive) { cont.resume(m); webSocket.close(1000, null) }
                }
                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (response?.code == 401) { Prefs.session = null; if (cont.isActive) cont.resumeWith(Result.failure(SignedOut())) }
                    else if (cont.isActive) cont.resumeWith(Result.failure(t))
                }
            })
            cont.invokeOnCancellation { ws.cancel() }
        }
    }
}

/** Pointer and keyboard input for the HTPC, over the live socket like the web remote's touchpad. */
object Input {
    fun move(dx: Double, dy: Double) = send(JSONObject().put("t", "move").put("dx", dx).put("dy", dy))
    fun click(right: Boolean = false) = send(JSONObject().put("t", "click").put("b", if (right) "right" else "left"))
    fun button(down: Boolean) = send(JSONObject().put("t", "button").put("b", "left").put("down", down))
    fun scroll(dy: Double, dx: Double = 0.0) = send(JSONObject().put("t", "scroll").put("dy", dy).put("dx", dx))
    fun key(combo: String) = send(JSONObject().put("t", "key").put("k", combo))
    fun type(text: String) = send(JSONObject().put("t", "type").put("s", text))

    fun send(msg: JSONObject) {
        val text = msg.toString()
        if (LiveService.send(text)) return
        // No live socket (screen-off service, live widgets off): a short-lived one carries the message.
        Api.openSocket(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { webSocket.send(text); webSocket.close(1000, null) }
        })
    }
}
