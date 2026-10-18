package app.homefront.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.LocalSize
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import app.homefront.HomeState
import app.homefront.R
import app.homefront.Store

class TvReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget = TvWidget() }

/** The TV in one strip: power, volume one step at a time around the level, and the PC input. */
class TvWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) = provideContent {
        val s by Store.state.collectAsState()
        if (!Blocked(s)) Tv(s)
    }
}

@Composable
private fun Tv(s: HomeState) {
    val size = LocalSize.current
    val tv = s.tv
    val wide = size.width >= 300.dp
    val h = (size.height - 20.dp).coerceIn(40.dp, 56.dp)
    val status = staleText(s) ?: when {
        !tv.present -> "Not in Home Assistant"
        !tv.on -> "Off"
        else -> tv.source ?: "On"
    }

    Frame(padding = 10.dp) {
        Row(GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
            RoundButton(R.drawable.lc_power, act("tvpower"), h, active = tv.on)
            Column(GlanceModifier.defaultWeight().padding(start = 12.dp).clickable(openApp("/remote"))) {
                Label("TV", 16.sp, bold = true)
                Label(status, 12.sp, if (tv.on) Hf.accent else Hf.muted)
            }
            // Volume: one pill, − level +. Tap the level to mute.
            Row(GlanceModifier.height(h).cornerRadius(h / 2).background(Hf.raised), verticalAlignment = Alignment.CenterVertically) {
                Box(GlanceModifier.width(h).fillMaxHeight().clickable(act("tvvol", num = -1)), contentAlignment = Alignment.Center) { Icon(R.drawable.lc_minus, 20.dp) }
                Box(GlanceModifier.width(40.dp).fillMaxHeight().clickable(act("tvmute")), contentAlignment = Alignment.Center) {
                    when {
                        tv.muted -> Icon(R.drawable.lc_volume_x, 20.dp, Hf.danger)
                        tv.volume != null -> Label("${tv.volume}", 18.sp, bold = true)
                        else -> Icon(R.drawable.lc_volume_2, 20.dp, Hf.muted)
                    }
                }
                Box(GlanceModifier.width(h).fillMaxHeight().clickable(act("tvvol", num = 1)), contentAlignment = Alignment.Center) { Icon(R.drawable.lc_plus, 20.dp) }
            }
            if (wide) {
                Spacer(GlanceModifier.width(8.dp))
                Box(GlanceModifier.height(h).cornerRadius(h / 2).background(Hf.raised).clickable(act("tvpc")).padding(horizontal = 14.dp), contentAlignment = Alignment.Center) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(R.drawable.lc_monitor, 18.dp)
                        Label("PC", 13.sp, modifier = GlanceModifier.padding(start = 6.dp))
                    }
                }
            }
        }
    }
}
