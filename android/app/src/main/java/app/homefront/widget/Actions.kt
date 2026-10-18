package app.homefront.widget

import android.content.Context
import android.util.Log
import androidx.glance.GlanceId
import androidx.glance.action.Action
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import app.homefront.Api
import app.homefront.Input
import app.homefront.LiveService
import app.homefront.SignedOut
import app.homefront.Store
import kotlinx.coroutines.delay
import org.json.JSONObject

private val KIND = ActionParameters.Key<String>("kind")
private val ARG = ActionParameters.Key<String>("arg")
private val NUM = ActionParameters.Key<Int>("num")

/** A widget tap: what to do, on what, by how much. */
fun act(kind: String, arg: String? = null, num: Int? = null): Action {
    val p = buildList<ActionParameters.Pair<out Any>> {
        add(KIND to kind)
        arg?.let { add(ARG to it) }
        num?.let { add(NUM to it) }
    }
    return actionRunCallback<WidgetAction>(actionParametersOf(*p.toTypedArray()))
}

/** Key a button uses to show it's waiting on the server. */
fun pendingKey(kind: String, arg: String? = null) = if (arg == null) kind else "$kind:$arg"

/**
 * Runs a widget tap. The widget changes first (optimistic), then the server is told; the live
 * socket (or a quick refresh when it's off) brings the real state back either way.
 */
class WidgetAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val kind = parameters[KIND] ?: return
        val arg = parameters[ARG]
        val num = parameters[NUM] ?: 0
        val s = Store.state.value
        if (kind in PAD) { pad(kind, arg, num, s.padArmedUntil > System.currentTimeMillis()); return }
        Store.setPending(pendingKey(kind, arg))
        try {
            when (kind) {
                "media" -> {
                    if (arg == "toggle") Store.patchMedia(if (s.media.playing) "paused" else "playing")
                    Api.post("/api/pc/media/${arg ?: "toggle"}")
                }
                "pcvol" -> {
                    val level = (s.pcVolume + num).coerceIn(0, 100)
                    Store.patchPcVolume(level, if (num > 0) false else null)
                    Api.post("/api/pc/volume", JSONObject().put("level", level).apply { if (num > 0 && s.pcMuted) put("muted", false) })
                }
                "pcmute" -> {
                    Store.patchPcVolume(null, !s.pcMuted)
                    Api.post("/api/pc/volume", JSONObject().put("muted", !s.pcMuted))
                }
                "scene" -> {
                    Store.sceneTapped(arg ?: return)
                    Api.post("/api/scene/$arg")
                }
                "room" -> {
                    val room = s.rooms.firstOrNull { it.name == arg } ?: return
                    Store.patchRoom(room.name, !room.lit)
                    Api.post("/api/lights/room", JSONObject().put("room", room.name).put("on", !room.lit))
                }
                "dim" -> {
                    val room = s.rooms.firstOrNull { it.name == arg } ?: return
                    if (!room.lit && num < 0) return
                    // From dark, + comes on at a gentle 20%. − stops at 1%, and once more switches off.
                    val next = if (!room.lit) 20 else room.brightness + num
                    if (room.brightness <= 1 && num < 0) {
                        Store.patchRoom(room.name, false)
                        Api.post("/api/lights/room", JSONObject().put("room", room.name).put("on", false))
                    } else {
                        val b = next.coerceIn(1, 100)
                        Store.patchRoom(room.name, true, b)
                        Api.post("/api/lights/room", JSONObject().put("room", room.name).put("on", true).put("brightness", b))
                    }
                }
                "tvpower" -> {
                    val on = !s.tv.on
                    Store.patchTv(on = on)
                    Api.post("/api/tv/${if (on) "on" else "off"}")
                }
                "tvvol" -> {
                    val level = ((s.tv.volume ?: 10) + num).coerceIn(0, 100)
                    Store.patchTv(volume = level)
                    Api.post("/api/tv/volume", JSONObject().put("level", level))
                }
                "tvmute" -> {
                    Store.patchTv(muted = !s.tv.muted)
                    Api.post("/api/tv/mute", JSONObject().put("muted", !s.tv.muted))
                }
                "tvpc" -> Api.post("/api/tv/pc")
                "refresh" -> Store.refresh()
            }
        } catch (e: SignedOut) {
            Store.refresh()
        } catch (e: Exception) {
            Log.i("homefront", "widget $kind failed: ${e.message}")
            Store.setOnline(false)
        } finally {
            Store.setPending(null)
        }
        if (!LiveService.connected && kind != "refresh") {
            delay(800)
            Store.refresh()
        }
    }

    /** Touchpad widget. Nothing reaches the PC unless the quick controls are unlocked. */
    private fun pad(kind: String, arg: String?, num: Int, armed: Boolean) {
        if (kind == "padarm") { Store.armPad(!armed); return }
        if (!armed) return
        Store.armPad(true)   // each use keeps it unlocked a little longer
        val step = Store.state.value.screenW / 24.0
        when (kind) {
            "nudge" -> when (arg) {
                "up" -> Input.move(0.0, -step); "down" -> Input.move(0.0, step)
                "left" -> Input.move(-step, 0.0); "right" -> Input.move(step, 0.0)
            }
            "pclick" -> Input.click(right = arg == "right")
            "pscroll" -> Input.scroll(num.toDouble())
            "pkey" -> arg?.let { Input.key(it) }
        }
    }

    private companion object { val PAD = setOf("padarm", "nudge", "pclick", "pscroll", "pkey") }
}
