package app.homefront

import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.webkit.CookieManager
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.enableEdgeToEdge
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.WindowCompat
import androidx.lifecycle.lifecycleScope
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import kotlinx.coroutines.launch

/**
 * The full homefront app, in a WebView pointed at the HTPC. The web UI already does everything;
 * this wraps it so it's a real app (its own icon, no browser chrome, widgets, share target) and
 * fills the gaps plain HTTP leaves: clipboard paste on the remote works without HTTPS.
 */
class MainActivity : ComponentActivity() {
    private lateinit var web: WebView
    private lateinit var root: FrameLayout
    private lateinit var error: LinearLayout
    private lateinit var errorText: TextView
    private var fullscreen: View? = null
    private var fullscreenCallback: WebChromeClient.CustomViewCallback? = null
    private var loadedUrl: String? = null

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        if (!Prefs.configured) { startActivity(Intent(this, SetupActivity::class.java)); finish(); return }

        root = FrameLayout(this)
        web = WebView(this).apply {
            setBackgroundColor(getColor(R.color.hf_bg))
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.mediaPlaybackRequiresUserGesture = false
            settings.userAgentString = settings.userAgentString + " homefront-android/${BuildConfig.VERSION_NAME}"
            addJavascriptInterface(Bridge(), "HomefrontNative")
            webViewClient = Client()
            webChromeClient = Chrome()
        }
        if (WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) {
            WebViewCompat.addDocumentStartJavaScript(web, BRIDGE_JS, setOf("*"))
        }
        root.addView(web, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        root.addView(buildError(), FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        setContentView(root)

        ViewCompat.setOnApplyWindowInsetsListener(root) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.ime() or WindowInsetsCompat.Type.displayCutout())
            if (fullscreen == null) v.setPadding(bars.left, bars.top, bars.right, bars.bottom) else v.setPadding(0, 0, 0, 0)
            WindowInsetsCompat.CONSUMED
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    fullscreen != null -> (web.webChromeClient as Chrome).onHideCustomView()
                    web.canGoBack() -> web.goBack()
                    else -> { isEnabled = false; onBackPressedDispatcher.onBackPressed(); isEnabled = true }
                }
            }
        })

        // A session saved by an earlier install of the WebView data goes back into the cookie jar.
        Prefs.session?.let { s ->
            if (CookieManager.getInstance().getCookie(Prefs.url)?.contains("${Api.COOKIE}=") != true)
                CookieManager.getInstance().setCookie(Prefs.url, "${Api.COOKIE}=$s; path=/; max-age=31536000")
        }
        open(savedInstanceState == null, intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        open(true, intent)
    }

    override fun onResume() {
        super.onResume()
        if (!::web.isInitialized) return
        web.onResume()
        // A different address may have been saved in Setup.
        if (loadedUrl != null && !loadedUrl!!.startsWith(Prefs.url)) open(true, null)
        LiveService.start(this)
        if (!LiveService.connected) lifecycleScope.launch { Store.refresh() }
    }

    override fun onPause() {
        if (::web.isInitialized) { web.onPause(); syncSession() }
        CookieManager.getInstance().flush()
        super.onPause()
    }

    private fun open(load: Boolean, intent: Intent?) {
        if (!load && loadedUrl != null) return
        val path = when (intent?.action) {
            Intent.ACTION_SEND -> {
                val text = intent.getStringExtra(Intent.EXTRA_TEXT) ?: ""
                val title = intent.getStringExtra(Intent.EXTRA_SUBJECT) ?: ""
                "/share?title=${Uri.encode(title)}&text=${Uri.encode(text)}"
            }
            else -> intent?.data?.takeIf { it.scheme == "homefront" }?.path ?: "/"
        }
        error.visibility = View.GONE
        loadedUrl = Prefs.url + path
        web.loadUrl(loadedUrl!!)
    }

    /** Copy the session cookie out of the WebView so widgets can make calls as you. */
    private fun syncSession() {
        val cookie = CookieManager.getInstance().getCookie(Prefs.url) ?: ""
        val s = cookie.split(";").map { it.trim() }.firstOrNull { it.startsWith("${Api.COOKIE}=") }?.substringAfter("=")
        if (s == Prefs.session) return
        Prefs.session = s
        if (s != null) {
            LiveService.start(this, reconnect = true)
            lifecycleScope.launch { Store.refresh() }
        } else {
            LiveService.stop(this)
            lifecycleScope.launch { Store.refresh() }
        }
    }

    private fun buildError(): View {
        val dp = resources.displayMetrics.density
        error = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(getColor(R.color.hf_bg))
            setPadding((32 * dp).toInt(), 0, (32 * dp).toInt(), 0)
            visibility = View.GONE
        }
        val title = TextView(this).apply { text = "Can't reach homefront"; textSize = 22f; gravity = Gravity.CENTER }
        errorText = TextView(this).apply { textSize = 15f; gravity = Gravity.CENTER; alpha = 0.7f; setPadding(0, (8 * dp).toInt(), 0, (24 * dp).toInt()) }
        val retry = Button(this).apply { text = "Try again"; setOnClickListener { open(true, null) } }
        val change = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
            text = "Change address"; setOnClickListener { startActivity(Intent(this@MainActivity, SetupActivity::class.java)) }
        }
        listOf(title, errorText, retry, change).forEach { error.addView(it, LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply { gravity = Gravity.CENTER_HORIZONTAL }) }
        return error
    }

    private inner class Client : WebViewClient() {
        override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
            val u = request.url
            val home = Uri.parse(Prefs.url)
            if (u.host == home.host && u.port == home.port) return false
            // Anything off the HTPC (links in the docs, Tailscale) opens in the browser.
            runCatching { startActivity(Intent(Intent.ACTION_VIEW, u)) }
            return true
        }

        override fun onPageFinished(view: WebView, url: String) { syncSession() }

        override fun onReceivedError(view: WebView, request: WebResourceRequest, err: WebResourceError) {
            if (!request.isForMainFrame) return
            errorText.text = "Nothing answered at ${Prefs.url}.\nIs the HTPC on, and is this phone on the home Wi-Fi?"
            error.visibility = View.VISIBLE
        }
    }

    private inner class Chrome : WebChromeClient() {
        override fun onShowCustomView(view: View, callback: CustomViewCallback) {
            fullscreen = view; fullscreenCallback = callback
            root.addView(view, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
            view.setBackgroundColor(Color.BLACK)
            WindowCompat.getInsetsController(window, root).apply {
                hide(WindowInsetsCompat.Type.systemBars())
                systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
            root.requestApplyInsets()
        }

        override fun onHideCustomView() {
            fullscreen?.let { root.removeView(it) }
            fullscreen = null
            fullscreenCallback?.onCustomViewHidden(); fullscreenCallback = null
            WindowCompat.getInsetsController(window, root).show(WindowInsetsCompat.Type.systemBars())
            root.requestApplyInsets()
        }
    }

    /** What the page can ask of the phone. */
    private inner class Bridge {
        private val clip get() = getSystemService(ClipboardManager::class.java)

        @JavascriptInterface fun readClipboard(): String =
            clip.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this@MainActivity)?.toString() ?: ""

        @JavascriptInterface fun writeClipboard(text: String) { clip.setPrimaryClip(ClipData.newPlainText("homefront", text)) }

        @JavascriptInterface fun sessionChanged() { runOnUiThread { syncSession() } }
    }

    companion object {
        /**
         * Runs before the web app's own scripts. Clipboard access normally needs HTTPS; here the
         * phone provides it. Sign-in and sign-out tell the app straight away so widgets follow.
         */
        private val BRIDGE_JS = """
            (function () {
              if (!window.HomefrontNative) return;
              var N = window.HomefrontNative;
              var cb = {
                readText: function () { return Promise.resolve(N.readClipboard()); },
                writeText: function (t) { N.writeClipboard(String(t)); return Promise.resolve(); }
              };
              try { Object.defineProperty(navigator, 'clipboard', { get: function () { return cb; }, configurable: true }); } catch (e) {}
              try { Object.defineProperty(window, 'isSecureContext', { get: function () { return true; }, configurable: true }); } catch (e) {}
              var f = window.fetch;
              window.fetch = function (input, init) {
                var url = typeof input === 'string' ? input : (input && input.url) || '';
                var p = f.apply(this, arguments);
                if (/\/api\/(login|logout)/.test(url)) p.then(function () { setTimeout(function () { N.sessionChanged(); }, 50); }, function () {});
                return p;
              };
            })();
        """.trimIndent()
    }
}
