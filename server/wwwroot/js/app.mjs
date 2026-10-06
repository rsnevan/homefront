import { render } from 'preact';
import { useState, useEffect } from 'preact/hooks';
import { html, Icon, Mark, Link, Toasts, useStore, useRoute, connect, api, navigate, act } from './lib.mjs';
import { Home } from './views/home.mjs';
import { Remote } from './views/remote.mjs';
import { Library } from './views/library.mjs';
import { LightsView } from './views/lights.mjs';
import { Settings } from './views/settings.mjs';
import { Ambient, RoutineScreen, Player } from './views/tv.mjs';

const NAV = [
  ['/', 'house', 'Home'],
  ['/remote', 'mouse-pointer-2', 'Remote'],
  ['/library', 'clapperboard', 'Library'],
  ['/lights', 'lightbulb', 'Lights'],
  ['/settings', 'sliders-horizontal', 'Settings'],
];

function Shell({ children }) {
  const s = useStore();
  const path = useRoute();
  useEffect(() => { document.title = s.home?.name ? `${s.home.name} · homefront` : 'homefront'; }, [s.home?.name]);
  return html`
    <div class="shell">
      <nav class="rail" aria-label="Main">
        <div class="brand"><${Mark} /><b>homefront</b></div>
        ${NAV.map(([to, icon, label]) => html`<${Link} to=${to} class="nav-item"><${Icon} name=${icon} /><span>${label}</span></${Link}>`)}
        <div class="rail-foot">
          <div class="status-line" title=${s.ha?.connected ? 'Home Assistant connected' : 'Home Assistant offline'}><span class=${'dot' + (s.ha?.connected ? ' live' : ' bad')}></span><span>Home Assistant</span></div>
          <div class="status-line" title=${s.jellyfin?.ok ? 'Jellyfin connected' : 'Jellyfin offline'}><span class=${'dot' + (s.jellyfin?.ok ? ' live' : ' bad')}></span><span>Jellyfin</span></div>
          ${s.me?.role === 'guest' && html`<div class="status-line"><${Icon} name="ticket" size=${14} /><span>Guest: ${s.me.name}</span></div>`}
        </div>
      </nav>
      <main id="main">${children}</main>
      <nav class="tabbar" aria-label="Main">
        ${NAV.map(([to, icon, label]) => html`<${Link} to=${to}><${Icon} name=${icon} /><span>${label}</span></${Link}>`)}
      </nav>
      ${s.snapshot && !s.connected && html`<div class="offline" role="status"><span class="dot bad"></span>Reconnecting to the HTPC</div>`}
      <${Toasts} />
    </div>`;
}

function Login({ onDone, expired }) {
  const [u, setU] = useState('');
  const [p, setP] = useState('');
  const [err, setErr] = useState(expired ? 'That guest pass has expired or been turned off.' : null);
  const [busy, setBusy] = useState(false);
  const submit = async e => {
    e.preventDefault(); setBusy(true); setErr(null);
    try { await api('/api/login', { username: u, password: p }); onDone(); } catch (e2) { setErr(e2.message); } finally { setBusy(false); }
  };
  return html`<div class="login">
    <form onSubmit=${submit}>
      <div class="mark"><${Mark} size=${64} /></div>
      <h1>Sign in to homefront</h1>
      <p class="muted" style="margin:0 0 10px">Lights, TV and the HTPC, from anything with a browser.</p>
      <div class="field"><label for="lu">Username</label><input id="lu" class="input" autocomplete="username" autocapitalize="off" value=${u} onInput=${e => setU(e.target.value)} autofocus /></div>
      <div class="field"><label for="lp">Password</label><input id="lp" class="input" type="password" autocomplete="current-password" value=${p} onInput=${e => setP(e.target.value)} /></div>
      ${err && html`<div class="err" role="alert">${err}</div>`}
      <button class="btn primary" style="min-height:48px;margin-top:6px" disabled=${busy || !u || !p}>${busy ? 'Signing in…' : 'Sign in'}</button>
    </form>
  </div>`;
}

/** Android share sheet → open the shared link on the TV. */
function Share() {
  useEffect(() => {
    const q = new URLSearchParams(location.search);
    const text = [q.get('url'), q.get('text'), q.get('title')].filter(Boolean).join(' ');
    const url = text.match(/https?:\/\/\S+/)?.[0];
    (url ? act('/api/pc/open', { url }, 'Opening on the TV') : Promise.resolve()).finally(() => navigate('/', true));
  }, []);
  return html`<div class="login"><p class="muted">Sending to the TV…</p></div>`;
}

function App() {
  const path = useRoute();
  const [me, setMe] = useState(undefined);
  const check = () => api('/api/me').then(setMe).catch(() => setMe({ role: null }));
  useEffect(() => { check(); }, []);
  useEffect(() => { if (me?.role) connect(); }, [me?.role]);

  if (me === undefined) return null;
  if (!me.role) return html`<${Login} onDone=${check} expired=${new URLSearchParams(location.search).get('guest') === 'expired'} />`;
  if (path === '/ambient') return html`<${Ambient} />`;
  if (path === '/routine') return html`<${RoutineScreen} />`;
  if (path === '/player') return html`<${Player} />`;
  if (path === '/share') return html`<${Share} />`;

  const view = path.startsWith('/remote') ? html`<${Remote} />`
    : path.startsWith('/library') ? html`<${Library} />`
    : path.startsWith('/lights') ? html`<${LightsView} />`
    : path.startsWith('/settings') ? html`<${Settings} />`
    : html`<${Home} />`;
  return html`<${Shell}>${view}</${Shell}>`;
}

render(html`<${App} />`, document.getElementById('app'));
