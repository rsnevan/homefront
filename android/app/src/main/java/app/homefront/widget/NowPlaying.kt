package app.homefront.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.LinearProgressIndicator
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.ContentScale
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import app.homefront.HomeState
import app.homefront.R
import app.homefront.Store

class NowPlayingReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget = NowPlayingWidget() }

/** What's playing on the HTPC: art, title, play / skip, and the PC's volume. */
class NowPlayingWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) = provideContent {
        val s by Store.state.collectAsState()
        if (!Blocked(s)) NowPlaying(s)
    }
}

@Composable
private fun NowPlaying(s: HomeState) {
    val size = LocalSize.current
    val m = s.media
    val tall = size.height >= 150.dp
    val roomy = size.height >= 100.dp
    val wide = size.width >= 230.dp
    val title = when {
        m.title != null -> m.title
        m.active -> m.app ?: "Playing"
        else -> "Nothing playing"
    }
    val sub = listOfNotNull(m.artist, m.app.takeIf { m.title != null }).joinToString(" · ").ifEmpty {
        staleText(s) ?: if (m.active) "" else "Tap to open the remote"
    }

    Frame(padding = 12.dp, onClick = openApp("/remote")) {
        Row(GlanceModifier.fillMaxWidth().defaultWeight(), verticalAlignment = Alignment.CenterVertically) {
            val art = when {
                tall -> 72.dp
                roomy -> 52.dp
                else -> (size.height - 24.dp).coerceIn(32.dp, 44.dp)
            }
            Art(s, art)
            Spacer(GlanceModifier.width(12.dp))
            Column(GlanceModifier.defaultWeight()) {
                Label(title, if (tall) 17.sp else 15.sp, bold = true, lines = if (tall) 2 else 1)
                if (sub.isNotEmpty()) Label(sub, 12.sp, Hf.muted, lines = 1)
                if (tall && m.duration > 0) {
                    Spacer(GlanceModifier.height(8.dp))
                    val pos = if (m.playing) m.position + (System.currentTimeMillis() - m.updatedAt) / 1000.0 else m.position
                    LinearProgressIndicator(
                        progress = (pos / m.duration).toFloat().coerceIn(0f, 1f),
                        modifier = GlanceModifier.fillMaxWidth().height(3.dp),
                        color = Hf.accent, backgroundColor = Hf.raised,
                    )
                }
            }
            if (!roomy) {
                // One row: just the essentials beside the title.
                Spacer(GlanceModifier.width(8.dp))
                if (wide && m.canPrev) { RoundButton(R.drawable.lc_skip_back, act("media", "prev"), 36.dp); Spacer(GlanceModifier.width(6.dp)) }
                PlayButton(s, 40.dp)
                if (wide) { Spacer(GlanceModifier.width(6.dp)); RoundButton(R.drawable.lc_skip_forward, act("media", "next"), 36.dp) }
            }
        }
        if (roomy) {
            Spacer(GlanceModifier.height(10.dp))
            Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                RoundButton(R.drawable.lc_skip_back, act("media", "prev"), 40.dp)
                Spacer(GlanceModifier.width(8.dp))
                PlayButton(s, 48.dp)
                Spacer(GlanceModifier.width(8.dp))
                RoundButton(R.drawable.lc_skip_forward, act("media", "next"), 40.dp)
                if (wide) {
                    Spacer(GlanceModifier.defaultWeight())
                    Volume(s)
                }
            }
        }
    }
}

@Composable
private fun PlayButton(s: HomeState, size: androidx.compose.ui.unit.Dp) =
    RoundButton(if (s.media.playing) R.drawable.lc_pause else R.drawable.lc_play, act("media", "toggle"), size, primary = true)

@Composable
private fun Volume(s: HomeState) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        RoundButton(R.drawable.lc_minus, act("pcvol", num = -1), 36.dp)
        Box(GlanceModifier.width(44.dp).clickable(act("pcmute")), contentAlignment = Alignment.Center) {
            if (s.pcMuted) Icon(R.drawable.lc_volume_x, 18.dp, Hf.muted)
            else Label("${s.pcVolume}", 15.sp, bold = true)
        }
        RoundButton(R.drawable.lc_plus, act("pcvol", num = 1), 36.dp)
    }
}

@Composable
private fun Art(s: HomeState, size: androidx.compose.ui.unit.Dp) {
    val bmp = s.art
    if (bmp != null) {
        Image(ImageProvider(bmp), contentDescription = null, contentScale = ContentScale.Crop,
            modifier = GlanceModifier.size(size).cornerRadius(12.dp))
    } else {
        Box(GlanceModifier.size(size).cornerRadius(12.dp).background(Hf.raised), contentAlignment = Alignment.Center) {
            Icon(if (s.media.isVideo) R.drawable.lc_clapperboard else R.drawable.lc_music, size * 0.42f, if (s.media.active) Hf.accent else Hf.faint)
        }
    }
}
