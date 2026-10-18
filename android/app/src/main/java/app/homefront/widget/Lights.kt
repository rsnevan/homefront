package app.homefront.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.LocalContext
import androidx.glance.LocalSize
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.LinearProgressIndicator
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.lazy.LazyColumn
import androidx.glance.appwidget.lazy.items
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.unit.ColorProvider
import app.homefront.HomeState
import app.homefront.R
import app.homefront.Room
import app.homefront.Store

class LightsReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget = LightsWidget() }

/**
 * Every room, glowing the colour its bulbs are: brightness as a bar in that colour, the kind of
 * white, how many bulbs are on. Tap a room to switch it; − and + dim it in steps.
 */
class LightsWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) = provideContent {
        val s by Store.state.collectAsState()
        if (!Blocked(s)) Lights(s)
    }
}

private val GAP = 6.dp

@Composable
private fun Lights(s: HomeState) {
    val size = LocalSize.current
    val lit = s.rooms.count { it.lit }
    val header = size.height >= 150.dp
    val body = size.height - 28.dp - if (header) 52.dp else 0.dp
    val n = s.rooms.size.coerceAtLeast(1)
    val rowH = (body - GAP * (n - 1)) / n
    val dimmers = size.width >= 230.dp

    Frame(padding = 14.dp) {
        if (header) {
            Row(GlanceModifier.fillMaxWidth().padding(start = 2.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(GlanceModifier.defaultWeight().clickable(openApp("/lights"))) {
                    Label("Lights", 18.sp, bold = true)
                    Label(staleText(s) ?: when (lit) { 0 -> "Everything's off"; s.rooms.size -> "Every room on"; else -> "$lit of ${s.rooms.size} rooms on" }, 12.sp, Hf.muted)
                }
                if (lit > 0) {
                    Box(GlanceModifier.height(36.dp).cornerRadius(18.dp).background(Hf.raised).clickable(act("scene", "off")).padding(horizontal = 14.dp),
                        contentAlignment = Alignment.Center) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(R.drawable.lc_lightbulb_off, 16.dp)
                            Label("All off", 13.sp, modifier = GlanceModifier.padding(start = 6.dp))
                        }
                    }
                }
            }
        }
        when {
            s.rooms.isEmpty() -> Box(GlanceModifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Label(staleText(s) ?: "No lights yet", 13.sp, Hf.muted)
            }
            // Rooms share the height between them; if there are too many to fit, it scrolls.
            // (Glance draws at most 10 children in a column: five rooms and their gaps.)
            rowH >= 52.dp && s.rooms.size <= 5 -> Column(GlanceModifier.fillMaxSize()) {
                s.rooms.forEachIndexed { i, room ->
                    if (i > 0) Spacer(GlanceModifier.height(GAP))
                    RoomRow(room, dimmers, GlanceModifier.fillMaxWidth().defaultWeight(), rowH)
                }
            }
            else -> LazyColumn(GlanceModifier.fillMaxSize()) {
                items(s.rooms, itemId = { it.name.hashCode().toLong() }) { room ->
                    Column { RoomRow(room, dimmers, GlanceModifier.fillMaxWidth().height(60.dp), 60.dp); Spacer(GlanceModifier.height(GAP)) }
                }
            }
        }
    }
}

@Composable
private fun RoomRow(room: Room, dimmers: Boolean, modifier: GlanceModifier, height: Dp) {
    val ctx = LocalContext.current
    val glow = room.glow
    val roomy = height >= 66.dp
    // A lit room's card takes on a faint wash of its own light.
    val wash = if (glow != null) ColorProvider(Color(glow).copy(alpha = 0.16f)) else Hf.raised
    Row(modifier.cornerRadius(18.dp).background(wash).padding(start = 10.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Row(GlanceModifier.defaultWeight().fillMaxHeight().clickable(act("room", room.name)), verticalAlignment = Alignment.CenterVertically) {
            Glow(room, if (roomy) 44.dp else 38.dp, iconRes(ctx, room.icon))
            Column(GlanceModifier.padding(start = 12.dp, end = 8.dp).defaultWeight()) {
                Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Label(room.name, 15.sp, bold = true, modifier = GlanceModifier.defaultWeight())
                    Label(if (room.lit) "${room.brightness}%" else "Off", 15.sp, if (room.lit) Hf.text else Hf.faint, bold = room.lit)
                }
                Label(detail(room), 12.sp, Hf.muted)
                Spacer(GlanceModifier.height(if (roomy) 7.dp else 5.dp))
                LinearProgressIndicator(
                    progress = if (room.lit) room.brightness / 100f else 0f,
                    modifier = GlanceModifier.fillMaxWidth().height(4.dp).cornerRadius(2.dp),
                    color = if (glow != null) Hf.fixed(glow) else Hf.faint,
                    backgroundColor = Hf.bg,
                )
            }
        }
        if (dimmers) {
            RoundButton(R.drawable.lc_minus, act("dim", room.name, -10), 36.dp)
            Spacer(GlanceModifier.width(6.dp))
            RoundButton(R.drawable.lc_plus, act("dim", room.name, 10), 36.dp)
        }
    }
}

/** The room's icon on a disc the colour of its light; a dark room is a plain disc. */
@Composable
private fun Glow(room: Room, size: Dp, icon: Int) {
    val glow = room.glow
    Box(GlanceModifier.size(size).cornerRadius(size / 2).background(if (glow != null) Hf.fixed(glow) else Hf.bg), contentAlignment = Alignment.Center) {
        Icon(icon, size * 0.5f, if (glow != null) Hf.onAccent else Hf.faint)
    }
}

private fun detail(room: Room): String {
    val bulbs = if (room.total == 1) "1 bulb" else "${room.total} bulbs"
    return when {
        !room.lit -> bulbs
        room.on < room.total -> listOfNotNull(room.tone, "${room.on} of ${room.total} on").joinToString(" · ")
        else -> listOfNotNull(room.tone, if (room.total == 1) "1 bulb" else "all ${room.total} on").joinToString(" · ")
    }
}
