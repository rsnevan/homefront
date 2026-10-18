package app.homefront.widget

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceModifier
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalContext
import androidx.glance.action.Action
import androidx.glance.action.clickable
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.appWidgetBackground
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.color.ColorProvider
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ColumnScope
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import app.homefront.HomeState
import app.homefront.MainActivity
import app.homefront.R
import java.text.DateFormat
import java.util.Date

/** homefront's palette (server/wwwroot/app.css), light and dark. */
object Hf {
    val bg = ColorProvider(day = Color(0xF2F3F6FA), night = Color(0xF00B1220))
    val raised = ColorProvider(day = Color(0xFFE3E9F1), night = Color(0xFF172238))
    val text = ColorProvider(day = Color(0xFF0F1726), night = Color(0xFFE6ECF4))
    val muted = ColorProvider(day = Color(0x990F1726), night = Color(0x99E6ECF4))
    val faint = ColorProvider(day = Color(0x5C0F1726), night = Color(0x5CE6ECF4))
    val accent = ColorProvider(day = Color(0xFFFFC94D), night = Color(0xFFFFC94D))
    val accentSoft = ColorProvider(day = Color(0x40FFC94D), night = Color(0x26FFC94D))
    val onAccent = ColorProvider(day = Color(0xFF0F1726), night = Color(0xFF0F1726))
    val danger = ColorProvider(day = Color(0xFFD9483B), night = Color(0xFFFF6B5E))

    fun fixed(argb: Int) = ColorProvider(Color(argb))
}

object Widgets {
    suspend fun updateAll(ctx: Context) {
        runCatching { NowPlayingWidget().updateAll(ctx) }
        runCatching { ScenesWidget().updateAll(ctx) }
        runCatching { LightsWidget().updateAll(ctx) }
        runCatching { TvWidget().updateAll(ctx) }
        runCatching { TouchpadWidget().updateAll(ctx) }
    }
}

@SuppressLint("DiscouragedApi")
fun iconRes(ctx: Context, lucide: String): Int {
    val id = ctx.resources.getIdentifier("lc_" + lucide.replace('-', '_'), "drawable", ctx.packageName)
    return if (id != 0) id else R.drawable.lc_lightbulb
}

@Composable
fun openApp(path: String): Action {
    val ctx = LocalContext.current
    return actionStartActivity(
        Intent(ctx, MainActivity::class.java).setAction(Intent.ACTION_VIEW).setData(Uri.parse("homefront:$path"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    )
}

@Composable
fun Frame(padding: Dp = 12.dp, onClick: Action? = null, content: @Composable ColumnScope.() -> Unit) {
    var m = GlanceModifier.fillMaxSize().appWidgetBackground().cornerRadius(26.dp).background(Hf.bg).padding(padding)
    if (onClick != null) m = m.clickable(onClick)
    Column(modifier = m, content = content)
}

@Composable
fun Label(text: String, size: TextUnit = 13.sp, color: ColorProvider = Hf.text, bold: Boolean = false, lines: Int = 1, modifier: GlanceModifier = GlanceModifier) =
    Text(text, modifier, style = TextStyle(color = color, fontSize = size, fontWeight = if (bold) FontWeight.Medium else FontWeight.Normal), maxLines = lines)

@Composable
fun Icon(res: Int, size: Dp = 20.dp, tint: ColorProvider = Hf.text, modifier: GlanceModifier = GlanceModifier) =
    Image(ImageProvider(res), contentDescription = null, modifier = modifier.size(size), colorFilter = ColorFilter.tint(tint))

/** Round button: icon on a raised disc, or on accent when it's the main action or switched on. */
@Composable
fun RoundButton(res: Int, action: Action, size: Dp = 40.dp, primary: Boolean = false, active: Boolean = false, pressed: Boolean = false) {
    val bg = when { primary || active -> Hf.accent; pressed -> Hf.accentSoft; else -> Hf.raised }
    val tint = if (primary || active) Hf.onAccent else Hf.text
    Box(
        GlanceModifier.size(size).cornerRadius(size / 2).background(bg).clickable(action),
        contentAlignment = Alignment.Center,
    ) { Icon(res, size * 0.48f, tint) }
}

/** When the widget can't show the house: not set up, not signed in. Tapping opens the app to fix it. */
@Composable
fun Blocked(state: HomeState): Boolean {
    val msg = when {
        !state.configured -> "Open homefront to connect"
        !state.signedIn -> "Open homefront to sign in"
        else -> return false
    }
    Frame(onClick = openApp("/")) {
        Column(GlanceModifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically, horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(R.drawable.lc_house, 24.dp, Hf.accent)
            Label(msg, 13.sp, Hf.muted, modifier = GlanceModifier.padding(top = 6.dp))
        }
    }
    return true
}

/** Small "offline since" line, so stale numbers never pass for live ones. */
fun staleText(state: HomeState): String? {
    if (state.online) return null
    if (state.updatedAt == 0L) return "Can't reach homefront"
    return "Offline · " + DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(state.updatedAt))
}
