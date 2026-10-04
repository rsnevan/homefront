import { h } from 'preact';
import { useState, useEffect, useRef, useCallback } from 'preact/hooks';
import htm from 'htm';
import { ICONS } from './icons.mjs';

export const html = htm.bind(h);

export function Icon({ name, size, cls = '' }) {
  const s = size ? `width:${size}px;height:${size}px` : '';
  return html`<svg class=${'i ' + cls} style=${s} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" dangerouslySetInnerHTML=${{ __html: ICONS[name] || '' }}></svg>`;
}

export const Mark = ({ size = 30, dark = false }) => html`
  <svg width=${size} height=${size} viewBox="0 0 48 48" fill="none" aria-hidden="true">
    <path d="M10 38V10H38V38H26M10 38H38M10 22H24M24 10V28M24 28H38" stroke=${dark ? '#0f0f0f' : 'currentColor'} stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/>
    <circle cx="26" cy="38" r="3" fill="#c97d3a"/>
  </svg>`;

// ---------------- store ----------------

export const store = {
  s: { connected: false, snapshot: false, entities: {}, rooms: [], scenes: [], media: {}, volume: {}, stats: {}, player: { status: 'stopped' } },
  subs: new Set(),
  set(patch) { Object.assign(this.s, patch); this.emit(); },
  emit() {
    if (this._raf) return;
    const run = () => { this._raf = 0; this.subs.forEach(f => f()); };
    // rAF is paused in hidden tabs; a timer keeps background PWAs current.
    this._raf = document.hidden ? setTimeout(run, 50) : requestAnimationFrame(run);
  },
};

export function useStore() {
  const [, force] = useState(0);
  useEffect(() => { const f = () => force(x => x + 1); store.subs.add(f); return () => store.subs.delete(f); }, []);
  return store.s;
}

// ---------------- live socket ----------------

let ws, retry = 0, wantOpen = false;
export function connect() {
  wantOpen = true;
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${proto}://${location.host}/api/ws`);
  ws.onopen = () => { retry = 0; store.set({ connected: true }); };
  ws.onclose = () => {
    store.set({ connected: false });
    if (!wantOpen) return;
    setTimeout(connect, Math.min(1000 * 2 ** retry++, 10000));
  };
  ws.onmessage = ev => {
    const { t, d } = JSON.parse(ev.data);
    const s = store.s;
    switch (t) {
      case 'snapshot': store.set({ ...d, snapshot: true }); break;
      case 'entity': {
        const entities = { ...s.entities };
        if (d.state) entities[d.id] = d.state; else delete entities[d.id];
        store.set({ entities });
        break;
      }
      case 'entities': store.set({ entities: d }); break;
      case 'feeds': store.set({ weather: d.weather, prayer: d.prayer }); break;
      case 'config': store.set({ home: d.home, shortcuts: d.shortcuts, rooms: d.rooms }); break;
      case 'tvEntity': store.set({ tvEntity: d || null }); break;
      case 'kiosk': store.set({ kiosk: d || null }); break;
      default: store.set({ [t]: d });
    }
  };
}
export function disconnect() { wantOpen = false; ws?.close(); }
export function send(msg) { if (ws?.readyState === 1) ws.send(JSON.stringify(msg)); }

// ---------------- api ----------------

export async function api(path, body, method) {
  const res = await fetch(path, {
    method: method || (body === undefined ? 'GET' : 'POST'),
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
  });
  if (res.status === 401 && !path.startsWith('/api/login')) { location.reload(); throw new Error('Signed out'); }
  const text = await res.text();
  const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
  if (!res.ok) throw new Error(data?.error || `Request failed (${res.status})`);
  return data;
}

/** Fire an action; show a toast if it fails. Returns a promise for chaining. */
export function act(path, body, okMsg) {
  return api(path, body ?? {}).then(r => { if (okMsg) toast(okMsg); return r; }).catch(e => { toast(e.message, true); throw e; });
}

export const haCall = (domain, service, data, target) => act('/api/ha/call', { domain, service, data, target });

// ---------------- toasts ----------------

export const toasts = { list: [], subs: new Set() };
export function toast(text, error = false) {
  const t = { id: Math.random(), text, error };
  toasts.list = [...toasts.list, t];
  toasts.subs.forEach(f => f());
  setTimeout(() => { toasts.list = toasts.list.filter(x => x !== t); toasts.subs.forEach(f => f()); }, error ? 5000 : 2600);
}
export function Toasts() {
  const [, force] = useState(0);
  useEffect(() => { const f = () => force(x => x + 1); toasts.subs.add(f); return () => toasts.subs.delete(f); }, []);
  return html`<div class="toasts" role="status" aria-live="polite">${toasts.list.map(t => html`<div key=${t.id} class=${'toast' + (t.error ? ' error' : '')}>${t.text}</div>`)}</div>`;
}

// ---------------- router ----------------

export const router = { path: location.pathname, subs: new Set() };
export function navigate(to, replace = false) {
  if (to === router.path) return;
  history[replace ? 'replaceState' : 'pushState']({}, '', to);
  router.path = location.pathname;
  router.subs.forEach(f => f());
  window.scrollTo({ top: 0 });
}
addEventListener('popstate', () => { router.path = location.pathname; router.subs.forEach(f => f()); });
export function useRoute() {
  const [, force] = useState(0);
  useEffect(() => { const f = () => force(x => x + 1); router.subs.add(f); return () => router.subs.delete(f); }, []);
  return router.path;
}
export const Link = ({ to, children, ...rest }) => html`
  <a href=${to} aria-current=${router.path === to ? 'page' : undefined} onClick=${e => { if (e.metaKey || e.ctrlKey) return; e.preventDefault(); navigate(to); }} ...${rest}>${children}</a>`;

// ---------------- hooks ----------------

export function useNow(ms = 1000) {
  const [now, set] = useState(() => new Date());
  useEffect(() => { const t = setInterval(() => set(new Date()), ms); return () => clearInterval(t); }, [ms]);
  return now;
}

export function useVisible(ref) {
  const [v, set] = useState(false);
  useEffect(() => {
    if (!ref.current) return;
    const io = new IntersectionObserver(([e]) => set(e.isIntersecting && document.visibilityState === 'visible'));
    io.observe(ref.current);
    const onVis = () => set(document.visibilityState === 'visible' && ref.current?.getBoundingClientRect().bottom > 0);
    document.addEventListener('visibilitychange', onVis);
    return () => { io.disconnect(); document.removeEventListener('visibilitychange', onVis); };
  }, [ref.current]);
  return v;
}

/** Live JPEG of the HTPC screen while visible. */
export function useScreen(ref, width, interval) {
  const visible = useVisible(ref);
  const [src, setSrc] = useState(null);
  useEffect(() => {
    if (!visible) return;
    let alive = true, timer;
    const tick = () => {
      const img = new Image();
      const url = `/api/pc/screen.jpg?w=${width}&t=${Date.now()}`;
      img.onload = () => { if (!alive) return; setSrc(url); timer = setTimeout(tick, interval); };
      img.onerror = () => { if (alive) timer = setTimeout(tick, interval * 3); };
      img.src = url;
    };
    tick();
    return () => { alive = false; clearTimeout(timer); };
  }, [visible, width, interval]);
  return src;
}

/** Throttled sender for sliders: sends while dragging, always sends the final value. */
export function useThrottled(fn, ms = 180) {
  const last = useRef(0), timer = useRef(0), pending = useRef(null);
  return useCallback(v => {
    pending.current = v;
    const now = Date.now();
    const fire = () => { last.current = Date.now(); timer.current = 0; fn(pending.current); };
    if (now - last.current >= ms) fire();
    else if (!timer.current) timer.current = setTimeout(fire, ms - (now - last.current));
  }, [fn, ms]);
}

// ---------------- formatting ----------------

export const pad = n => String(n).padStart(2, '0');
export const hhmm = d => `${pad(d.getHours())}:${pad(d.getMinutes())}`;
export function dur(sec) {
  if (!isFinite(sec) || sec < 0) sec = 0;
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = Math.floor(sec % 60);
  return h ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}
export function runtime(sec) {
  if (!sec) return '';
  const h = Math.floor(sec / 3600), m = Math.round((sec % 3600) / 60);
  return h ? `${h}h ${m}m` : `${m}m`;
}
export function until(mins) {
  if (mins < 1) return 'now';
  const h = Math.floor(mins / 60), m = Math.round(mins % 60);
  return h ? `in ${h}h ${m}m` : `in ${m} min`;
}
export const greeting = d => { const h = d.getHours(); return h < 5 ? 'Good night' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'; };
export const dateLong = d => d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' });

const WMO = {
  0: ['Clear', 'sun', 'moon'], 1: ['Mostly clear', 'sun', 'moon'], 2: ['Partly cloudy', 'cloud-sun', 'cloud-moon'], 3: ['Overcast', 'cloud', 'cloud'],
  45: ['Fog', 'cloud-fog', 'cloud-fog'], 48: ['Fog', 'cloud-fog', 'cloud-fog'],
  51: ['Drizzle', 'cloud-drizzle', 'cloud-drizzle'], 53: ['Drizzle', 'cloud-drizzle', 'cloud-drizzle'], 55: ['Drizzle', 'cloud-drizzle', 'cloud-drizzle'],
  61: ['Light rain', 'cloud-rain', 'cloud-rain'], 63: ['Rain', 'cloud-rain', 'cloud-rain'], 65: ['Heavy rain', 'cloud-rain', 'cloud-rain'],
  71: ['Snow', 'snowflake', 'snowflake'], 73: ['Snow', 'snowflake', 'snowflake'], 75: ['Snow', 'snowflake', 'snowflake'],
  80: ['Showers', 'cloud-rain', 'cloud-rain'], 81: ['Showers', 'cloud-rain', 'cloud-rain'], 82: ['Heavy showers', 'cloud-rain', 'cloud-rain'],
  95: ['Thunderstorm', 'cloud-lightning', 'cloud-lightning'], 96: ['Thunderstorm', 'cloud-lightning', 'cloud-lightning'], 99: ['Thunderstorm', 'cloud-lightning', 'cloud-lightning'],
};
export const wx = (code, day = true) => { const w = WMO[code] || ['—', 'cloud', 'cloud']; return { text: w[0], icon: day ? w[1] : w[2] }; };

// ---------------- prayer ----------------

const PRAYERS = [['fajr', 'Fajr'], ['dhuhr', 'Dhuhr'], ['asr', 'Asr'], ['maghrib', 'Maghrib'], ['isha', 'Isha']];
const toDate = (day, t) => { const [h, m] = t.split(':').map(Number); const d = new Date(day + 'T00:00:00'); d.setHours(h, m, 0, 0); return d; };
export function prayerInfo(p, now) {
  if (!p?.today) return null;
  const list = PRAYERS.map(([k, n]) => ({ key: k, name: n, time: p.today[k], at: toDate(p.today.date, p.today[k]) }));
  let next = list.find(x => x.at > now);
  if (!next && p.tomorrow) next = { key: 'fajr', name: 'Fajr', time: p.tomorrow.fajr, at: toDate(p.tomorrow.date, p.tomorrow.fajr), tomorrow: true };
  const ramadan = p.today.hijri?.month === 9;
  const sehri = (() => {
    const src = now > toDate(p.today.date, p.today.fajr) && p.tomorrow ? p.tomorrow : p.today;
    const d = toDate(src.date, src.fajr); d.setMinutes(d.getMinutes() - (p.sehriOffset || 0)); return d;
  })();
  return { list, next, mins: next ? (next.at - now) / 60000 : 0, ramadan, sehri, iftar: toDate(p.today.date, p.today.maghrib), hijri: p.today.hijri, sunrise: p.today.sunrise };
}

// ---------------- lights ----------------

export const lightName = e => e?.attributes?.friendly_name || e?.entity_id;
export const isOn = e => e?.state === 'on';
export const pct = e => Math.round(((e?.attributes?.brightness ?? 255) / 255) * 100);
export const supportsCt = e => (e?.attributes?.supported_color_modes || []).some(m => ['color_temp', 'rgbww', 'rgbw'].includes(m));
export const supportsColor = e => (e?.attributes?.supported_color_modes || []).some(m => ['hs', 'xy', 'rgb', 'rgbw', 'rgbww'].includes(m));
export const supportsDim = e => (e?.attributes?.supported_color_modes || []).some(m => m !== 'onoff');

/** Colour a lit room glows: colour temperature or hue, at an alpha tracking brightness. */
export function glow(lights) {
  const on = lights.filter(isOn);
  if (!on.length) return null;
  let r = 0, g = 0, b = 0, bri = 0;
  for (const e of on) {
    const a = e.attributes || {};
    let c;
    if (a.color_mode && !['color_temp', 'brightness', 'onoff', 'white'].includes(a.color_mode) && a.rgb_color) c = a.rgb_color;
    else c = kelvinRgb(a.color_temp_kelvin || 2700);
    r += c[0]; g += c[1]; b += c[2]; bri += (a.brightness ?? 255) / 255;
  }
  const n = on.length, level = (bri / n) * (on.length / lights.length);
  return { color: `rgba(${Math.round(r / n)}, ${Math.round(g / n)}, ${Math.round(b / n)}, ${(0.07 + level * 0.3).toFixed(3)})`, level };
}

export function kelvinRgb(k) {
  const t = k / 100;
  const r = t <= 66 ? 255 : 329.7 * Math.pow(t - 60, -0.1332);
  const g = t <= 66 ? 99.47 * Math.log(t) - 161.12 : 288.12 * Math.pow(t - 60, -0.0755);
  const b = t >= 66 ? 255 : t <= 19 ? 0 : 138.52 * Math.log(t - 10) - 305.04;
  return [r, g, b].map(v => Math.max(0, Math.min(255, v)));
}

export const jfImg = (id, type = 'Primary', w = 480) => `/api/jf/img/${id}/${type}?w=${w}`;
export function thumbFor(item, w = 640) {
  if (item.type === 'Episode') return item.parentThumbId ? jfImg(item.parentThumbId, 'Thumb', w) : item.hasPrimary ? jfImg(item.id, 'Primary', w) : item.parentBackdropId ? jfImg(item.parentBackdropId, 'Backdrop', w) : null;
  if (item.hasThumb) return jfImg(item.id, 'Thumb', w);
  if (item.hasBackdrop) return jfImg(item.id, 'Backdrop', w);
  return item.hasPrimary ? jfImg(item.id, 'Primary', w) : null;
}
export const epLabel = i => i.type === 'Episode' ? `S${i.season ?? '?'} E${i.episode ?? '?'}` : '';
