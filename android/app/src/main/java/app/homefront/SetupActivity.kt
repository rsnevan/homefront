package app.homefront

import android.content.Intent
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.inputmethod.EditorInfo
import android.webkit.CookieManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.enableEdgeToEdge
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch

/** Where homefront lives, and whether widgets stay live. Shown on first launch and from the app icon's "Connection" shortcut. */
class SetupActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val dp = resources.displayMetrics.density
        fun px(v: Int) = (v * dp).toInt()

        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(px(28), px(40), px(28), px(28)) }
        fun add(v: android.view.View, top: Int = 0) = col.addView(v, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = px(top) })

        add(TextView(this).apply { text = "homefront"; textSize = 30f })
        add(TextView(this).apply { text = "Where is homefront running? This is the address you open in a browser at home."; textSize = 15f; alpha = 0.7f }, 8)

        val url = EditText(this).apply {
            setText(Prefs.url.ifEmpty { BuildConfig.DEFAULT_URL })
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            imeOptions = EditorInfo.IME_ACTION_GO
            isSingleLine = true
        }
        add(url, 24)
        val status = TextView(this).apply { textSize = 14f; alpha = 0.8f }
        add(status, 8)

        val live = Switch(this).apply {
            text = "Live widgets"
            isChecked = Prefs.live
            textSize = 16f
        }
        add(live, 24)
        add(TextView(this).apply {
            text = "Widgets follow the house the moment anything changes while the screen is on, and pause when it's off. " +
                "Off: they refresh every 15 minutes and whenever you tap them."
            textSize = 13f; alpha = 0.6f
        }, 4)

        val save = Button(this).apply { text = if (Prefs.configured) "Save" else "Connect" }
        add(save, 28)

        val signOut = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
            text = "Sign out of this phone"
            visibility = if (Prefs.signedIn) android.view.View.VISIBLE else android.view.View.GONE
            setOnClickListener {
                CookieManager.getInstance().removeAllCookies(null)
                Prefs.session = null
                LiveService.stop(this@SetupActivity)
                lifecycleScope.launch { Store.refresh() }
                visibility = android.view.View.GONE
                status.text = "Signed out. Open homefront to sign in again."
            }
        }
        add(signOut, 8)

        fun go() {
            var u = url.text.toString().trim().trimEnd('/')
            if (!u.startsWith("http://") && !u.startsWith("https://")) u = "http://$u"
            save.isEnabled = false
            status.text = "Checking $u …"
            lifecycleScope.launch {
                val ok = Api.probe(u)
                save.isEnabled = true
                if (!ok) { status.text = "homefront didn't answer at $u. Check the address, and that this phone is on the home Wi-Fi."; return@launch }
                val moved = u != Prefs.url
                Prefs.url = u
                Prefs.live = live.isChecked
                if (moved) Prefs.session = null   // a session belongs to one server
                if (Prefs.live) LiveService.start(this@SetupActivity, reconnect = true) else LiveService.stop(this@SetupActivity)
                Store.refresh()
                startActivity(Intent(this@SetupActivity, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP))
                finish()
            }
        }
        save.setOnClickListener { go() }
        url.setOnEditorActionListener { _, a, _ -> if (a == EditorInfo.IME_ACTION_GO) { go(); true } else false }

        val scroll = ScrollView(this).apply { addView(col); isFillViewport = true }
        col.gravity = Gravity.TOP
        setContentView(scroll)
        ViewCompat.setOnApplyWindowInsetsListener(scroll) { v, insets ->
            val b = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.ime())
            v.setPadding(b.left, b.top, b.right, b.bottom); WindowInsetsCompat.CONSUMED
        }
    }
}
