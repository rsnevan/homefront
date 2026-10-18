package app.homefront.widget

import android.content.Context
import android.content.Intent
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.LocalContext
import androidx.glance.LocalSize
import androidx.glance.action.Action
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.actionStartActivity
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
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import app.homefront.HomeState
import app.homefront.R
import app.homefront.Store
import app.homefront.TouchpadActivity

class TouchpadReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget = TouchpadWidget() }

/**
 * Widgets only get taps, never drags, so the real touchpad is a full-screen pad one tap away.
 * The widget's own quick controls (nudge, click, scroll) stay locked until you unlock them,
 * so brushing the home screen never clicks anything on the TV.
 */
class TouchpadWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) = provideContent {
        val s by Store.state.collectAsState()
        if (!Blocked(s)) Touchpad(s)
    }
}

@Composable
private fun openPad(): Action {
    val ctx = LocalContext.current
    return actionStartActivity(Intent(ctx, TouchpadActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
}

@Composable
private fun Touchpad(s: HomeState) {
    val armed = s.padArmedUntil > System.currentTimeMillis()
    Frame(padding = 10.dp) {
        if (armed) Armed(s) else Locked(s)
    }
}

@Composable
private fun Locked(s: HomeState) {
    Row(GlanceModifier.fillMaxSize()) {
        // The whole surface opens the full touchpad; opening it sends nothing to the PC.
        Column(
            GlanceModifier.defaultWeight().fillMaxHeight().cornerRadius(18.dp).background(Hf.raised).clickable(openPad()),
            verticalAlignment = Alignment.CenterVertically, horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(R.drawable.lc_mouse_pointer_2, 28.dp, Hf.accent)
            Label("Touchpad", 16.sp, bold = true, modifier = GlanceModifier.padding(top = 8.dp))
            Label(staleText(s) ?: "Tap to open", 12.sp, Hf.muted)
        }
        Spacer(GlanceModifier.width(8.dp))
        Column(
            GlanceModifier.width(64.dp).fillMaxHeight().cornerRadius(18.dp).background(Hf.raised).clickable(act("padarm")),
            verticalAlignment = Alignment.CenterVertically, horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(R.drawable.lc_lock, 20.dp, Hf.muted)
            Label("Quick", 11.sp, Hf.muted, modifier = GlanceModifier.padding(top = 6.dp))
        }
    }
}

@Composable
private fun Armed(s: HomeState) {
    val size = LocalSize.current
    val pad = (size.height - 20.dp).coerceAtMost(size.width * 0.5f)
    val cell = (pad - 8.dp) / 3
    Row(GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
        // Nudge pad: arrows move the pointer a step, the middle clicks.
        Column(GlanceModifier.size(pad)) {
            Row { Spacer(GlanceModifier.size(cell + 4.dp)); Key(R.drawable.lc_arrow_up, act("nudge", "up"), cell); }
            Spacer(GlanceModifier.height(4.dp))
            Row {
                Key(R.drawable.lc_arrow_left, act("nudge", "left"), cell)
                Spacer(GlanceModifier.width(4.dp))
                Key(R.drawable.lc_mouse_pointer_click, act("pclick", "left"), cell, primary = true)
                Spacer(GlanceModifier.width(4.dp))
                Key(R.drawable.lc_arrow_right, act("nudge", "right"), cell)
            }
            Spacer(GlanceModifier.height(4.dp))
            Row { Spacer(GlanceModifier.size(cell + 4.dp)); Key(R.drawable.lc_arrow_down, act("nudge", "down"), cell) }
        }
        Spacer(GlanceModifier.width(8.dp))
        Column(GlanceModifier.defaultWeight().fillMaxHeight()) {
            Row(GlanceModifier.fillMaxWidth().defaultWeight()) {
                Chip("Right click", null, act("pclick", "right"))
                Spacer(GlanceModifier.width(6.dp))
                Chip("Esc", null, act("pkey", "esc"))
            }
            Spacer(GlanceModifier.height(6.dp))
            Row(GlanceModifier.fillMaxWidth().defaultWeight()) {
                Chip(null, R.drawable.lc_chevron_up, act("pscroll", num = -240))
                Spacer(GlanceModifier.width(6.dp))
                Chip(null, R.drawable.lc_chevron_down, act("pscroll", num = 240))
            }
            Spacer(GlanceModifier.height(6.dp))
            Row(GlanceModifier.fillMaxWidth().defaultWeight()) {
                Chip("Open pad", null, openPad())
                Spacer(GlanceModifier.width(6.dp))
                Chip("Lock", R.drawable.lc_lock, act("padarm"), accent = true)
            }
        }
    }
}

@Composable
private fun Key(res: Int, action: Action, size: Dp, primary: Boolean = false) {
    Box(
        GlanceModifier.size(size).cornerRadius(14.dp).background(if (primary) Hf.accent else Hf.raised).clickable(action),
        contentAlignment = Alignment.Center,
    ) { Icon(res, 20.dp, if (primary) Hf.onAccent else Hf.text) }
}

@Composable
private fun androidx.glance.layout.RowScope.Chip(label: String?, icon: Int?, action: Action, accent: Boolean = false) {
    Box(
        GlanceModifier.defaultWeight().fillMaxHeight().cornerRadius(14.dp).background(if (accent) Hf.accentSoft else Hf.raised).clickable(action),
        contentAlignment = Alignment.Center,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (icon != null) Icon(icon, 18.dp, if (accent) Hf.accent else Hf.text)
            if (label != null) Label(label, 13.sp, if (accent) Hf.accent else Hf.text, modifier = GlanceModifier.padding(start = if (icon != null) 6.dp else 0.dp))
        }
    }
}
