package app.homefront

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.enableEdgeToEdge
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.min

/**
 * A real touchpad for the HTPC, opened from the Touchpad widget. Same gestures as the web
 * remote: drag to move, tap to click, two fingers to scroll, hold or two-finger tap to
 * right-click, tap then drag to drag. It holds its own socket while it's open, for low latency.
 */
class TouchpadActivity : ComponentActivity() {
    private var ws: WebSocket? = null
    private val dp by lazy { resources.displayMetrics.density }
    private fun px(v: Int) = (v * dp).toInt()

    private fun send(msg: JSONObject) { if (ws?.send(msg.toString()) != true) Input.send(msg) }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(SystemBarStyle.dark(Color.TRANSPARENT), SystemBarStyle.dark(Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        window.decorView.setBackgroundColor(INK)

        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(px(16), px(8), px(16), px(12)) }

        // Header: title, what it does, close.
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        head.addView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(text("Touchpad", 22f, CHALK))
            addView(text("Tap to click · two fingers to scroll · hold to right-click", 13f, MUTED))
        }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        head.addView(iconButton(R.drawable.lc_x, 44) { finish() })
        root.addView(head, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = px(12) })

        // The pad, with a scroll strip down the right edge.
        val padFrame = FrameLayout(this)
        padFrame.addView(PadView(this), FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        padFrame.addView(StripView(this), FrameLayout.LayoutParams(px(44), MATCH_PARENT, Gravity.END))
        root.addView(padFrame, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))

        // Mouse buttons and the keyboard.
        val keys = KeyCatcher(this)
        val buttons = row(top = 12)
        buttons.addView(holdButton("Left"), LinearLayout.LayoutParams(0, px(56), 1f).apply { marginEnd = px(8) })
        buttons.addView(pill("Right") { send(JSONObject().put("t", "click").put("b", "right")) }, LinearLayout.LayoutParams(0, px(56), 1f).apply { marginEnd = px(8) })
        buttons.addView(iconButton(R.drawable.lc_keyboard, 56, square = true) { keys.toggle() })
        root.addView(buttons)

        // Keys that a phone keyboard doesn't have, and the media strip.
        val nav = row(top = 8)
        listOf("Esc" to "esc", "Tab" to "tab").forEach { (label, k) ->
            nav.addView(pill(label, 15f) { key(k) }, LinearLayout.LayoutParams(0, px(44), 1f).apply { if (nav.childCount > 0) marginStart = px(6) })
        }
        listOf(R.drawable.lc_arrow_left to "left", R.drawable.lc_arrow_up to "up", R.drawable.lc_arrow_down to "down",
            R.drawable.lc_arrow_right to "right", R.drawable.lc_corner_down_left to "enter").forEach { (icon, k) ->
            nav.addView(iconButton(icon, 44, square = true) { key(k) }, LinearLayout.LayoutParams(0, px(44), 1f).apply { marginStart = px(6) })
        }
        root.addView(nav)

        val media = row(top = 8)
        val mediaKeys = listOf(
            R.drawable.lc_skip_back to { post("/api/pc/media/prev") },
            R.drawable.lc_play to { post("/api/pc/media/toggle") },
            R.drawable.lc_skip_forward to { post("/api/pc/media/next") },
            R.drawable.lc_volume_1 to { pcVolume(-2) },
            R.drawable.lc_volume_2 to { pcVolume(2) },
        )
        mediaKeys.forEachIndexed { i, (icon, run) ->
            media.addView(iconButton(icon, 44, square = true) { run() }, LinearLayout.LayoutParams(0, px(44), 1f).apply { if (i > 0) marginStart = px(6) })
        }
        root.addView(media)
        root.addView(keys, LinearLayout.LayoutParams(1, 1))

        setContentView(root)
        ViewCompat.setOnApplyWindowInsetsListener(root) { v, insets ->
            val b = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.ime())
            v.setPadding(px(16) + b.left, px(8) + b.top, px(16) + b.right, px(12) + b.bottom); WindowInsetsCompat.CONSUMED
        }
    }

    override fun onResume() {
        super.onResume()
        if (Prefs.signedIn) ws = Api.openSocket(object : WebSocketListener() {
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) { if (ws === webSocket) ws = null }
        })
    }

    override fun onPause() {
        ws?.close(1000, null); ws = null
        super.onPause()
    }

    private fun key(k: String) = send(JSONObject().put("t", "key").put("k", k))
    private fun post(path: String) { lifecycleScope.launch { runCatching { Api.post(path) } } }
    private fun pcVolume(delta: Int) {
        val level = (Store.state.value.pcVolume + delta).coerceIn(0, 100)
        Store.patchPcVolume(level, null)
        lifecycleScope.launch { runCatching { Api.post("/api/pc/volume", JSONObject().put("level", level)) } }
    }

    // ---------------- the pad ----------------

    private inner class PadView(ctx: Context) : View(ctx) {
        private val bg = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = RAISED }
        private val hint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = FAINT; textSize = 14 * dp; textAlign = Paint.Align.CENTER }
        private val ripple = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = ACCENT; style = Paint.Style.STROKE; strokeWidth = 2 * dp }
        private var rippleAt: Pair<Float, Float>? = null
        private var rippleStart = 0L

        private var lastX = 0f; private var lastY = 0f
        private var start = 0L; private var moved = 0f
        private var two = false; private var dragArm = false; private var dragging = false; private var longFired = false
        private var lastTap = 0L
        private var accX = 0.0; private var accY = 0.0; private var flushQueued = false
        private val longPress = Runnable {
            if (moved < 8 && !dragArm && !two) { longFired = true; send(JSONObject().put("t", "click").put("b", "right")); buzz(); showRipple(lastX, lastY) }
        }

        override fun onDraw(c: Canvas) {
            c.drawRoundRect(RectF(0f, 0f, width.toFloat(), height.toFloat()), 24 * dp, 24 * dp, bg)
            c.drawText("Drag to move the pointer", width / 2f, height / 2f, hint)
            rippleAt?.let { (x, y) ->
                val t = (System.currentTimeMillis() - rippleStart) / 400f
                if (t >= 1f) { rippleAt = null; return@let }
                ripple.alpha = ((1 - t) * 255).toInt()
                c.drawCircle(x, y, (12 + 36 * t) * dp, ripple)
                postInvalidateOnAnimation()
            }
        }

        private fun showRipple(x: Float, y: Float) { rippleAt = x to y; rippleStart = System.currentTimeMillis(); invalidate() }
        private fun buzz() = performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)

        private fun queue(dx: Double, dy: Double) {
            accX += dx; accY += dy
            if (flushQueued) return
            flushQueued = true
            postOnAnimation {
                flushQueued = false
                if (abs(accX) + abs(accY) >= 0.3) send(JSONObject().put("t", "move").put("dx", accX).put("dy", accY))
                accX = 0.0; accY = 0.0
            }
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    lastX = e.x; lastY = e.y; start = System.currentTimeMillis(); moved = 0f
                    two = false; dragging = false; longFired = false
                    // A tap then a drag straight after holds the button down: drag windows, select text.
                    dragArm = start - lastTap < 300
                    postDelayed(longPress, 550)
                }
                MotionEvent.ACTION_POINTER_DOWN -> { two = true; removeCallbacks(longPress); lastX = avgX(e); lastY = avgY(e) }
                MotionEvent.ACTION_MOVE -> {
                    val x = if (two) avgX(e) else e.x
                    val y = if (two) avgY(e) else e.y
                    val dx = (x - lastX) / dp; val dy = (y - lastY) / dp
                    lastX = x; lastY = y
                    moved += abs(dx) + abs(dy)
                    if (moved > 8) removeCallbacks(longPress)
                    if (two) { send(JSONObject().put("t", "scroll").put("dy", dy * 4.0).put("dx", -dx * 4.0)); return true }
                    if (dragArm && !dragging && moved > 8) { dragging = true; send(JSONObject().put("t", "button").put("b", "left").put("down", true)) }
                    val speed = hypot(dx, dy)
                    val gain = Store.state.value.screenW / (width / dp) * (0.35 + min(speed, 30f) / 28.0)
                    queue(dx * gain, dy * gain)
                }
                MotionEvent.ACTION_POINTER_UP -> {
                    // Keep scrolling smoothly with whichever finger is left.
                    val keep = if (e.actionIndex == 0) 1 else 0
                    lastX = e.getX(keep); lastY = e.getY(keep)
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    removeCallbacks(longPress)
                    if (dragging) { send(JSONObject().put("t", "button").put("b", "left").put("down", false)); return true }
                    if (longFired || e.actionMasked == MotionEvent.ACTION_CANCEL) return true
                    val quick = System.currentTimeMillis() - start < 300 && moved < 10
                    if (!quick) return true
                    if (two) { send(JSONObject().put("t", "click").put("b", "right")); buzz() }
                    else { send(JSONObject().put("t", "click").put("b", "left")); lastTap = System.currentTimeMillis() }
                    showRipple(e.x, e.y)
                }
            }
            return true
        }

        private fun avgX(e: MotionEvent) = (0 until e.pointerCount).map { e.getX(it) }.average().toFloat()
        private fun avgY(e: MotionEvent) = (0 until e.pointerCount).map { e.getY(it) }.average().toFloat()
    }

    /** One-finger scrolling down the right edge. */
    private inner class StripView(ctx: Context) : View(ctx) {
        private val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x14E6ECF4 }
        private val dot = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = FAINT }
        private var lastY = 0f
        override fun onDraw(c: Canvas) {
            c.drawRoundRect(RectF(6 * dp, 12 * dp, width - 6 * dp, height - 12 * dp), 16 * dp, 16 * dp, p)
            for (i in -1..1) c.drawCircle(width / 2f, height / 2f + i * 10 * dp, 2 * dp, dot)
        }
        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> lastY = e.y
                MotionEvent.ACTION_MOVE -> { val dy = (e.y - lastY) / dp; lastY = e.y; send(JSONObject().put("t", "scroll").put("dy", dy * 5.0).put("dx", 0)) }
            }
            return true
        }
    }

    /** Invisible view that takes the phone keyboard and sends each key to the HTPC as it's pressed. */
    private inner class KeyCatcher(ctx: Context) : View(ctx) {
        private var open = false
        init { isFocusableInTouchMode = true; isFocusable = true }

        fun toggle() {
            val imm = getSystemService(InputMethodManager::class.java)
            open = !open
            if (open) { requestFocus(); imm.showSoftInput(this, 0) } else imm.hideSoftInputFromWindow(windowToken, 0)
        }

        override fun onCheckIsTextEditor() = true

        override fun onCreateInputConnection(out: EditorInfo): InputConnection {
            // Visible-password stops keyboards holding letters back as suggestions: every key goes now.
            out.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            out.imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN
            return object : BaseInputConnection(this, false) {
                override fun commitText(text: CharSequence, newCursorPosition: Int): Boolean {
                    val s = text.toString()
                    when (s) { "\n" -> key("enter"); "" -> {}; else -> send(JSONObject().put("t", "type").put("s", s)) }
                    return true
                }
                override fun setComposingText(text: CharSequence, newCursorPosition: Int) = commitText(text, newCursorPosition)
                override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                    repeat(beforeLength.coerceAtMost(20)) { key("backspace") }; return true
                }
                override fun sendKeyEvent(event: KeyEvent): Boolean {
                    if (event.action != KeyEvent.ACTION_DOWN) return true
                    when (event.keyCode) {
                        KeyEvent.KEYCODE_DEL -> key("backspace")
                        KeyEvent.KEYCODE_FORWARD_DEL -> key("delete")
                        KeyEvent.KEYCODE_ENTER -> key("enter")
                        KeyEvent.KEYCODE_TAB -> key("tab")
                        KeyEvent.KEYCODE_SPACE -> key("space")
                        else -> event.unicodeChar.takeIf { it > 0 }?.let { send(JSONObject().put("t", "type").put("s", it.toChar().toString())) }
                    }
                    return true
                }
                override fun performEditorAction(actionCode: Int): Boolean { key("enter"); return true }
            }
        }
    }

    // ---------------- little view helpers ----------------

    private fun text(s: String, size: Float, color: Int) = TextView(this).apply { text = s; textSize = size; setTextColor(color) }

    private fun row(top: Int) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = px(top) }
    }

    private fun rounded(color: Int, radius: Int = 16) = GradientDrawable().apply { setColor(color); cornerRadius = radius * dp }

    private fun pill(label: String, size: Float = 16f, onTap: () -> Unit) = TextView(this).apply {
        text = label; textSize = size; setTextColor(CHALK); gravity = Gravity.CENTER
        background = rounded(RAISED)
        setOnClickListener { performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP); onTap() }
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun holdButton(label: String) = pill(label) {}.apply {
        setOnClickListener(null)
        setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { v.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP); v.alpha = 0.7f; send(JSONObject().put("t", "button").put("b", "left").put("down", true)) }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> { v.alpha = 1f; send(JSONObject().put("t", "button").put("b", "left").put("down", false)) }
            }
            true
        }
    }

    private fun iconButton(res: Int, sizeDp: Int, square: Boolean = false, onTap: () -> Unit) = ImageView(this).apply {
        setImageResource(res); setColorFilter(CHALK)
        val pad = px(sizeDp / 4); setPadding(pad, pad, pad, pad)
        background = if (square) rounded(RAISED) else GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(RAISED) }
        layoutParams = LinearLayout.LayoutParams(px(sizeDp), px(sizeDp))
        setOnClickListener { performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP); onTap() }
    }

    companion object {
        private const val INK = 0xFF0B1220.toInt()
        private const val RAISED = 0xFF172238.toInt()
        private const val CHALK = 0xFFE6ECF4.toInt()
        private const val MUTED = 0x99E6ECF4.toInt()
        private const val FAINT = 0x5CE6ECF4
        private const val ACCENT = 0xFFFFC94D.toInt()
    }
}
