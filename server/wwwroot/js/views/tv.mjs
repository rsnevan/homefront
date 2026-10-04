import { useState, useEffect, useRef } from 'preact/hooks';
import { html, useStore, useNow, hhmm, dateLong, wx, prayerInfo, until, dur, epLabel, pad, thumbFor } from '../lib.mjs';
import { Plan, nowPlaying } from '../components.mjs';

// ================= ambient (TV wallpaper) =================

export function Ambient() {
  const s = useStore();
  const now = useNow(1000);
  const [shift, setShift] = useState([0, 0]);
  const [shelf, setShelf] = useState([]);
  const [shelfName, setShelfName] = useState('Continue watching');
  const [sel, setSel] = useState(0);
  const selRef = useRef(0), shelfRef = useRef([]);
  selRef.current = sel; shelfRef.current = shelf;
  useEffect(() => {
    const t = setInterval(() => setShift([Math.round((Math.random() - 0.5) * 40), Math.round((Math.random() - 0.5) * 30)]), 60000);
    // Continue watching: in-progress titles first, then the next episode of shows you're following.
    const load = () => fetch('/api/jf/home').then(r => r.ok ? r.json() : {}).then(h => {
      const seen = new Set();
      const cont = [...(h.resume || []), ...(h.nextUp || [])].filter(i => !seen.has(i.seriesId || i.id) && seen.add(i.seriesId || i.id));
      // Nothing in progress yet: show what's new instead of an empty row.
      const items = cont.length ? cont : [...(h.movies || []), ...(h.shows || [])];
      setShelfName(cont.length ? 'Continue watching' : 'New in your library');
      setShelf(items.slice(0, 5));
    }).catch(() => {});
    load();
    const l = setInterval(load, 600000);
    const play = item => fetch('/api/jf/play', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: item.id }) });
    const key = e => {
      const n = shelfRef.current.length;
      if (e.key === 'ArrowRight' && n) setSel((selRef.current + 1) % n);
      else if (e.key === 'ArrowLeft' && n) setSel((selRef.current - 1 + n) % n);
      else if (e.key === 'Enter' && n) play(shelfRef.current[selRef.current]);
      else if (e.key === 'Escape' || e.key === 'Backspace') fetch('/api/kiosk/close', { method: 'POST' });
    };
    addEventListener('keydown', key);
    window.__ambientPlay = play;
    return () => { clearInterval(t); clearInterval(l); removeEventListener('keydown', key); };
  }, []);
  const w = s.weather?.now;
  const pi = prayerInfo(s.prayer, now);
  const np = nowPlaying(s);
  // Ramadan: the last hour before Iftar takes over the clock.
  const toIftar = pi?.ramadan ? (pi.iftar - now) / 1000 : -1;
  const iftarSoon = toIftar > 0 && toIftar <= 3600;
  const countdown = iftarSoon ? `${Math.floor(toIftar / 60)}:${pad(Math.floor(toIftar % 60))}` : null;
  return html`
    <div class="ambient">
      <div class="amb-inner" style=${`transform:translate(${shift[0]}px,${shift[1]}px)`}>
        <div>
          ${iftarSoon ? html`
            <div class="amb-date" style="margin:0 0 2vh;color:var(--accent)">Iftar at ${hhmm(pi.iftar)}</div>
            <div class="amb-time">${countdown}</div>
            <div class="amb-date">${hhmm(now)}, ${dateLong(now)}</div>`
          : html`
            <div class="amb-time">${hhmm(now)}</div>
            <div class="amb-date">${dateLong(now)}</div>`}
        </div>
        <div class="amb-side">
          ${w && html`<div><div class="big num">${Math.round(w.temp)}°</div><div style="color:var(--muted);margin-top:.6vh">${wx(w.code, w.isDay).text}</div></div>`}
          ${pi?.next && !iftarSoon && html`<div><div class="big">${pi.next.name}</div><div style="color:var(--muted);margin-top:.6vh">${pi.next.time}, ${pi.next.tomorrow ? 'tomorrow' : until(pi.mins)}</div></div>`}
          ${pi?.ramadan && !iftarSoon && html`<div><div class="big num">${hhmm(pi.iftar)}</div><div style="color:var(--muted);margin-top:.6vh">Iftar today</div></div>`}
          ${s.rooms?.length > 0 && html`<div class="amb-plan"><${Plan} mini /></div>`}
        </div>
        ${shelf.length > 0 && html`
          <div class="amb-shelf" aria-label=${shelfName}>
            <span class="amb-shelf-name">${shelfName}</span>
            ${shelf.map((it, i) => {
              const img = thumbFor(it, 480);
              const prog = it.position && it.runtime ? it.position / it.runtime : 0;
              return html`<button key=${it.id} class=${'amb-card' + (i === sel ? ' on' : '')} onClick=${() => window.__ambientPlay?.(it)} onMouseEnter=${() => setSel(i)}>
                <div class="amb-img">${img && html`<img src=${img} alt="" />`}${prog > 0.01 && html`<i style=${`width:${prog * 100}%`}></i>`}</div>
                <span class="amb-title">${it.type === 'Episode' ? it.seriesName : it.name}</span>
                <span class="amb-sub">${it.type === 'Episode' ? epLabel(it) : it.year || ''}</span>
              </button>`;
            })}
          </div>`}
        <div class="amb-foot">
          ${np ? html`${np.art && html`<img src=${np.art} alt="" />`}<div><div style="color:var(--chalk)">${np.title}</div><div>${np.sub || np.app}</div></div>`
               : html`<span style="display:flex;align-items:center;gap:1vw"><svg width="22" height="22" viewBox="0 0 48 48" fill="none"><path d="M10 38V10H38V38H26M10 38H38M10 22H24M24 10V28M24 28H38" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="26" cy="38" r="3" fill="#ffc94d"/></svg>${s.home?.name || 'homefront'}</span>`}
        </div>
      </div>
    </div>`;
}

// ================= kiosk player =================

const loadHls = () => window.Hls ? Promise.resolve() : new Promise(res => {
  const sc = document.createElement('script'); sc.src = '/vendor/hls.min.js'; sc.onload = sc.onerror = res; document.head.appendChild(sc);
});

export function Player() {
  const videoRef = useRef();
  const hlsRef = useRef();
  const wsRef = useRef();
  const srcRef = useRef(null);
  const subRef = useRef(null);          // index of the subtitle stream being shown, or null
  const [src, setSrc] = useState(null);
  const [status, setStatus] = useState('loading');
  const [osd, setOsd] = useState(true);
  const [upNext, setUpNext] = useState(null);
  const [err, setErr] = useState(null);
  const [notice, setNotice] = useState(null);
  const osdTimer = useRef(), noticeTimer = useRef();
  const now = useNow(500);

  const showOsd = (ms = 3500) => { setOsd(true); clearTimeout(osdTimer.current); osdTimer.current = setTimeout(() => !videoRef.current?.paused && setOsd(false), ms); };
  const flash = text => { setNotice(text); clearTimeout(noticeTimer.current); noticeTimer.current = setTimeout(() => setNotice(null), 2500); };

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
      tracks: {
        subtitles: (sr.subtitles || []).map(t => ({ index: t.index, name: t.name, isText: t.isText })),
        audio: (sr.audio || []).map(t => ({ index: t.index, name: t.name })),
        sub: subRef.current, audioIndex: sr.audioIndex ?? null,
      },
    }));
  };

  // Show one text track (or none). Image tracks are handled by reloading with burn-in.
  const applyTextTrack = index => {
    const v = videoRef.current; if (!v) return;
    for (const tr of v.textTracks) tr.mode = tr.label === `s${index}` ? 'showing' : 'disabled';
  };

  async function load(id, opts = {}) {
    setErr(null); setStatus('loading'); setUpNext(null);
    if (srcRef.current) report({ status: 'stopped' });
    const q = new URLSearchParams({ id });
    if (opts.audio != null) q.set('audio', opts.audio);
    if (opts.burn != null) q.set('burn', opts.burn);
    const r = await fetch('/api/player/source?' + q);
    if (!r.ok) { setErr("That title couldn't be loaded from Jellyfin."); return; }
    const sr = await r.json();

    // Which subtitle: an explicit choice, else the preferred-language default.
    const want = 'sub' in opts ? opts.sub : sr.defaultSub;
    const track = sr.subtitles.find(t => t.index === want);
    if (track && !track.isText && opts.burn !== want) return load(id, { ...opts, burn: want, sub: want });   // picture subs: burn in
    subRef.current = track ? want : null;

    srcRef.current = sr; setSrc(sr);
    const v = videoRef.current;
    hlsRef.current?.destroy();
    await loadHls();
    [...v.querySelectorAll('track')].forEach(t => t.remove());
    for (const t of sr.subtitles.filter(t => t.isText)) {
      const el = document.createElement('track');
      el.kind = 'subtitles'; el.label = `s${t.index}`; el.srclang = (t.lang || 'und').slice(0, 2); el.src = t.vtt;
      v.appendChild(el);
    }
    const start = opts.start ?? (opts.fromStart ? 0 : sr.resume || 0);
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
    } else { v.src = sr.hls; v.currentTime = start; }
    v.play().catch(() => {});
    setTimeout(() => applyTextTrack(track?.isText ? want : null), 300);
    showOsd(5000);
    report();
  }

  async function setSubtitle(index) {
    const sr = srcRef.current, v = videoRef.current; if (!sr || !v) return;
    const track = sr.subtitles.find(t => t.index === index);
    const pos = v.currentTime;
    if (sr.burnSub != null && sr.burnSub !== index) { await load(sr.itemId, { start: pos, audio: sr.audioIndex, sub: index }); flash(track ? track.name : 'Subtitles off'); return; }
    if (track && !track.isText) { await load(sr.itemId, { start: pos, audio: sr.audioIndex, sub: index, burn: index }); flash(track.name); return; }
    subRef.current = track ? index : null;
    applyTextTrack(track ? index : null);
    flash(track ? track.name : 'Subtitles off');
    report();
  }

  async function setAudio(index) {
    const sr = srcRef.current, v = videoRef.current; if (!sr || !v || index === sr.audioIndex) return;
    const name = sr.audio.find(a => a.index === index)?.name;
    await load(sr.itemId, { start: v.currentTime, audio: index, sub: subRef.current, burn: sr.burnSub ?? undefined });
    if (name) flash(name);
  }

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const id = params.get('id');
    const ws = new WebSocket(`ws://${location.host}/api/ws?kind=player`);
    wsRef.current = ws;
    ws.onmessage = ev => {
      const { t, d } = JSON.parse(ev.data);
      const v = videoRef.current;
      if (t === 'load') load(d.id, { fromStart: d.fromStart });
      else if (t === 'next') setUpNext({ id: d.id, at: Date.now() + 10000 });
      else if (t === 'cmd' && v) {
        if (d.cmd === 'play') v.play();
        else if (d.cmd === 'pause') v.pause();
        else if (d.cmd === 'toggle') v.paused ? v.play() : v.pause();
        else if (d.cmd === 'seek' && d.position != null) v.currentTime = Math.max(0, d.position);
        else if (d.cmd === 'subs') setSubtitle(d.index ?? null);
        else if (d.cmd === 'audio' && d.index != null) setAudio(d.index);
        showOsd();
      }
    };
    ws.onopen = () => { if (id) load(id, { fromStart: params.get('start') === '0' }); };
    const iv = setInterval(() => report(), 2000);
    const key = e => {
      const v = videoRef.current; if (!v) return;
      if (e.key === ' ' || e.key === 'Enter' || e.key === 'k') { v.paused ? v.play() : v.pause(); }
      else if (e.key === 'ArrowRight') v.currentTime += 30;
      else if (e.key === 'ArrowLeft') v.currentTime -= 10;
      else if (e.key === 'c') {
        // cycle subtitles: off -> each track -> off
        const list = [null, ...(srcRef.current?.subtitles || []).map(t => t.index)];
        setSubtitle(list[(list.indexOf(subRef.current) + 1) % list.length]);
      }
      else if (e.key === 'Escape' || e.key === 'Backspace') { report({ status: 'stopped' }); fetch('/api/player/stop', { method: 'POST' }); }
      showOsd();
    };
    addEventListener('keydown', key);
    addEventListener('mousemove', () => showOsd());
    return () => { clearInterval(iv); removeEventListener('keydown', key); ws.close(); hlsRef.current?.destroy(); };
  }, []);

  useEffect(() => {
    if (!upNext) return;
    if (now.getTime() >= upNext.at) { const id = upNext.id; setUpNext(null); load(id, { fromStart: true }); }
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
      ${notice && html`<div class="player-notice">${notice}</div>`}
      ${it && html`
        <div class=${'osd' + (osd || status !== 'playing' ? '' : ' hidden')}>
          <h1>${it.type === 'Episode' ? it.seriesName : it.name}</h1>
          <p>${it.type === 'Episode' ? `${epLabel(it)}, ${it.name}` : it.year || ''}</p>
          <div class="progress"><i style=${`width:${total ? (pos / total) * 100 : 0}%`}></i></div>
          <div class="times"><span>${dur(pos)}</span><span>${status === 'paused' ? 'Paused' : ''}</span><span>-${dur(total - pos)}</span></div>
        </div>`}
      ${upNext && html`<div class="upnext"><span style="color:rgba(230,236,244,.65)">Next episode in ${Math.max(0, Math.ceil((upNext.at - now.getTime()) / 1000))}s</span><b>Up next</b></div>`}
    </div>`;
}
