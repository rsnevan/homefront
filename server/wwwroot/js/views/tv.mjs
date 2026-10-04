import { useState, useEffect, useRef } from 'preact/hooks';
import { html, Icon, useStore, useNow, hhmm, dateLong, wx, prayerInfo, until, dur, epLabel, jfImg } from '../lib.mjs';
import { Plan, nowPlaying } from '../components.mjs';

// ================= ambient (TV wallpaper) =================

export function Ambient() {
  const s = useStore();
  const now = useNow(1000);
  const [shift, setShift] = useState([0, 0]);
  useEffect(() => {
    const t = setInterval(() => setShift([Math.round((Math.random() - 0.5) * 40), Math.round((Math.random() - 0.5) * 30)]), 60000);
    const close = () => fetch('/api/kiosk/close', { method: 'POST' });
    addEventListener('keydown', close);
    return () => { clearInterval(t); removeEventListener('keydown', close); };
  }, []);
  const w = s.weather?.now;
  const pi = prayerInfo(s.prayer, now);
  const np = nowPlaying(s);
  return html`
    <div class="ambient">
      <div class="amb-inner" style=${`transform:translate(${shift[0]}px,${shift[1]}px)`}>
        <div>
          <div class="amb-time">${hhmm(now)}</div>
          <div class="amb-date">${dateLong(now)}</div>
        </div>
        <div class="amb-side">
          ${w && html`<div><div class="big num">${Math.round(w.temp)}°</div><div style="color:var(--muted);margin-top:.6vh">${wx(w.code, w.isDay).text}</div></div>`}
          ${pi?.next && html`<div><div class="big">${pi.next.name}</div><div style="color:var(--muted);margin-top:.6vh">${pi.next.time}, ${pi.next.tomorrow ? 'tomorrow' : until(pi.mins)}</div></div>`}
          ${s.rooms?.length > 0 && html`<div class="amb-plan"><${Plan} mini /></div>`}
        </div>
        <div class="amb-foot">
          ${np ? html`${np.art && html`<img src=${np.art} alt="" />`}<div><div style="color:#e6ecf4">${np.title}</div><div>${np.sub || np.app}</div></div>`
               : html`<span style="display:flex;align-items:center;gap:1vw"><svg width="22" height="22" viewBox="0 0 48 48" fill="none"><path d="M10 38V10H38V38H26M10 38H38M10 22H24M24 10V28M24 28H38" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="26" cy="38" r="3" fill="#ffc94d"/></svg>${s.home?.name || 'homefront'}</span>`}
        </div>
      </div>
    </div>`;
}

// ================= kiosk player =================

export function Player() {
  const videoRef = useRef();
  const hlsRef = useRef();
  const wsRef = useRef();
  const srcRef = useRef(null);
  const [src, setSrc] = useState(null);
  const [status, setStatus] = useState('loading');
  const [osd, setOsd] = useState(true);
  const [upNext, setUpNext] = useState(null);
  const [err, setErr] = useState(null);
  const osdTimer = useRef();
  const now = useNow(500);

  const showOsd = (ms = 3500) => { setOsd(true); clearTimeout(osdTimer.current); osdTimer.current = setTimeout(() => !videoRef.current?.paused && setOsd(false), ms); };

  const report = (extra = {}) => {
    const v = videoRef.current, sr = srcRef.current;
    if (!sr || wsRef.current?.readyState !== 1) return;
    const st = !v || v.ended ? 'stopped' : v.paused ? 'paused' : 'playing';
    wsRef.current.send(JSON.stringify({
      t: 'state', status: extra.status || st, position: v?.currentTime || 0, duration: v?.duration || sr.runtime || 0,
      itemId: sr.itemId, mediaSourceId: sr.mediaSourceId, playSessionId: sr.playSessionId,
      title: sr.item.type === 'Episode' ? sr.item.seriesName : sr.item.name,
      subtitle: sr.item.type === 'Episode' ? `${epLabel(sr.item)}  ${sr.item.name}` : sr.item.year ? String(sr.item.year) : '',
      imageId: sr.item.hasBackdrop ? sr.item.id : sr.item.parentBackdropId || sr.item.seriesId || sr.item.id,
    }));
  };

  async function load(id, fromStart) {
    setErr(null); setStatus('loading'); setUpNext(null);
    if (srcRef.current) report({ status: 'stopped' });
    const r = await fetch('/api/player/source?id=' + encodeURIComponent(id));
    if (!r.ok) { setErr("That title couldn't be loaded from Jellyfin."); return; }
    const sr = await r.json();
    srcRef.current = sr; setSrc(sr);
    const v = videoRef.current;
    hlsRef.current?.destroy();
    if (!window.Hls) await new Promise(res => { const sc = document.createElement('script'); sc.src = '/vendor/hls.min.js'; sc.onload = sc.onerror = res; document.head.appendChild(sc); });
    const start = fromStart ? 0 : sr.resume || 0;
    if (window.Hls?.isSupported()) {
      const hls = new window.Hls({ startPosition: start, maxBufferLength: 40, backBufferLength: 30 });
      hlsRef.current = hls;
      hls.on(window.Hls.Events.ERROR, (_, d) => {
        if (!d.fatal) return;
        if (d.type === window.Hls.ErrorTypes.NETWORK_ERROR) hls.startLoad();
        else if (d.type === window.Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
        else setErr('Playback stopped: ' + d.details);
      });
      hls.loadSource(sr.hls);
      hls.attachMedia(v);
    } else {
      v.src = sr.hls; v.currentTime = start;
    }
    v.play().catch(() => {});
    showOsd(5000);
  }

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const id = params.get('id');
    const ws = new WebSocket(`ws://${location.host}/api/ws?kind=player`);
    wsRef.current = ws;
    ws.onmessage = ev => {
      const { t, d } = JSON.parse(ev.data);
      const v = videoRef.current;
      if (t === 'load') load(d.id, d.fromStart);
      else if (t === 'next') setUpNext({ id: d.id, at: Date.now() + 10000 });
      else if (t === 'cmd' && v) {
        if (d.cmd === 'play') v.play();
        else if (d.cmd === 'pause') v.pause();
        else if (d.cmd === 'toggle') v.paused ? v.play() : v.pause();
        else if (d.cmd === 'seek' && d.position != null) v.currentTime = Math.max(0, d.position);
        showOsd();
      }
    };
    ws.onopen = () => { if (id) load(id, params.get('start') === '0'); };
    const iv = setInterval(() => report(), 2000);
    const key = e => {
      const v = videoRef.current; if (!v) return;
      if (e.key === ' ' || e.key === 'Enter' || e.key === 'k') { v.paused ? v.play() : v.pause(); }
      else if (e.key === 'ArrowRight') v.currentTime += 30;
      else if (e.key === 'ArrowLeft') v.currentTime -= 10;
      else if (e.key === 'Escape' || e.key === 'Backspace') { report({ status: 'stopped' }); fetch('/api/player/stop', { method: 'POST' }); }
      showOsd();
    };
    addEventListener('keydown', key);
    addEventListener('mousemove', () => showOsd());
    return () => { clearInterval(iv); removeEventListener('keydown', key); ws.close(); hlsRef.current?.destroy(); };
  }, []);

  useEffect(() => {
    if (!upNext) return;
    if (now.getTime() >= upNext.at) { const id = upNext.id; setUpNext(null); load(id, true); }
  }, [now, upNext]);

  const v = videoRef.current;
  const pos = v?.currentTime || 0, total = v?.duration || src?.runtime || 0;
  const it = src?.item;
  return html`
    <div class="player">
      <video ref=${videoRef} playsinline autoplay
        onPlaying=${() => { setStatus('playing'); report(); showOsd(); }}
        onPause=${() => { setStatus('paused'); setOsd(true); report(); }}
        onWaiting=${() => setStatus('loading')}
        onEnded=${() => { report({ status: 'stopped' }); wsRef.current?.send(JSON.stringify({ t: 'ended' })); }}></video>
      ${(status === 'loading' && !err) && html`<div class="center"><div class="spinner"></div></div>`}
      ${err && html`<div class="center" style="pointer-events:auto"><div style="text-align:center;max-width:40ch"><p style="font-size:2.4vh">${err}</p></div></div>`}
      ${it && html`
        <div class=${'osd' + (osd || status !== 'playing' ? '' : ' hidden')}>
          <h1>${it.type === 'Episode' ? it.seriesName : it.name}</h1>
          <p>${it.type === 'Episode' ? `${epLabel(it)}, ${it.name}` : it.year || ''}</p>
          <div class="progress"><i style=${`width:${total ? (pos / total) * 100 : 0}%`}></i></div>
          <div class="times"><span>${dur(pos)}</span><span>${status === 'paused' ? 'Paused' : ''}</span><span>-${dur(total - pos)}</span></div>
        </div>`}
      ${upNext && html`<div class="upnext"><span style="color:rgba(230, 236, 244, .65)">Next episode in ${Math.max(0, Math.ceil((upNext.at - now.getTime()) / 1000))}s</span><b>Up next</b></div>`}
    </div>`;
}
