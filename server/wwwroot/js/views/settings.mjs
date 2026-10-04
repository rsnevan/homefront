import { useState, useEffect } from 'preact/hooks';
import { html, Icon, useStore, api, act, toast, disconnect } from '../lib.mjs';
import { Toggle, Confirm } from '../components.mjs';

const METHODS = [[1, 'University of Islamic Sciences, Karachi'], [2, 'ISNA (North America)'], [3, 'Muslim World League'], [4, 'Umm al-Qura, Makkah'], [5, 'Egyptian General Authority'], [15, 'Moonsighting Committee']];
const ROOM_ICONS = ['sofa', 'utensils', 'bed-double', 'book-open', 'lamp', 'tv', 'bed', 'house'];

export function Settings() {
  const s = useStore();
  const [cfg, setCfg] = useState(null);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const owner = s.me?.role === 'owner';

  const load = () => api('/api/settings').then(c => { setCfg(c); setDirty(false); }).catch(e => toast(e.message, true));
  useEffect(() => { if (owner) load(); }, [owner]);
  useEffect(() => { if (cfg && location.hash) document.getElementById(location.hash.slice(1))?.scrollIntoView({ block: 'start' }); }, [!!cfg]);

  if (!owner) return html`
    <h1 class="h-page" style="margin-bottom:18px">Settings</h1>
    <div class="settings"><section class="panel set-sec">
      <div class="set-row"><div class="txt"><b>You're a guest</b><span>Signed in as ${s.me?.name}. The owner manages everything else.</span></div></div>
      <${Appearance} />
      <div class="set-row"><div class="txt"><b>Leave</b><span>Sign out of this device.</span></div><button class="btn sm" onClick=${signOut}>Sign out</button></div>
    </section></div>`;
  if (!cfg) return html`<h1 class="h-page" style="margin-bottom:18px">Settings</h1><div class="skeleton" style="height:400px;max-width:880px"></div>`;

  const upd = patch => { setCfg({ ...cfg, ...patch }); setDirty(true); };
  const save = async () => {
    setSaving(true);
    try {
      await api('/api/settings', { homeName: cfg.homeName, ownerName: cfg.ownerName, location: cfg.location, prayer: cfg.prayer, cinema: cfg.cinema, rooms: cfg.rooms, shortcuts: cfg.shortcuts, tvPcInput: cfg.tvPcInput });
      toast('Settings saved'); setDirty(false);
    } catch (e) { toast(e.message, true); } finally { setSaving(false); }
  };
  const assigned = id => cfg.rooms.find(r => r.lights.includes(id))?.name || '';
  const assign = (id, roomName) => upd({ rooms: cfg.rooms.map(r => ({ ...r, lights: r.name === roomName ? [...r.lights.filter(x => x !== id), id] : r.lights.filter(x => x !== id) })) });

  return html`
    <div class="sec-head" style="margin-bottom:18px;position:sticky;top:0;z-index:5;background:var(--bg);padding:8px 0">
      <h1 class="h-page">Settings</h1>
      <button class="btn primary" disabled=${!dirty || saving} onClick=${save}>${saving ? 'Saving…' : dirty ? 'Save changes' : 'Saved'}</button>
    </div>
    <div class="settings">
      <section class="panel set-sec">
        <h2 class="h-sec">Home</h2>
        <div class="form-grid">
          <div class="field"><label for="hn">Home name</label><input id="hn" class="input" value=${cfg.homeName} onInput=${e => upd({ homeName: e.target.value })} /></div>
          <div class="field"><label for="on">Your name</label><input id="on" class="input" value=${cfg.ownerName} onInput=${e => upd({ ownerName: e.target.value })} /></div>
          <div class="field"><label for="tvin">HTPC's HDMI input on the TV</label>
            <select id="tvin" class="input" value=${cfg.tvPcInput} onChange=${e => upd({ tvPcInput: +e.target.value })}>${[1, 2, 3, 4].map(n => html`<option value=${n}>HDMI ${n}</option>`)}</select></div>
        </div>
      </section>

      <section class="panel set-sec">
        <h2 class="h-sec">Location</h2>
        <p class="muted small" style="margin:-8px 0 0">Used for weather and prayer times.</p>
        <div class="form-grid">
          <div class="field"><label for="ln">Place name</label><input id="ln" class="input" value=${cfg.location.name} onInput=${e => upd({ location: { ...cfg.location, name: e.target.value } })} /></div>
          <div class="field"><label for="la">Latitude</label><input id="la" class="input num" inputmode="decimal" value=${cfg.location.lat} onInput=${e => upd({ location: { ...cfg.location, lat: parseFloat(e.target.value) || 0 } })} /></div>
          <div class="field"><label for="lo">Longitude</label><input id="lo" class="input num" inputmode="decimal" value=${cfg.location.lon} onInput=${e => upd({ location: { ...cfg.location, lon: parseFloat(e.target.value) || 0 } })} /></div>
        </div>
        ${'geolocation' in navigator && window.isSecureContext && html`<div><button class="btn sm" onClick=${() => navigator.geolocation.getCurrentPosition(p => upd({ location: { ...cfg.location, lat: +p.coords.latitude.toFixed(4), lon: +p.coords.longitude.toFixed(4) } }), () => toast('Location permission was declined', true))}><${Icon} name="scan" size=${16} />Use this device's location</button></div>`}
      </section>

      <section class="panel set-sec">
        <div class="set-row"><div class="txt"><b>Prayer times</b><span>Next prayer on the home screen. Sehri and Iftar appear automatically during Ramadan.</span></div>
          <${Toggle} label="Prayer times" checked=${cfg.prayer.enabled} onChange=${v => upd({ prayer: { ...cfg.prayer, enabled: v } })} /></div>
        ${cfg.prayer.enabled && html`<div class="form-grid">
          <div class="field wide"><label for="pm">Calculation method</label>
            <select id="pm" class="input" value=${cfg.prayer.method} onChange=${e => upd({ prayer: { ...cfg.prayer, method: +e.target.value } })}>${METHODS.map(([v, n]) => html`<option value=${v}>${n}</option>`)}</select></div>
          <div class="field"><label for="so">Sehri ends before Fajr (minutes)</label><input id="so" class="input num" type="number" min="0" max="30" value=${cfg.prayer.sehriOffsetMinutes} onInput=${e => upd({ prayer: { ...cfg.prayer, sehriOffsetMinutes: +e.target.value } })} /></div>
        </div>`}
      </section>

      <section class="panel set-sec" id="rooms">
        <div class="row between"><h2 class="h-sec">Rooms</h2>
          <button class="btn sm" onClick=${() => upd({ rooms: [...cfg.rooms, { name: 'New room', icon: 'lamp', lights: [] }] })}><${Icon} name="plus" size=${16} />Add room</button></div>
        <div class="list-edit">
          ${cfg.rooms.map((r, i) => html`<div class="row">
            <select class="input" style="width:64px;flex:none;padding:0 8px" aria-label="Icon" value=${r.icon} onChange=${e => upd({ rooms: cfg.rooms.map((x, j) => j === i ? { ...x, icon: e.target.value } : x) })}>
              ${ROOM_ICONS.map(ic => html`<option value=${ic}>${ic.replace('-', ' ')}</option>`)}</select>
            <input class="input" aria-label="Room name" value=${r.name} onInput=${e => upd({ rooms: cfg.rooms.map((x, j) => j === i ? { ...x, name: e.target.value } : x) })} />
            <button class="icon-btn plain" aria-label="Move up" disabled=${i === 0} onClick=${() => { const a = [...cfg.rooms]; [a[i - 1], a[i]] = [a[i], a[i - 1]]; upd({ rooms: a }); }}><${Icon} name="chevron-up" /></button>
            <button class="icon-btn plain" aria-label=${`Remove ${r.name}`} onClick=${() => upd({ rooms: cfg.rooms.filter((_, j) => j !== i) })}><${Icon} name="trash-2" /></button>
          </div>`)}
        </div>
        <p class="muted small" style="margin:0">The first room is the big one on the floor plan and the one that follows what's playing.</p>
        ${cfg.lights.length > 0 && html`
          <h3 class="h-sec" style="font-size:15px;margin-top:8px">Which room is each light in?</h3>
          <div class="list-edit">
            ${cfg.lights.map(l => html`<div class="row between">
              <span class="grow ellipsis">${l.name || l.id}</span>
              <select class="input" style="width:min(200px, 55%)" value=${assigned(l.id)} onChange=${e => assign(l.id, e.target.value)}>
                <option value="">By name (automatic)</option>${cfg.rooms.map(r => html`<option value=${r.name}>${r.name}</option>`)}</select>
            </div>`)}
          </div>`}
        <div class="set-row"><div class="txt"><b>Follow-the-movie rooms</b><span>Which rooms dim when a video plays.</span></div></div>
        <div class="row wrap" style="gap:8px">${cfg.rooms.map(r => html`<button class="chip" aria-pressed=${cfg.cinema.rooms.includes(r.name)}
          onClick=${() => upd({ cinema: { ...cfg.cinema, rooms: cfg.cinema.rooms.includes(r.name) ? cfg.cinema.rooms.filter(x => x !== r.name) : [...cfg.cinema.rooms, r.name] } })}>${r.name}</button>`)}</div>
        <div class="form-grid">
          <div class="field"><label for="cb1">Brightness while playing (%)</label><input id="cb1" class="input num" type="number" min="1" max="100" value=${cfg.cinema.playingBrightness} onInput=${e => upd({ cinema: { ...cfg.cinema, playingBrightness: +e.target.value } })} /></div>
          <div class="field"><label for="cb2">Brightness when paused (%)</label><input id="cb2" class="input num" type="number" min="1" max="100" value=${cfg.cinema.pausedBrightness} onInput=${e => upd({ cinema: { ...cfg.cinema, pausedBrightness: +e.target.value } })} /></div>
        </div>
      </section>

      <section class="panel set-sec">
        <div class="row between"><h2 class="h-sec">Open on the TV</h2>
          <button class="btn sm" onClick=${() => upd({ shortcuts: [...cfg.shortcuts, { name: '', url: 'https://', color: '#ffc94d' }] })}><${Icon} name="plus" size=${16} />Add</button></div>
        <div class="list-edit">
          ${cfg.shortcuts.map((sc, i) => html`<div class="row">
            <input type="color" aria-label="Colour" value=${sc.color} onInput=${e => upd({ shortcuts: cfg.shortcuts.map((x, j) => j === i ? { ...x, color: e.target.value } : x) })} style="width:42px;height:42px;border:0;background:none;padding:0;flex:none" />
            <input class="input" style="max-width:160px" aria-label="Name" placeholder="Name" value=${sc.name} onInput=${e => upd({ shortcuts: cfg.shortcuts.map((x, j) => j === i ? { ...x, name: e.target.value } : x) })} />
            <input class="input" aria-label="Link" placeholder="https://" value=${sc.url} onInput=${e => upd({ shortcuts: cfg.shortcuts.map((x, j) => j === i ? { ...x, url: e.target.value } : x) })} />
            <button class="icon-btn plain" aria-label=${`Remove ${sc.name}`} onClick=${() => upd({ shortcuts: cfg.shortcuts.filter((_, j) => j !== i) })}><${Icon} name="trash-2" /></button>
          </div>`)}
        </div>
        <p class="muted small" style="margin:0">Links open in Brave on the HTPC with your existing logins. Desktop apps work too: <span class="num">app:spotify</span>, <span class="num">app:iptvnator</span>, <span class="num">app:vlc</span>.</p>
      </section>

      <${Guests} />
      <${Account} username=${cfg.username} />

      <section class="panel set-sec">
        <h2 class="h-sec">This device</h2>
        <${Appearance} />
        <div class="set-row"><div class="txt"><b>Sign out</b><span>You'll need the password to get back in on this device.</span></div><button class="btn sm" onClick=${signOut}>Sign out</button></div>
      </section>

      <section class="panel set-sec">
        <h2 class="h-sec">Connections</h2>
        <div class="set-row"><div class="txt"><b>Home Assistant</b><span>${cfg.status.ha ? `Connected at ${cfg.status.haUrl}, version ${cfg.status.haVersion}` : 'Not connected'}</span></div><span class=${'dot' + (cfg.status.ha ? ' live' : ' bad')}></span></div>
        <div class="set-row"><div class="txt"><b>Jellyfin</b><span>${cfg.status.jellyfin ? 'Running on the HTPC' : 'Not reachable'}</span></div><span class=${'dot' + (cfg.status.jellyfin ? ' live' : ' bad')}></span></div>
        <div class="set-row"><div class="txt"><b>Open homefront anywhere</b><span>This page is at <span class="num">${location.origin}</span>. Over Tailscale it works away from home too.</span></div></div>
      </section>
    </div>`;
}

async function signOut() { await api('/api/logout', {}); disconnect(); location.href = '/'; }

function Appearance() {
  const [t, setT] = useState(() => document.documentElement.dataset.theme || 'auto');
  const set = v => { setT(v); if (v === 'auto') { delete document.documentElement.dataset.theme; try { localStorage.removeItem('hf-theme'); } catch {} } else { document.documentElement.dataset.theme = v; try { localStorage.setItem('hf-theme', v); } catch {} } };
  return html`<div class="set-row"><div class="txt"><b>Theme</b><span>Follows the device unless you pick one.</span></div>
    <div class="row" style="gap:6px">${[['auto', 'Auto'], ['dark', 'Dark'], ['light', 'Light']].map(([v, n]) => html`<button class="chip" aria-pressed=${t === v} onClick=${() => set(v)}>${n}</button>`)}</div></div>`;
}

function Guests() {
  const [list, setList] = useState([]);
  const [name, setName] = useState('');
  const [hours, setHours] = useState(48);
  const [made, setMade] = useState(null);
  const [del, setDel] = useState(null);
  useEffect(() => { api('/api/guests').then(setList); }, []);
  const create = async e => {
    e.preventDefault();
    const r = await act('/api/guests', { name, hours: hours || null });
    setMade({ ...r, url: location.origin + r.path });
    setName('');
    api('/api/guests').then(setList);
  };
  const copy = async u => { try { await navigator.clipboard.writeText(u); toast('Link copied'); } catch { toast('Copy failed. Long-press the link instead.', true); } };
  const expiry = g => !g.expires ? 'Never expires' : new Date(g.expires) < new Date() ? 'Expired' : `Until ${new Date(g.expires).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}`;
  return html`<section class="panel set-sec" id="guests">
    <h2 class="h-sec">Guest passes</h2>
    <p class="muted small" style="margin:-8px 0 0">Guests get lights, the TV, media and the remote. Settings, power and light setup stay yours.</p>
    <form class="row wrap" onSubmit=${create}>
      <input class="input grow" style="min-width:180px" placeholder="Guest's name" value=${name} onInput=${e => setName(e.target.value)} aria-label="Guest's name" />
      <select class="input" style="width:150px" value=${hours} onChange=${e => setHours(+e.target.value)} aria-label="How long">
        <option value="4">4 hours</option><option value="24">1 day</option><option value="48">2 days</option><option value="168">1 week</option><option value="0">No expiry</option></select>
      <button class="btn primary" type="submit"><${Icon} name="ticket" size=${18} />Create pass</button>
    </form>
    ${made && html`<div class="row wrap" style="gap:20px;align-items:center;border:1px solid var(--accent);border-radius:var(--r-md);padding:16px;background:var(--accent-soft)">
      <img class="qr" style="width:160px;height:160px" src=${'/api/qr.svg?data=' + encodeURIComponent(made.url)} alt="Guest pass QR code" />
      <div class="grow stack" style="min-width:220px">
        <b style="font-weight:500">${made.pass.name}'s pass is ready</b>
        <span class="muted small">Scan the code on their phone, or send them the link. It signs them in straight away.</span>
        <div class="row"><input class="input" readonly value=${made.url} onFocus=${e => e.target.select()} aria-label="Guest link" /><button class="btn" onClick=${() => copy(made.url)}><${Icon} name="copy" size=${16} />Copy</button></div>
      </div>
    </div>`}
    ${list.length > 0 && html`<div>${list.map(g => html`<div class="set-row">
      <div class="txt"><b>${g.name}</b><span>${g.enabled ? expiry(g) : 'Turned off'}</span></div>
      <div class="row" style="gap:8px"><${Toggle} label=${`${g.name}'s pass`} checked=${g.enabled} onChange=${() => api(`/api/guests/${g.id}/toggle`, {}).then(setList)} />
        <button class="icon-btn plain" aria-label=${`Delete ${g.name}'s pass`} onClick=${() => setDel(g)}><${Icon} name="trash-2" /></button></div>
    </div>`)}</div>`}
    ${del && html`<${Confirm} title=${`Delete ${del.name}'s pass?`} body="Their link stops working immediately." action="Delete pass" danger onClose=${() => setDel(null)}
      onConfirm=${() => api(`/api/guests/${del.id}`, undefined, 'DELETE').then(setList)} />`}
  </section>`;
}

function Account({ username }) {
  const [f, setF] = useState({ username, current: '', next: '' });
  const [err, setErr] = useState(null);
  const submit = async e => {
    e.preventDefault(); setErr(null);
    try { await api('/api/settings/password', f); toast('Sign-in details updated'); setF({ ...f, current: '', next: '' }); } catch (e2) { setErr(e2.message); }
  };
  return html`<section class="panel set-sec">
    <h2 class="h-sec">Your sign-in</h2>
    <form class="form-grid" onSubmit=${submit}>
      <div class="field"><label for="u">Username</label><input id="u" class="input" autocomplete="username" value=${f.username} onInput=${e => setF({ ...f, username: e.target.value })} /></div>
      <div class="field"><label for="cp">Current password</label><input id="cp" class="input" type="password" autocomplete="current-password" value=${f.current} onInput=${e => setF({ ...f, current: e.target.value })} /></div>
      <div class="field"><label for="np">New password</label><input id="np" class="input" type="password" autocomplete="new-password" value=${f.next} onInput=${e => setF({ ...f, next: e.target.value })} /></div>
      <div class="field" style="align-content:end"><button class="btn" type="submit" disabled=${!f.current || !f.next}>Update sign-in</button></div>
    </form>
    ${err && html`<div class="err">${err}</div>`}
  </section>`;
}
