package app.homefront.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.LocalContext
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
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import app.homefront.HomeState
import app.homefront.Scene
import app.homefront.Store

class ScenesReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget = ScenesWidget() }

/** Every scene as a big tile; the one you just tapped lights up while it runs. */
class ScenesWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) = provideContent {
        val s by Store.state.collectAsState()
        if (!Blocked(s)) Scenes(s)
    }
}

@Composable
private fun Scenes(s: HomeState) {
    val size = LocalSize.current
    val n = s.scenes.size
    // As square as the space allows: one row when it's a strip, two or three when there's height.
    val rows = when {
        size.height < 90.dp -> 1
        size.height < 200.dp || size.width > size.height * 1.6f -> 2
        else -> 3
    }.coerceAtMost(n)
    val cols = (n + rows - 1) / rows
    val showLabels = size.height / rows >= 54.dp && size.width / cols >= 64.dp
    val recent = s.lastScene.takeIf { System.currentTimeMillis() - s.lastSceneAt < 90_000 }

    Frame(padding = 8.dp) {
        for (r in 0 until rows) {
            if (r > 0) Spacer(GlanceModifier.height(6.dp))
            Row(GlanceModifier.fillMaxWidth().defaultWeight()) {
                for (c in 0 until cols) {
                    if (c > 0) Spacer(GlanceModifier.width(6.dp))
                    val i = r * cols + c
                    Box(GlanceModifier.defaultWeight().fillMaxHeight()) {
                        if (i < n) Tile(s.scenes[i], s.scenes[i].id == recent, showLabels)
                    }
                }
            }
        }
    }
}

@Composable
private fun Tile(scene: Scene, active: Boolean, label: Boolean) {
    val ctx = LocalContext.current
    Column(
        GlanceModifier.fillMaxSize().cornerRadius(16.dp).background(if (active) Hf.accent else Hf.raised).clickable(act("scene", scene.id)).padding(4.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(iconRes(ctx, scene.icon), 22.dp, if (active) Hf.onAccent else Hf.text)
        if (label) Label(scene.name, 12.sp, if (active) Hf.onAccent else Hf.muted, modifier = GlanceModifier.padding(top = 4.dp))
    }
}
