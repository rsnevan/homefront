import { useState, useEffect, useRef, useMemo } from 'preact/hooks';
import {
  html, Icon, store, useStore, api, act, haCall, toast, navigate, useNow, useScreen, useThrottled, useVisible,
  dur, runtime, hhmm, until, wx, prayerInfo, lightName, isOn, pct, supportsCt, supportsColor, supportsDim, glow,
  jfImg, thumbFor, epLabel, send,
} from './lib.mjs';

// ================= primitives =================

export function Range({ value, min = 0, max = 100, step = 1, onInput, onChange, cls = '', label, showValue, fmt = v => v, disabled }) {
  const [local, setLocal] = useState(null);
  const v = local ?? value ?? min;
  const p = ((v - min) / (max - min)) * 100;
  return html`
    <div class="range">
      <input type="range" class=${cls} min=${min} max=${max} step=${step} value=${v} aria-label=${label} disabled=${disabled}
        style=${`--p:${p}%`}
        onInput=${e => { const n = +e.target.value; setLocal(n); onInput?.(n); }}
        onChange=${e => { const n = +e.target.value; onChange?.(n); setTimeout(() => setLocal(null), 1200); }} />
      ${showValue && html`<span class="val num">${fmt(v)}</span>`}
    </div>`;
}

export const Toggle = ({ checked, onChange, label }) => html`
  <label class="toggle"><input type="checkbox" role="switch" checked=${!!checked} aria-label=${label} onChange=${e => onChange(e.target.checked)} /><span></span></label>`;

export function Sheet({ onClose, children, wide, label, noClose }) {
  const ref = useRef();
  useEffect(() => {
    const k = e => e.key === 'Escape' && onClose();
    addEventListener('keydown', k);
    const prev = document.activeElement;
    ref.current?.focus();
    document.body.style.overflow = 'hidden';
    return () => { removeEventListener('keydown', k); document.body.style.overflow = ''; prev?.focus?.(); };
  }, []);
  return html`
    <div class="sheet-scrim" onClick=${e => e.target === e.currentTarget && onClose()}>
      <div class=${'sheet' + (wide ? ' wide' : '')} role="dialog" aria-modal="true" aria-label=${label} tabindex="-1" ref=${ref}>
        ${!noClose && html`<button class="icon-btn sheet-close plain" onClick=${onClose} aria-label="Close"><${Icon} name="x" /></button>`}
        ${children}
      </div>
    </div>`;
}

export function Confirm({ title, body, action, danger, onConfirm, onClose }) {
  return html`<${Sheet} onClose=${onClose} label=${title} noClose>
    <h2 class="h-sec" style="font-size:20px;margin-bottom:8px">${title}</h2>
    <p class="muted" style="margin:0 0 22px">${body}</p>
    <div class="row" style="justify-content:flex-end">
      <button class="btn ghost" onClick=${onClose}>Cancel</button>
      <button class=${'btn ' + (danger ? 'danger' : 'primary')} onClick=${() => { onConfirm(); onClose(); }}>${action}</button>
    </div>
  </${Sheet}>`;
}

// ================= floor plan =================

// [colStart, colEnd, rowStart, rowEnd] per room, in room order. Proportions follow the brand mark.
const PLAN_WIDE = {
  1: [[1, 13, 1, 9]],
  2: [[1, 8, 1, 9], [8, 13, 1, 9]],
  3: [[1, 8, 1, 9], [8, 13, 1, 5], [8, 13, 5, 9]],
  4: [[1, 8, 4, 9], [1, 8, 1, 4], [8, 13, 1, 6], [8, 13, 6, 9]],
  5: [[1, 8, 4, 9], [1, 5, 1, 4], [8, 13, 1, 6], [8, 13, 6, 9], [5, 8, 1, 4]],
  6: [[1, 7, 4, 9], [1, 4, 1, 4], [7, 13, 1, 5], [7, 10, 5, 9], [4, 7, 1, 4], [10, 13, 5, 9]],
};
const PLAN_TALL = {
  1: [[1, 7, 1, 13]],
  2: [[1, 7, 5, 13], [1, 7, 1, 5]],
  3: [[1, 7, 6, 13], [1, 4, 1, 6], [4, 7, 1, 6]],
  4: [[1, 7, 7, 13], [1, 4, 1, 7], [4, 7, 1, 4], [4, 7, 4, 7]],
  5: [[1, 7, 7, 13], [1, 4, 1, 4], [4, 7, 1, 4], [4, 7, 4, 7], [1, 4, 4, 7]],
  6: [[1, 7, 8, 13], [1, 4, 1, 4], [4, 7, 1, 4], [4, 7, 4, 8], [1, 4, 4, 6], [1, 4, 6, 8]],
};

function useNarrow() {
  const q = '(max-width: 720px)';
  const [n, set] = useState(() => matchMedia(q).matches);
  useEffect(() => { const m = matchMedia(q); const f = () => set(m.matches); m.addEventListener('change', f); return () => m.removeEventListener('change', f); }, []);
  return n;
}

let introPlayed = false;

export function Plan({ compact, mini, onRoom }) {
  const s = useStore();
  const narrow = useNarrow() && !mini;
  const [busy, setBusy] = useState(null);
  const rooms = (s.rooms || []).slice(0, 6);
  const tpl = (narrow ? PLAN_TALL : PLAN_WIDE)[rooms.length] || null;
  const [cols, rowsN] = narrow ? [7, 13] : [13, 9];
  const anyLights = Object.keys(s.entities).some(k => k.startsWith('light.'));
  const liveRoom = s.cinema?.rooms?.[0] || rooms[0]?.name;
  const playing = s.player?.status === 'playing' || (s.media?.active && s.media?.status === 'playing');
  const intro = useMemo(() => { const v = !introPlayed && !mini; introPlayed = true; return v; }, []);

  async function toggleRoom(e, room, on) {
    e.stopPropagation();
    setBusy(room.name);
    try { await act('/api/lights/room', { room: room.name, on: !on }); } finally { setBusy(null); }
  }

  return html`
    <div class="plan-wrap">
      <div class=${'plan' + (compact ? ' compact' : '') + (intro ? ' intro' : '')} role="group" aria-label="Rooms">
        ${rooms.map((room, i) => {
          const ents = room.lights.map(id => s.entities[id]).filter(Boolean);
          const g = glow(ents);
          const onCount = ents.filter(isOn).length;
          const avg = onCount ? Math.round(ents.filter(isOn).reduce((a, e) => a + pct(e), 0) / onCount) : 0;
          const area = tpl?.[i];
          const style = (area ? `grid-column:${area[0]}/${area[1]};grid-row:${area[2]}/${area[3]};` : 'grid-column:span 12;grid-row:span 2;') +
            (g ? `background-color:${g.color};background-image:${g.image};` : '') + `--i:${i}`;
          const edge = (area && area[1] === cols ? ' edge-r' : '') + (area && area[3] === rowsN ? ' edge-b' : '');
          const meta = !ents.length ? (anyLights ? 'No lights here yet' : 'Not connected') : onCount ? `${onCount} of ${ents.length} on` : `${ents.length} ${ents.length === 1 ? 'light' : 'lights'} off`;
          return html`
            <div key=${room.name} class=${'room' + edge} style=${style} role="button" tabindex=${mini ? -1 : 0}
              aria-label=${`${room.name}, ${meta}`}
              onClick=${() => !mini && onRoom?.(room)} onKeyDown=${e => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), onRoom?.(room))}>
              <div>
                <div class="room-name">${room.name}</div>
                <div class="room-meta">${meta}</div>
              </div>
              <div class="room-foot">
                ${onCount ? html`<div class="bright">${avg}<small>%</small></div>` : html`<span></span>`}
                ${ents.length > 0 && html`
                  <button class=${'bulb' + (onCount ? ' on' : '')} aria-label=${`Turn ${room.name} ${onCount ? 'off' : 'on'}`} aria-pressed=${!!onCount}
                    disabled=${busy === room.name} onClick=${e => toggleRoom(e, room, !!onCount)}>
                    <${Icon} name=${onCount ? 'lightbulb' : 'lightbulb-off'} size=${18} />
                  </button>`}
              </div>
              ${room.name === liveRoom && (playing || s.kiosk) && html`<span class=${'live-dot' + (playing ? ' pulse' : '')} style="bottom:-7px;left:46%" title="Something's playing"></span>`}
            </div>`;
        })}
      </div>
      ${!anyLights && !mini && s.snapshot && s.me?.role === 'owner' && html`
        <div class="plan-cta"><div>
          <b style="font-weight:500;font-size:17px">Connect your lights</b>
          <p>Link your bulbs once through Home Assistant and each room here shows its lights live.</p>
          <button class="btn primary sm" onClick=${() => navigate('/lights?setup=1')}>Connect lights</button>
        </div></div>`}
    </div>`;
}

// ================= room sheet =================

const PRESETS = [['Candle', 2200], ['Warm', 2700], ['Neutral', 4000], ['Daylight', 6000]];
const HUES = [0, 28, 50, 120, 190, 220, 270, 320];

export function RoomSheet({ room, onClose }) {
  const s = useStore();
  const ents = room.lights.map(id => s.entities[id]).filter(Boolean);
  const anyOn = ents.some(isOn);
  const avg = anyOn ? Math.round(ents.filter(isOn).reduce((a, e) => a + pct(e), 0) / ents.filter(isOn).length) : 0;
  const setRoom = useThrottled(b => api('/api/lights/room', { room: room.name, on: true, brightness: b }).catch(e => toast(e.message, true)));

  return html`<${Sheet} onClose=${onClose} label=${room.name}>
    <div class="sheet-head" style="padding-right:44px">
      <div>
        <h2 style="font:500 26px/1.1 var(--display);letter-spacing:-.04em">${room.name}</h2>
        <div class="muted small" style="margin-top:4px">${ents.length ? `${ents.filter(isOn).length} of ${ents.length} on` : 'No lights assigned'}</div>
      </div>
      ${ents.length > 0 && html`<${Toggle} checked=${anyOn} label=${`${room.name} lights`} onChange=${on => act('/api/lights/room', { room: room.name, on })} />`}
    </div>
    ${ents.length === 0 && html`<div class="empty"><p style="margin:0">Assign lights to this room in Settings, or name a bulb with the room in it (like "${room.name} 1") and it joins automatically.</p>
      <button class="btn sm" onClick=${() => { onClose(); navigate('/settings#rooms'); }}>Open room settings</button></div>`}
    ${ents.length > 0 && html`
      <div class="stack" style="margin-bottom:8px">
        <${Range} label="Room brightness" value=${avg} min=${1} max=${100} showValue fmt=${v => v + '%'} onInput=${setRoom} onChange=${setRoom} />
        <div class="row wrap" style="gap:8px">
          ${PRESETS.map(([n, k]) => html`<button class="chip" onClick=${() => act('/api/lights/room', { room: room.name, on: true, kelvin: k })}>${n}</button>`)}
        </div>
      </div>
      ${ents.map(e => html`<${LightRow} key=${e.entity_id} e=${e} />`)}`}
  </${Sheet}>`;
}

function LightRow({ e }) {
  const id = e.entity_id, a = e.attributes || {};
  const on = isOn(e);
  const bri = useThrottled(v => haCall('light', 'turn_on', { brightness_pct: v }, { entity_id: id }));
  const ct = useThrottled(v => haCall('light', 'turn_on', { color_temp_kelvin: v }, { entity_id: id }));
  const hue = useThrottled(v => haCall('light', 'turn_on', { hs_color: [v, 85] }, { entity_id: id }));
  return html`
    <div class="light-row">
      <div class="row between">
        <div class="grow"><b style="font-weight:500">${lightName(e)}</b>
          <div class="muted small">${e.state === 'unavailable' ? 'Unavailable' : on ? `${pct(e)}%` : 'Off'}</div></div>
        <${Toggle} checked=${on} label=${lightName(e)} onChange=${v => haCall('light', v ? 'turn_on' : 'turn_off', {}, { entity_id: id })} />
      </div>
      ${on && supportsDim(e) && html`<${Range} label="Brightness" value=${pct(e)} min=${1} max=${100} onInput=${bri} onChange=${bri} />`}
      ${on && supportsCt(e) && html`<${Range} cls="ct" label="Colour temperature" value=${a.color_temp_kelvin || 2700} min=${a.min_color_temp_kelvin || 2000} max=${a.max_color_temp_kelvin || 6500} step=${50} onInput=${ct} onChange=${ct} />`}
      ${on && supportsColor(e) && html`<div class="swatches">${HUES.map(hh => html`<button class="swatch" style=${`background:hsl(${hh} 85% 58%)`} aria-label=${`Colour ${hh}`} onClick=${() => hue(hh)}></button>`)}</div>`}
    </div>`;
}

// ================= scenes =================

export function SceneStrip() {
  const s = useStore();
  const [busy, setBusy] = useState(null);
  if (!s.scenes?.length) return null;
  return html`<div class="scenes" role="list">
    ${s.scenes.map(sc => html`
      <button role="listitem" class=${'scene' + (busy === sc.id ? ' busy' : '')} onClick=${async () => {
        setBusy(sc.id);
        try { await act('/api/scene/' + sc.id, {}, `${sc.name} set`); } finally { setTimeout(() => setBusy(null), 600); }
      }}>
        <${Icon} name=${sc.icon} />
        <span><b>${sc.name}</b><small>${sc.description}</small></span>
      </button>`)}
  </div>`;
}

// ================= now playing =================

export function nowPlaying(s) {
  const p = s.player;
  if (p && p.status && p.status !== 'stopped') {
    return { kind: 'player', title: p.title, sub: p.subtitle, status: p.status, position: p.position, duration: p.duration, at: p.at,
      art: p.imageId ? jfImg(p.imageId, 'Backdrop', 800) : null, video: true, app: 'Jellyfin on TV', canSeek: true, tracks: p.tracks };
  }
  const m = s.media;
  if (m?.active && m.title) {
    return { kind: 'media', title: m.title, sub: m.artist || m.album, status: m.status, position: m.position, duration: m.duration, at: m.updatedAt,
      art: m.artVersion ? `/api/pc/art?v=${m.artVersion}` : null, video: m.isVideo && !m.artVersion, app: m.appName, canSeek: m.canSeek, canPrev: m.canPrev, canNext: m.canNext };
  }
  return null;
}

function usePosition(np) {
  const now = useNow(1000);
  if (!np) return 0;
  const pos = np.status === 'playing' ? np.position + (now.getTime() - np.at) / 1000 : np.position;
  return Math.min(pos, np.duration || pos);
}

export function NowPlaying({ horizontal }) {
  const s = useStore();
  const np = nowPlaying(s);
  const pos = usePosition(np);
  const seekRef = useRef();
  const [picker, setPicker] = useState(false);
  if (!np) return html`
    <div class=${'np np-empty' + (horizontal ? ' horizontal' : '')}>
      <div class="np-art placeholder"><${Icon} name="music" /></div>
      <div><div class="np-title">Nothing playing</div><div class="np-sub">Start something from Launch or your library.</div></div>
    </div>`;

  const ctl = a => np.kind === 'player' ? act('/api/player/' + a, {}) : act('/api/pc/media/' + a, {});
  const seekTo = e => {
    if (!np.canSeek || !np.duration) return;
    const r = seekRef.current.getBoundingClientRect();
    const t = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)) * np.duration;
    np.kind === 'player' ? act('/api/player/seek', { position: t }) : act('/api/pc/media/seek', { position: t });
  };
  const playing = np.status === 'playing';
  return html`
    <div class=${'np' + (horizontal ? ' horizontal' : '')}>
      ${np.art ? html`<img class=${'np-art' + (np.video ? ' video' : '')} src=${np.art} alt="" />` : html`<div class=${'np-art placeholder' + (np.video ? ' video' : '')}><${Icon} name=${np.video ? 'film' : 'music'} /></div>`}
      <div style="min-width:0">
        <div class="np-src"><span class=${'dot' + (playing ? ' live' : '')}></span>${np.app}</div>
        <div class="np-title ellipsis" style="margin-top:6px">${np.title}</div>
        ${np.sub && html`<div class="np-sub ellipsis">${np.sub}</div>`}
        ${np.duration > 0 && html`
          <div style="margin-top:14px">
            <div class="seek" ref=${seekRef} onClick=${seekTo} role="slider" aria-label="Position" aria-valuemin="0" aria-valuemax=${Math.round(np.duration)} aria-valuenow=${Math.round(pos)}>
              <div class="progress"><i style=${`width:${(pos / np.duration) * 100}%`}></i></div>
            </div>
            <div class="np-time"><span>${dur(pos)}</span><span>-${dur(np.duration - pos)}</span></div>
          </div>`}
      </div>
      <${Controls} np=${np} ctl=${ctl} playing=${playing} small=${horizontal} onTracks=${() => setPicker(true)} />
      ${picker && html`<${TracksSheet} tracks=${np.tracks} onClose=${() => setPicker(false)} />`}
    </div>`;
}

function Controls({ np, ctl, playing, small, onTracks }) {
  const t = np.tracks, hasTracks = t && (t.subtitles?.length > 0 || t.audio?.length > 1);
  const back = np.kind === 'player' ? () => act('/api/player/seek', { position: Math.max(0, np.position - 10) }) : () => ctl('prev');
  const fwd = np.kind === 'player' ? () => act('/api/player/seek', { position: np.position + 30 }) : () => ctl('next');
  return html`<div class="np-controls">
    <button class="icon-btn plain" onClick=${back} aria-label=${np.kind === 'player' ? 'Back 10 seconds' : 'Previous'} disabled=${np.kind === 'media' && !np.canPrev}><${Icon} name=${np.kind === 'player' ? 'rotate-ccw' : 'skip-back'} /></button>
    <button class=${'icon-btn on' + (small ? '' : ' big')} onClick=${() => ctl(playing ? 'pause' : 'play')} aria-label=${playing ? 'Pause' : 'Play'}><${Icon} name=${playing ? 'pause' : 'play'} /></button>
    <button class="icon-btn plain" onClick=${fwd} aria-label=${np.kind === 'player' ? 'Forward 30 seconds' : 'Next'} disabled=${np.kind === 'media' && !np.canNext}><${Icon} name=${np.kind === 'player' ? 'fast-forward' : 'skip-forward'} /></button>
    ${hasTracks && html`<button class=${'icon-btn plain' + (t.sub != null ? ' cc-on' : '')} onClick=${onTracks} aria-label="Subtitles and audio"><${Icon} name="captions" /></button>`}
    ${np.kind === 'player' && html`<button class="icon-btn plain" onClick=${() => act('/api/player/stop', {})} aria-label="Stop"><${Icon} name="square" /></button>`}
  </div>`;
}

// ================= TV =================

export function TvPanel() {
  const s = useStore();
  const tv = s.entities[s.tvEntity];
  const on = tv && tv.state !== 'off' && tv.state !== 'unavailable';
  const a = tv?.attributes || {};
  const vol = Math.round((a.volume_level ?? 0) * 100);
  const setVol = useThrottled(v => api('/api/tv/volume', { level: v }).catch(e => toast(e.message, true)), 250);
  const [busy, setBusy] = useState(false);
  const power = async () => { setBusy(true); try { await act('/api/tv/' + (on ? 'off' : 'on'), {}); } finally { setTimeout(() => setBusy(false), 1500); } };
  const src = a.source === 'PC' || /hdmi\s*3/i.test(a.source || '') ? 'HTPC' : a.source;
  return html`
    <div class="tv-top">
      <button class=${'icon-btn big' + (on ? ' on' : '')} onClick=${power} disabled=${busy} aria-label=${on ? 'Turn TV off' : 'Turn TV on'}><${Icon} name="power" /></button>
      <div class="tv-state">
        <b>Living room TV</b>
        <span>${!tv ? 'Controls via LGTV Companion' : on ? `On ${src ? 'with ' + src : ''}` : 'Off'}</span>
      </div>
      ${on && html`<button class=${'icon-btn' + (a.is_volume_muted ? ' on' : '')} aria-label=${a.is_volume_muted ? 'Unmute TV' : 'Mute TV'} onClick=${() => act('/api/tv/mute', { muted: !a.is_volume_muted })}><${Icon} name=${a.is_volume_muted ? 'volume-x' : 'volume-2'} /></button>`}
    </div>
    ${on && html`<div style="margin-top:14px"><${Range} label="TV volume" value=${vol} showValue onInput=${setVol} onChange=${setVol} /></div>`}
    <div class="tv-row">
      <button class="chip" onClick=${() => act('/api/tv/pc', {}, 'Switched to the HTPC')}><${Icon} name="monitor" size=${16} />HTPC input</button>
      <button class="chip" onClick=${() => act('/api/tv/screen_off', {}, 'Screen off, sound stays on')}><${Icon} name="monitor-off" size=${16} />Screen off</button>
      <button class="chip" onClick=${() => act('/api/tv/screen_on', {})}><${Icon} name="monitor-play" size=${16} />Screen on</button>
      <${SleepChip} />
    </div>`;
}

// ================= PC =================

export function PcPanel() {
  const s = useStore();
  const ref = useRef();
  const src = useScreen(ref, 640, 2500);
  const st = s.stats || {};
  const [confirm, setConfirm] = useState(null);
  const owner = s.me?.role === 'owner';
  const fg = st.foreground?.title;
  return html`
    <a href="/remote" ref=${ref} class="screen" onClick=${e => { e.preventDefault(); navigate('/remote'); }} aria-label="Open remote">
      ${src ? html`<img src=${src} alt="HTPC screen" />` : html`<div class="skeleton" style="position:absolute;inset:0;border-radius:0"></div>`}
      ${fg && html`<span class="tag"><${Icon} name="app-window" size=${14} /><span class="ellipsis">${fg}</span></span>`}
    </a>
    <div class="bars">
      <${Bar} label="CPU" value=${st.cpu} max=${100} text=${st.cpu != null ? Math.round(st.cpu) + '%' : '—'} />
      <${Bar} label="Memory" value=${st.memUsed} max=${st.memTotal} text=${st.memTotal ? `${st.memUsed} of ${st.memTotal} GB` : '—'} />
      ${(st.disks || []).map(d => html`<${Bar} label=${d.name} value=${d.total - d.free} max=${d.total} warn=${d.free / d.total < 0.05} text=${d.free < 1 ? 'Full' : `${Math.round(d.free)} GB free`} />`)}
    </div>
    <div class="tv-row">
      <button class="chip" onClick=${() => act('/api/kiosk/' + (s.kiosk === 'ambient' ? 'close' : 'ambient'), {})}><${Icon} name="clock" size=${16} />${s.kiosk === 'ambient' ? 'Close ambient' : 'Ambient on TV'}</button>
      ${owner && html`<button class="chip" onClick=${() => act('/api/pc/power/lock', {}, 'Locked')}><${Icon} name="lock" size=${16} />Lock</button>`}
      ${owner && html`<button class="chip" onClick=${() => setConfirm('sleep')}><${Icon} name="moon" size=${16} />Sleep</button>`}
    </div>
    ${confirm === 'sleep' && html`<${Confirm} title="Put the HTPC to sleep?" action="Sleep" onClose=${() => setConfirm(null)}
      body="homefront runs on the HTPC, so this page goes offline until you wake it with the keyboard or the TV remote. Jellyfin and Home Assistant pause too."
      onConfirm=${() => act('/api/pc/power/sleep', {}, 'Going to sleep')} />`}`;
}

const Bar = ({ label, value, max, text, warn }) => html`
  <div class=${'bar-row' + (warn ? ' warn' : '')}><span>${label}</span><div class="progress"><i style=${`width:${max ? Math.min(100, (value / max) * 100) : 0}%`}></i></div><span class="num">${text}</span></div>`;

// ================= launch =================

export function Launch() {
  const s = useStore();
  const [busy, setBusy] = useState(null);
  const [link, setLink] = useState('');
  const open = async (name, url) => { setBusy(name); try { await act('/api/pc/open', { url }, `Opening ${name} on the TV`); } finally { setTimeout(() => setBusy(null), 800); } };
  const sendLink = e => {
    e.preventDefault();
    let u = link.trim(); if (!u) return;
    if (!/^https?:\/\//i.test(u)) u = 'https://' + u;
    open('your link', u).then(() => setLink(''));
  };
  return html`
    <div class="launch">
      ${(s.shortcuts || []).map(sc => html`<button class=${'svc' + (busy === sc.name ? ' busy' : '')} style=${`--c:${sc.color}`} onClick=${() => open(sc.name, sc.url)}>${sc.name}</button>`)}
    </div>
    <form class="send-link" onSubmit=${sendLink}>
      <input class="input" type="url" inputmode="url" placeholder="Paste a link to open on the TV" value=${link} onInput=${e => setLink(e.target.value)} aria-label="Link to open on the TV" />
      <button class="icon-btn" type="submit" aria-label="Open on TV"><${Icon} name="send" /></button>
    </form>`;
}

// ================= today: weather + prayer =================

export function Weather() {
  const s = useStore();
  const w = s.weather;
  if (!w) return html`<div class="skeleton" style="height:160px"></div>`;
  const now = wx(w.now.code, w.now.isDay);
  const lo = Math.min(...w.daily.map(d => d.min)), hi = Math.max(...w.daily.map(d => d.max));
  return html`
    <div class="row between">
      <div>
        <div style="font:400 52px/1 var(--display);letter-spacing:-.05em" class="num">${Math.round(w.now.temp)}°</div>
        <div class="muted" style="margin-top:6px">${now.text}, feels ${Math.round(w.now.feels)}°</div>
      </div>
      <${Icon} name=${now.icon} size=${44} />
    </div>
    <div class="hourly" aria-label="Next hours">
      ${w.hourly.filter((_, i) => i % 2 === 0).slice(0, 10).map(hr => html`
        <div class="hour"><span>${hr.time.slice(11, 13)}</span><${Icon} name=${wx(hr.code, hr.isDay).icon} size=${18} /><b>${Math.round(hr.temp)}°</b>${hr.rain >= 30 ? html`<span class="micro">${hr.rain}%</span>` : html`<span class="micro"> </span>`}</div>`)}
    </div>
    <div class="days">
      ${w.daily.slice(0, 5).map((d, i) => html`
        <div class="day">
          <span class="muted">${i === 0 ? 'Today' : new Date(d.date + 'T12:00').toLocaleDateString(undefined, { weekday: 'short' })}</span>
          <${Icon} name=${wx(d.code).icon} size=${18} />
          <div class="temp-range"><i style=${`left:${((d.min - lo) / (hi - lo || 1)) * 100}%;right:${100 - ((d.max - lo) / (hi - lo || 1)) * 100}%`}></i></div>
          <span class="num small"><span class="muted">${Math.round(d.min)}°</span>  ${Math.round(d.max)}°</span>
        </div>`)}
    </div>`;
}

export function Prayer() {
  const s = useStore();
  const now = useNow(30000);
  const info = prayerInfo(s.prayer, now);
  if (!s.prayer) return html`<div class="muted">Prayer times are off. Turn them on in Settings.</div>`;
  if (!info) return html`<div class="skeleton" style="height:120px"></div>`;
  const dayPct = d => ((d.getHours() * 60 + d.getMinutes()) / 1440) * 100;
  const sr = info.sunrise.split(':').map(Number), ss = info.list.find(x => x.key === 'maghrib').at;
  const sunrisePct = ((sr[0] * 60 + sr[1]) / 1440) * 100, sunsetPct = dayPct(ss);
  return html`
    <div class="prayer-next">
      <b>${info.next.name} ${info.next.time}</b>
      <span class="muted">${info.next.tomorrow ? 'tomorrow' : until(info.mins)}</span>
    </div>
    ${info.hijri && html`<div class="muted small" style="margin-top:6px">${info.hijri.day} ${info.hijri.monthName} ${info.hijri.year}</div>`}
    <div class="timeline" aria-label="Today's prayer times">
      <div class="track"><div class="daylight" style=${`left:${sunrisePct}%;width:${sunsetPct - sunrisePct}%`}></div></div>
      ${info.list.map((p, i) => html`
        <span class=${'tick' + (p.key === info.next.key && !info.next.tomorrow ? ' next' : '')} style=${`left:${dayPct(p.at)}%`}></span>
        <span class=${'label' + (i % 2 ? ' up' : '') + (p.key === info.next.key && !info.next.tomorrow ? ' next' : '')} style=${`left:${Math.min(95, Math.max(5, dayPct(p.at)))}%`}>${p.name}</span>`)}
      <span class="now" style=${`left:${dayPct(now)}%`}></span>
    </div>
    ${info.ramadan && html`
      <div class="ramadan">
        <div><span class="muted small">Sehri ends</span><b class="num">${hhmm(info.sehri)}</b></div>
        <div><span class="muted small">Iftar</span><b class="num">${hhmm(info.iftar)}</b></div>
      </div>`}`;
}

// ================= library pieces =================

export function Tile({ item, poster, onOpen }) {
  const img = poster ? (item.hasPrimary ? jfImg(item.id, 'Primary', 360) : null) : thumbFor(item, 640);
  const prog = item.position && item.runtime ? item.position / item.runtime : 0;
  const title = item.type === 'Episode' ? item.seriesName : item.name;
  const sub = item.type === 'Episode' ? `${epLabel(item)}  ${item.name}` : [item.year, item.runtime && runtime(item.runtime)].filter(Boolean).join('  ');
  return html`
    <button class=${'tile' + (poster ? ' poster' : '')} onClick=${() => onOpen(item)}>
      <div class="img">
        ${img ? html`<img src=${img} alt="" loading="lazy" />` : html`<div style="position:absolute;inset:0;display:grid;place-items:center" class="faint"><${Icon} name="film" size=${28} /></div>`}
        ${prog > 0.01 && html`<div class="progress"><i style=${`width:${prog * 100}%`}></i></div>`}
        ${item.unplayed > 0 && poster && html`<span class="badge">${item.unplayed}</span>`}
      </div>
      <div style="min-width:0"><b class="ellipsis">${title}</b><span class="ellipsis" style="display:block">${sub}</span></div>
    </button>`;
}

export function ItemSheet({ id, onClose }) {
  const [data, setData] = useState(null);
  const [season, setSeason] = useState(null);
  const [eps, setEps] = useState(null);
  const s = useStore();
  useEffect(() => { api('/api/jf/item/' + id).then(d => { setData(d); const firstUnwatched = d.seasons?.find(x => x.unplayed > 0) || d.seasons?.[0]; if (firstUnwatched) setSeason(firstUnwatched.id); }).catch(e => toast(e.message, true)); }, [id]);
  useEffect(() => { if (season && data) { setEps(null); api(`/api/jf/episodes/${data.item.id}/${season}`).then(setEps); } }, [season, data]);
  const play = (itemId, fromStart) => act('/api/jf/play', { id: itemId, fromStart }, 'Starting on the TV').then(onClose);

  const it = data?.item;
  const backdrop = it && (it.hasBackdrop ? jfImg(it.id, 'Backdrop', 1280) : it.parentBackdropId ? jfImg(it.parentBackdropId, 'Backdrop', 1280) : null);
  const poster = it && !backdrop && (it.hasPrimary ? jfImg(it.id, 'Primary', 400) : null);
  const nextEp = eps?.find(e => !e.played) || eps?.[0];
  return html`<${Sheet} onClose=${onClose} wide label=${it?.name || 'Details'}>
    ${!it ? html`<div style="padding:28px"><div class="skeleton" style="height:280px"></div></div>` : html`
      ${backdrop && html`<div class="detail-hero"><img src=${backdrop} alt="" /><div class="veil"></div></div>`}
      <div class=${'detail-body' + (backdrop ? '' : ' plain')}>
        ${poster && html`<img class="detail-poster" src=${poster} alt="" />`}
        <h2>${it.type === 'Episode' ? it.seriesName : it.name}</h2>
        <div class="facts">
          ${it.type === 'Episode' && html`<span>${epLabel(it)}  ${it.name}</span>`}
          ${it.year && html`<span>${it.year}</span>`}
          ${it.runtime > 0 && it.type !== 'Series' && html`<span>${runtime(it.runtime)}</span>`}
          ${it.rating && html`<span>${it.rating}</span>`}
          ${it.score > 0 && html`<span class="row" style="gap:4px"><${Icon} name="star" size=${14} />${it.score.toFixed(1)}</span>`}
          ${it.genres?.length > 0 && html`<span>${it.genres.join(', ')}</span>`}
        </div>
        <div class="row wrap">
          ${it.type !== 'Series' && html`
            <button class="btn primary" onClick=${() => play(it.id)}><${Icon} name="play" />${it.position > 30 ? `Resume from ${dur(it.position)}` : 'Play on TV'}</button>
            ${it.position > 30 && html`<button class="btn" onClick=${() => play(it.id, true)}><${Icon} name="rotate-ccw" />Start over</button>`}`}
          ${it.type === 'Series' && nextEp && html`<button class="btn primary" onClick=${() => play(nextEp.id)}><${Icon} name="play" />Play ${epLabel(nextEp)}</button>`}
          ${s.player?.status !== 'stopped' && s.player?.itemId === it.id && html`<span class="muted small">Playing now</span>`}
        </div>
        ${it.overview && html`<p class="overview">${it.overview}</p>`}
        ${data.seasons?.length > 0 && html`
          <div class="row wrap" style="gap:8px;margin-top:22px">
            ${data.seasons.map(se => html`<button class="chip" aria-pressed=${se.id === season} onClick=${() => setSeason(se.id)}>${se.name}</button>`)}
          </div>
          <div class="episodes">
            ${!eps ? html`<div class="skeleton" style="height:90px"></div>` : eps.map(ep => html`
              <button class="ep" onClick=${() => play(ep.id)}>
                <div class="img">${ep.hasPrimary && html`<img src=${jfImg(ep.id, 'Primary', 320)} alt="" loading="lazy" />`}
                  ${ep.position > 0 && ep.runtime > 0 && html`<div class="progress"><i style=${`width:${(ep.position / ep.runtime) * 100}%`}></i></div>`}</div>
                <div style="min-width:0"><b style="font-weight:500">${ep.episode}. ${ep.name}</b>${ep.played && html` <span class="muted small">Watched</span>`}<p>${ep.overview || ''}</p></div>
                <span class="icon-btn plain ep-play"><${Icon} name="play" /></span>
              </button>`)}
          </div>`}
      </div>`}
  </${Sheet}>`;
}

// ================= subtitles & audio =================

export function TracksSheet({ tracks, onClose }) {
  const t = tracks || {};
  const pick = (cmd, index) => act('/api/player/' + cmd, { index });
  return html`<${Sheet} onClose=${onClose} label="Subtitles and audio">
    <h2 class="h-sec" style="font-size:20px;margin-bottom:14px;margin-right:48px">Subtitles</h2>
    <div class="track-list" role="radiogroup" aria-label="Subtitles">
      <button class="track" role="radio" aria-checked=${t.sub == null} onClick=${() => pick('subs', null)}><span>Off</span>${t.sub == null && html`<${Icon} name="check" size=${18} />`}</button>
      ${(t.subtitles || []).map(s => html`
        <button class="track" role="radio" aria-checked=${t.sub === s.index} onClick=${() => pick('subs', s.index)}>
          <span>${s.name}</span><span class="row" style="gap:10px">${!s.isText && html`<small>Drawn into the video</small>`}${t.sub === s.index && html`<${Icon} name="check" size=${18} />`}</span>
        </button>`)}
    </div>
    ${t.audio?.length > 1 && html`
      <h2 class="h-sec" style="font-size:20px;margin:24px 0 14px">Audio</h2>
      <div class="track-list" role="radiogroup" aria-label="Audio">
        ${t.audio.map(a => html`<button class="track" role="radio" aria-checked=${t.audioIndex === a.index} onClick=${() => pick('audio', a.index)}><span>${a.name}</span>${t.audioIndex === a.index && html`<${Icon} name="check" size=${18} />`}</button>`)}
      </div>`}
  </${Sheet}>`;
}

// ================= sleep timer =================

export function SleepChip() {
  const s = useStore();
  const now = useNow(15000);
  const [open, setOpen] = useState(false);
  const ends = s.timer?.endsAt;
  const left = ends ? Math.max(0, Math.ceil((ends - now.getTime()) / 60000)) : 0;
  const set = m => act('/api/timer', { minutes: m }, m ? `Everything turns off in ${m} minutes` : 'Sleep timer cancelled').then(() => setOpen(false));
  return html`
    <button class=${'chip' + (ends ? ' on' : '')} onClick=${() => setOpen(true)}><${Icon} name="timer" size=${16} />${ends ? `Off in ${left} min` : 'Sleep timer'}</button>
    ${open && html`<${Sheet} onClose=${() => setOpen(false)} label="Sleep timer">
      <h2 class="h-sec" style="font-size:20px;margin-bottom:6px;margin-right:48px">Sleep timer</h2>
      <p class="muted" style="margin:0 0 18px">Pauses what's playing, turns the TV off and fades the lights out. The TV shows a warning a minute before.</p>
      <div class="row wrap" style="gap:8px">
        ${[15, 30, 45, 60, 90, 120].map(m => html`<button class="chip" style="height:44px;padding:0 18px" onClick=${() => set(m)}>${m < 60 ? `${m} min` : `${m / 60} h${m % 60 ? ' ' + (m % 60) + ' min' : ''}`}</button>`)}
      </div>
      ${ends && html`<div class="row between" style="margin-top:20px;border-top:1px solid var(--line);padding-top:16px">
        <span class="muted">Turning off in ${left} min</span><button class="btn sm danger" onClick=${() => set(null)}>Cancel timer</button></div>`}
    </${Sheet}>`}`;
}

// ================= cameras =================

export function CameraPanel() {
  const s = useStore();
  const ref = useRef();
  const visible = useVisible(ref);
  const [big, setBig] = useState(null);
  const ids = Object.keys(s.entities).filter(k => k.startsWith('camera.'));
  if (!ids.length) return null;
  const name = id => s.entities[id]?.attributes?.friendly_name || id.replace('camera.', '').replace(/_/g, ' ');
  return html`
    <div ref=${ref} class="stack">
      ${ids.map(id => html`
        <button class="camera" onClick=${() => setBig(id)} aria-label=${`Open ${name(id)}`}>
          ${visible ? html`<img src=${`/api/camera/${id}/stream`} alt=${name(id)} />` : html`<div class="skeleton" style="position:absolute;inset:0;border-radius:0"></div>`}
          <span class="tag"><span class="dot live"></span>${name(id)}</span>
        </button>`)}
    </div>
    ${big && html`<${Sheet} onClose=${() => setBig(null)} wide label=${name(big)}>
      <div style="padding:20px">
        <h2 class="h-sec" style="font-size:20px;margin-bottom:14px;margin-right:48px">${name(big)}</h2>
        <img class="camera-big" src=${`/api/camera/${big}/stream`} alt=${name(big)} />
      </div>
    </${Sheet}>`}`;
}
