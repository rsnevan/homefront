import { useState, useRef, useEffect } from 'preact/hooks';
import { html, Icon, useStore, useScreen, send, act, api, toast, useThrottled } from '../lib.mjs';
import { Range, NowPlaying } from '../components.mjs';

export function Remote() {
  const s = useStore();
  const [mode, setMode] = useState(() => { try { return localStorage.getItem('hf-remote-mode') || 'tap'; } catch { return 'tap'; } });
  const [hd, setHd] = useState(false);
  const mirrorRef = useRef();
  const src = useScreen(mirrorRef, hd ? 1600 : 960, hd ? 900 : 550);
  const [ripples, setRipples] = useState([]);
  const [text, setText] = useState('');
  useEffect(() => { try { localStorage.setItem('hf-remote-mode', mode); } catch {} }, [mode]);

  const ripple = (x, y) => {
    const r = { id: Math.random(), x, y };
    setRipples(list => [...list, r]);
    setTimeout(() => setRipples(list => list.filter(i => i !== r)), 520);
  };

  // ---- pointer handling on the mirror ----
  const pointers = useRef(new Map());
  const gesture = useRef(null);
  const moveAcc = useRef({ dx: 0, dy: 0, raf: 0 });
  const flushMove = () => {
    const m = moveAcc.current;
    m.raf = 0;
    if (Math.abs(m.dx) + Math.abs(m.dy) < 0.3) return;
    send({ t: 'move', dx: m.dx, dy: m.dy });
    m.dx = 0; m.dy = 0;
  };
  const queueMove = (dx, dy) => {
    const m = moveAcc.current;
    m.dx += dx; m.dy += dy;
    if (!m.raf) m.raf = requestAnimationFrame(flushMove);
  };
  const norm = e => {
    const r = mirrorRef.current.getBoundingClientRect();
    return { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height, px: e.clientX - r.left, py: e.clientY - r.top, w: r.width };
  };

  const onDown = e => {
    e.currentTarget.setPointerCapture(e.pointerId);
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const n = norm(e);
    if (pointers.current.size === 1) {
      gesture.current = { start: Date.now(), sx: e.clientX, sy: e.clientY, moved: 0, n, two: false, long: setTimeout(() => {
        if (gesture.current && gesture.current.moved < 8) {
          gesture.current.longFired = true;
          if (mode === 'tap') send({ t: 'moveTo', x: n.x, y: n.y });
          send({ t: 'click', b: 'right' }); ripple(n.px, n.py); navigator.vibrate?.(15);
        }
      }, 550) };
    } else if (gesture.current) { gesture.current.two = true; clearTimeout(gesture.current.long); }
  };

  const onMove = e => {
    const prev = pointers.current.get(e.pointerId);
    if (!prev || !gesture.current) return;
    const dx = e.clientX - prev.x, dy = e.clientY - prev.y;
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    gesture.current.moved += Math.abs(dx) + Math.abs(dy);
    if (gesture.current.moved > 8) clearTimeout(gesture.current.long);
    if (gesture.current.two) {
      send({ t: 'scroll', dy: dy * 4, dx: -dx * 4 });
      return;
    }
    if (mode === 'pad') {
      const w = mirrorRef.current.getBoundingClientRect().width;
      const speed = Math.hypot(dx, dy);
      const gain = (s.screen?.w || 1920) / w * (0.45 + Math.min(speed, 30) / 40);
      queueMove(dx * gain, dy * gain);
    } else if (gesture.current.moved > 12) {
      const n = norm(e);
      send({ t: 'moveTo', x: n.x, y: n.y });
    }
  };

  const onUp = e => {
    const g = gesture.current;
    pointers.current.delete(e.pointerId);
    if (!g || pointers.current.size > 0) return;
    clearTimeout(g.long);
    gesture.current = null;
    if (g.longFired) return;
    const quick = Date.now() - g.start < 350 && g.moved < 10;
    if (g.two && quick) { send({ t: 'click', b: 'right' }); return; }
    if (!quick) return;
    const n = norm(e);
    if (mode === 'tap') send({ t: 'tapAt', x: n.x, y: n.y });
    else send({ t: 'click', b: 'left' });
    ripple(n.px, n.py);
  };

  const key = k => send({ t: 'key', k });
  const typeIt = e => {
    e.preventDefault();
    if (!text) { key('enter'); return; }
    send({ t: 'type', s: text });
    setText('');
  };
  const vol = s.volume || {};
  const setVol = useThrottled(v => api('/api/pc/volume', { level: v }).catch(err => toast(err.message, true)), 150);

  return html`
    <div class="sec-head" style="margin-bottom:18px"><h1 class="h-page">Remote</h1>
      <span class="muted small ellipsis remote-fg">${s.stats?.foreground?.title || ''}</span></div>
    <div class="remote">
      <div>
        <div ref=${mirrorRef} class=${'mirror' + (mode === 'pad' ? ' pad' : '')}
          onPointerDown=${onDown} onPointerMove=${onMove} onPointerUp=${onUp} onPointerCancel=${onUp} onContextMenu=${e => e.preventDefault()}
          role="application" aria-label=${mode === 'tap' ? 'HTPC screen. Tap to click there, hold to right-click.' : 'Touchpad. Drag to move, tap to click, two fingers to scroll.'}>
          ${src ? html`<img src=${src} alt="" draggable="false" />` : html`<div class="skeleton" style="position:absolute;inset:0;border-radius:0"></div>`}
          ${ripples.map(r => html`<span key=${r.id} class="ripple" style=${`left:${r.x}px;top:${r.y}px`}></span>`)}
        </div>
        <div class="mirror-bar">
          <button class="chip" aria-pressed=${mode === 'tap'} onClick=${() => setMode('tap')}><${Icon} name="mouse-pointer-click" size=${16} />Tap to click</button>
          <button class="chip" aria-pressed=${mode === 'pad'} onClick=${() => setMode('pad')}><${Icon} name="move" size=${16} />Touchpad</button>
          <button class="chip" aria-pressed=${hd} onClick=${() => setHd(!hd)}>Sharper preview</button>
          <span class="muted small mirror-hint">${mode === 'tap' ? 'Hold to right-click. Two fingers scroll.' : 'Two-finger tap right-clicks.'}</span>
        </div>
        <form class="send-link" onSubmit=${typeIt} style="margin-top:14px">
          <input class="input" placeholder="Type on the TV" value=${text} onInput=${e => setText(e.target.value)} aria-label="Text to type on the HTPC" autocomplete="off" autocapitalize="off" />
          <button class="btn" type="submit"><${Icon} name="corner-down-left" size=${18} />${text ? 'Type' : 'Enter'}</button>
        </form>
      </div>

      <div class="stack" style="gap:16px">
        <section class="panel">
          <div class="keys">
            <button class="key" onClick=${() => key('esc')}>Esc</button>
            <button class="key" onClick=${() => key('space')} aria-label="Space, play or pause video"><${Icon} name="play" size=${18} /></button>
            <button class="key" onClick=${() => key('f')} aria-label="Fullscreen video (F)"><${Icon} name="maximize" size=${18} /></button>
            <button class="key" onClick=${() => key('f11')}>F11</button>
            <button class="key" onClick=${() => key('alt+left')} aria-label="Back"><${Icon} name="undo-2" size=${18} /></button>
            <button class="key" onClick=${() => key('ctrl+w')} aria-label="Close tab">Close tab</button>
            <button class="key" onClick=${() => key('backspace')} aria-label="Backspace"><${Icon} name="delete" size=${18} /></button>
            <button class="key" onClick=${() => key('tab')}>Tab</button>
          </div>
          <div class="dpad" style="margin-top:14px">
            <span></span><button class="key" onClick=${() => key('up')} aria-label="Up"><${Icon} name="arrow-up" /></button><span></span>
            <button class="key" onClick=${() => key('left')} aria-label="Left"><${Icon} name="arrow-left" /></button>
            <button class="key ok" onClick=${() => key('enter')} aria-label="Enter">OK</button>
            <button class="key" onClick=${() => key('right')} aria-label="Right"><${Icon} name="arrow-right" /></button>
            <span></span><button class="key" onClick=${() => key('down')} aria-label="Down"><${Icon} name="arrow-down" /></button><span></span>
          </div>
        </section>
        <section class="panel">
          <div class="row between" style="margin-bottom:10px"><b style="font-weight:500">PC volume</b>
            <button class=${'icon-btn' + (vol.muted ? ' on' : '')} aria-label=${vol.muted ? 'Unmute PC' : 'Mute PC'} onClick=${() => act('/api/pc/volume', { muted: !vol.muted })}><${Icon} name=${vol.muted ? 'volume-x' : 'volume-2'} /></button></div>
          <${Range} label="PC volume" value=${vol.level} showValue onInput=${setVol} onChange=${setVol} />
        </section>
        <section class="panel"><${NowPlaying} horizontal /></section>
      </div>
    </div>`;
}
