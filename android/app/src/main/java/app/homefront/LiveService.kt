package app.homefront

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject

/**
 * Holds homefront's live socket open while the screen is on, so widgets change the moment the
 * house does: a song starts, someone dims the kitchen from the TV. With the screen off nobody can
 * see a widget, so the socket closes and the phone sleeps; it reconnects and catches up on wake.
 */
class LiveService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private var ws: WebSocket? = null
    private var attempt = 0
    private var screenOn = true
    private var generation = 0

    private val screen = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            when (i.action) {
                Intent.ACTION_SCREEN_OFF -> { screenOn = false; disconnect() }
                Intent.ACTION_SCREEN_ON -> { screenOn = true; attempt = 0; connect() }
            }
        }
    }

    private val net = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) { main.post { if (ws == null && screenOn) { attempt = 0; connect() } } }
    }

    override fun onBind(intent: Intent?) = null

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.live_channel), NotificationManager.IMPORTANCE_MIN).apply {
            description = "Keeps the homefront widgets live while the screen is on"
            setShowBadge(false)
        })
        startForeground(1, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        registerReceiver(screen, IntentFilter().apply { addAction(Intent.ACTION_SCREEN_ON); addAction(Intent.ACTION_SCREEN_OFF) })
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(net)
        screenOn = getSystemService(PowerManager::class.java).isInteractive
        running = true
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (!Prefs.live || !Prefs.signedIn || !Prefs.configured) { stopSelf(); return START_NOT_STICKY }
        if (intent?.action == ACTION_RECONNECT) disconnect()
        if (ws == null && screenOn) connect()
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        unregisterReceiver(screen)
        runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(net) }
        disconnect()
        super.onDestroy()
    }

    private fun notification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_live)
            .setContentTitle("Widgets are live")
            .setContentText("Following ${Prefs.url.removePrefix("http://").removePrefix("https://")} while the screen is on")
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    private fun connect() {
        if (!Prefs.signedIn) { stopSelf(); return }
        main.removeCallbacksAndMessages(null)
        val gen = ++generation
        ws = Api.openSocket(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { main.post { if (gen == generation) attempt = 0 }; socket = webSocket; connected = true }
            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { Store.apply(JSONObject(text)) }.onFailure { Log.w(TAG, "bad message", it) }
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { lost(gen, null) }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) { lost(gen, response?.code) }
        })
    }

    private fun lost(gen: Int, code: Int?) = main.post {
        if (gen != generation) return@post
        connected = false
        socket = null
        ws = null
        Store.setOnline(false)
        if (code == 401) { Prefs.session = null; Store.setOnline(false); stopSelf(); return@post }
        if (!screenOn) return@post
        // 1s, 2s, 4s … up to a minute: quick when the server restarts, gentle when the phone is away from home.
        val wait = (1000L shl attempt.coerceAtMost(6)).coerceAtMost(60_000)
        attempt++
        main.postDelayed({ if (ws == null && screenOn) connect() }, wait)
    }

    private fun disconnect() {
        generation++
        main.removeCallbacksAndMessages(null)
        ws?.close(1000, null)
        ws = null
        socket = null
        connected = false
    }

    companion object {
        private const val TAG = "homefront"
        private const val CHANNEL = "live"
        private const val ACTION_RECONNECT = "reconnect"
        @Volatile var running = false; private set
        @Volatile var connected = false; private set
        @Volatile private var socket: WebSocket? = null

        /** Send on the open live socket; false when there isn't one. */
        fun send(text: String): Boolean = socket?.send(text) ?: false

        /** Start (or restart, after a sign-in or address change) if live widgets are switched on. */
        fun start(ctx: Context, reconnect: Boolean = false) {
            if (!Prefs.live || !Prefs.signedIn || !Prefs.configured) { stop(ctx); return }
            runCatching {
                ctx.startForegroundService(Intent(ctx, LiveService::class.java).apply { if (reconnect) action = ACTION_RECONNECT })
            }.onFailure { Log.i(TAG, "live service not started: ${it.message}") }
        }

        fun stop(ctx: Context) { ctx.stopService(Intent(ctx, LiveService::class.java)) }
    }
}

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        LiveService.start(context)
        RefreshWorker.schedule(context)
    }
}
