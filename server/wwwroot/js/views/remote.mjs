import { useState, useRef, useEffect } from 'preact/hooks';
import { html, Icon, useStore, useScreen, send, act, api, toast, useThrottled } from '../lib.mjs';
import { Range, NowPlaying } from '../components.mjs';

// Three ways to drive the HTPC. Touchpad is the default on phones: you look at the TV, not at the phone,
// like Unified Remote. Screen mirrors the TV for when you can't see it. Buttons is a TV-style remote.
const VIEWS = [['pad', 'Touchpad', 'move'], ['screen', 'Screen', 'monitor'], ['buttons', 'Buttons', 'circle-dot']];

export function Remote() {
  const s = useStore();
  const [view, setView] = useState(() => {
    try { const v = localStorage.getItem('hf-remote-view'); if (VIEWS.some(x => x[0] === v)) return v; } catch {}
    return matchMedia('(max-width: 960px)').matches ? 'pad' : 'screen';
  });
  useEffect(() => { try { localStorage.setItem('hf-remote-view', view); } catch {} }, [view]);

  return html`
    <div class="sec-head" style="margin-bottom:14px"><h1 class="h-page">Remote</h1>
      <span class="muted small ellipsis remote-fg">${s.stats?.foreground?.title || ''}</span></div>
    <div class="seg remote-tabs" role="tablist" aria-label="Remote style">
      ${VIEWS.map(([id, name, icon]) => html`
        <button role="tab" aria-selected=${view === id} class=${view === id ? 'on' : ''} onClick=${() => setView(id)}><${Icon} name=${icon} size=${16} />${name}</button>`)}
    </div>
    ${view === 'pad' ? html`<${PadView} />` : view === 'screen' ? html`<${ScreenView} />` : html`<${ButtonsView} />`}`;
}

const key = k => send({ t: 'key', k });

// ---------------- Touchpad ----------------

function PadView() {
  const s = useStore();
  const padRef = useRef();
  const [ripples, setRipples] = useState([]);
  const ripple = (x, y) => {
    const r = { id: Math.random(), x, y };
    setRipples(list => [...list, r]);
    setTimeout(() => setRipples(list => list.filter(i => i !== r)), 520);
  };

  const pointers = useRef(new Map());
  const g = useRef(null);
  const lastTap = useRef(0);
  const acc = useRef({ dx: 0, dy: 0, raf: 0 });
  const flush = () => {
    const m = acc.current; m.raf = 0;
    if (Math.abs(m.dx) + Math.abs(m.dy) < 0.3) return;
    send({ t: 'move', dx: m.dx, dy: m.dy }); m.dx = 0; m.dy = 0;
  };
  const queue = (dx, dy) => { const m = acc.current; m.dx += dx; m.dy += dy; if (!m.raf) m.raf = requestAnimationFrame(flush); };
  const local = e => { const r = padRef.current.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top, w: r.width }; };

  const down = e => {
    e.currentTarget.setPointerCapture(e.pointerId);
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.current.size === 1) {
      const p = local(e);
      // A tap then a drag straight after (within 300 ms) holds the button down: drag windows, select text.
      const dragArm = Date.now() - lastTap.current < 300;
      g.current = { start: Date.now(), moved: 0, two: false, p, dragArm, dragging: false, long: setTimeout(() => {
        if (g.current && g.current.moved < 8 && !g.current.dragArm) {
          g.current.longFired = true; send({ t: 'click', b: 'right' }); ripple(p.x, p.y); navigator.vibrate?.(15);
        }
      }, 550) };
    } else if (g.current) { g.current.two = true; clearTimeout(g.current.long); }
  };

  const move = e => {
    const prev = pointers.current.get(e.pointerId);
    if (!prev || !g.current) return;
    const dx = e.clientX - prev.x, dy = e.clientY - prev.y;
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    g.current.moved += Math.abs(dx) + Math.abs(dy);
    if (g.current.moved > 8) clearTimeout(g.current.long);
    if (g.current.two) { send({ t: 'scroll', dy: dy * 4, dx: -dx * 4 }); return; }
    if (g.current.dragArm && !g.current.dragging && g.current.moved > 8) { g.current.dragging = true; send({ t: 'button', b: 'left', down: true }); }
    const w = padRef.current.getBoundingClientRect().width;
    const speed = Math.hypot(dx, dy);
    const gain = (s.screen?.w || 1920) / w * (0.35 + Math.min(speed, 30) / 28);
    queue(dx * gain, dy * gain);
  };

  const up = e => {
    const gs = g.current;
    pointers.current.delete(e.pointerId);
    if (!gs || pointers.current.size > 0) return;
    clearTimeout(gs.long);
    g.current = null;
    if (gs.dragging) { send({ t: 'button', b: 'left', down: false }); return; }
    if (gs.longFired) return;
    const quick = Date.now() - gs.start < 300 && gs.moved < 10;
    if (!quick) return;
    if (gs.two) { send({ t: 'click', b: 'right' }); ripple(gs.p.x, gs.p.y); return; }
    send({ t: 'click', b: 'left' });
    lastTap.current = Date.now();
    ripple(gs.p.x, gs.p.y);
  };

  // Scroll strip down the right edge, for one-handed scrolling.
  const strip = useRef(null);
  const stripDown = e => { e.stopPropagation(); e.currentTarget.setPointerCapture(e.pointerId); strip.current = e.clientY; };
  const stripMove = e => { if (strip.current == null) return; const dy = e.clientY - strip.current; strip.current = e.clientY; send({ t: 'scroll', dy: dy * 5, dx: 0 }); };
  const stripUp = () => { strip.current = null; };

  return html`
    <div class="pad-wrap">
      <div ref=${padRef} class="touchpad" onPointerDown=${down} onPointerMove=${move} onPointerUp=${up} onPointerCancel=${up}
        onContextMenu=${e => e.preventDefault()} role="application"
        aria-label="Touchpad. Drag to move the pointer, tap to click, two fingers to scroll, two-finger tap or hold to right-click.">
        <span class="pad-hint muted small">Tap to click · two fingers to scroll · hold to right-click · tap then drag to drag</span>
        ${ripples.map(r => html`<span key=${r.id} class="ripple" style=${`left:${r.x}px;top:${r.y}px`}></span>`)}
        <div class="scroll-strip" aria-hidden="true" onPointerDown=${stripDown} onPointerMove=${stripMove} onPointerUp=${stripUp} onPointerCancel=${stripUp}>
          <${Icon} name="chevron-up" size=${16} /><${Icon} name="chevron-down" size=${16} /></div>
      </div>
      <div class="pad-buttons">
        <button class="key" onPointerDown=${e => { e.preventDefault(); send({ t: 'button', b: 'left', down: true }); }}
          onPointerUp=${() => send({ t: 'button', b: 'left', down: false })} onPointerCancel=${() => send({ t: 'button', b: 'left', down: false })}>Left</button>
        <button class="key" onClick=${() => send({ t: 'click', b: 'right' })}>Right</button>
      </div>
      <${LiveKeyboard} />
      <${MediaStrip} />
    </div>`;
}

// ---------------- Live keyboard ----------------
// Every key goes to the HTPC as you press it, like a real keyboard: no text box, no Send.

const SENTINEL = '  ';   // keeps something to delete, so Backspace still fires on an "empty" field (Android)

function LiveKeyboard() {
  const inp = useRef();
  const [open, setOpen] = useState(false);
  const [mods, setMods] = useState({ ctrl: false, alt: false, win: false });
  const modsRef = useRef(mods); modsRef.current = mods;

  const press = k => {
    const m = modsRef.current;
    const combo = [m.ctrl && 'ctrl', m.alt && 'alt', m.win && 'win', k].filter(Boolean).join('+');
    key(combo);
    if (m.ctrl || m.alt || m.win) setMods({ ctrl: false, alt: false, win: false });
  };
  const reset = () => { const el = inp.current; if (el) { el.value = SENTINEL; el.setSelectionRange(SENTINEL.length, SENTINEL.length); } };

  const beforeInput = e => {
    const t = e.inputType;
    if (t === 'insertText' || t === 'insertReplacementText') {
      const d = e.data ?? e.dataTransfer?.getData('text') ?? '';
      const m = modsRef.current;
      if (d.length === 1 && (m.ctrl || m.alt || m.win)) press(d.toLowerCase());
      else if (d) send({ t: 'type', s: d });
    }
    else if (t === 'deleteContentBackward' || t === 'deleteWordBackward') press('backspace');
    else if (t === 'insertLineBreak' || t === 'insertParagraph') press('enter');
    else if (t === 'insertFromPaste') { const d = e.dataTransfer?.getData('text') || e.data; if (d) send({ t: 'type', s: d }); }
    else return;   // composition (other languages' keyboards) falls through to onInput
    e.preventDefault();
    reset();
  };
  const onInput = () => {
    // Fallback for keyboards that don't send beforeinput: type whatever landed after the sentinel.
    const v = inp.current.value;
    if (v.startsWith(SENTINEL) && v.length > SENTINEL.length) send({ t: 'type', s: v.slice(SENTINEL.length) });
    else if (v.length < SENTINEL.length) press('backspace');
    reset();
  };
  const keyDown = e => {
    const named = { Enter: 'enter', Escape: 'esc', Tab: 'tab', ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' }[e.key];
    if (named) { e.preventDefault(); press(named); }
  };

  const toggle = () => {
    if (open) { inp.current.blur(); return; }
    reset(); inp.current.focus();   // must happen inside the tap for the phone keyboard to appear
  };

  const Mod = ({ id, label }) => html`<button class="chip" aria-pressed=${mods[id]} onPointerDown=${e => e.preventDefault()} onClick=${() => setMods({ ...mods, [id]: !mods[id] })}>${label}</button>`;
  const K = ({ k, label, icon }) => html`<button class="chip" onPointerDown=${e => e.preventDefault()} onClick=${() => press(k)} aria-label=${label}>${icon ? html`<${Icon} name=${icon} size=${16} />` : label}</button>`;

  // Paste the phone's clipboard onto the HTPC. Reading the clipboard needs HTTPS (the Tailscale address);
  // otherwise show a real field to long-press and paste into.
  const [pasteBox, setPasteBox] = useState(false);
  const paste = async () => {
    if (navigator.clipboard?.readText && window.isSecureContext) {
      try {
        const t = await navigator.clipboard.readText();
        if (t) { send({ t: 'type', s: t }); toast(t.length > 40 ? `Pasted ${t.length} characters` : `Pasted "${t}"`); }
        else toast('The clipboard is empty');
        return;
      } catch { /* refused or unsupported: fall back to the field */ }
    }
    setPasteBox(true);
  };
  const pasted = text => { if (text) { send({ t: 'type', s: text }); toast(text.length > 40 ? `Pasted ${text.length} characters` : `Pasted "${text}"`); } setPasteBox(false); };

  return html`
    <div class=${'kbd' + (open ? ' open' : '')}>
      <input ref=${inp} class="kbd-input" value=${SENTINEL} aria-label="Type on the HTPC"
        autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false" enterkeyhint="send"
        onBeforeInput=${beforeInput} onInput=${onInput} onKeyDown=${keyDown}
        onFocus=${() => setOpen(true)} onBlur=${() => setOpen(false)} />
      <div class="kbd-row">
        <button class=${'btn' + (open ? ' primary' : '')} onPointerDown=${e => open && e.preventDefault()} onClick=${toggle}>
          <${Icon} name="keyboard" size=${18} />${open ? 'Hide keyboard' : 'Keyboard'}</button>
        <button class="btn" onPointerDown=${e => open && e.preventDefault()} onClick=${paste}><${Icon} name="copy" size=${18} />Paste</button>
      </div>
      ${pasteBox && html`
        <div class="paste-box">
          <input class="input" autofocus placeholder="Long-press here and choose Paste" aria-label="Paste text to send to the HTPC"
            autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false"
            onPaste=${e => { e.preventDefault(); pasted(e.clipboardData?.getData('text') || ''); }}
            onKeyDown=${e => { if (e.key === 'Enter') { e.preventDefault(); pasted(e.target.value); } }} />
          <button class="btn" onClick=${e => pasted(e.currentTarget.previousElementSibling.value)}>Send</button>
          <button class="icon-btn plain" aria-label="Close" onClick=${() => setPasteBox(false)}><${Icon} name="x" /></button>
        </div>`}
      ${open && html`
        <div class="kbd-extra" aria-label="Extra keys">
          <${Mod} id="ctrl" label="Ctrl" /><${Mod} id="alt" label="Alt" /><${Mod} id="win" label="Win" />
          <${K} k="esc" label="Esc" /><${K} k="tab" label="Tab" />
          <${K} k="left" label="Left" icon="arrow-left" /><${K} k="right" label="Right" icon="arrow-right" />
          <${K} k="up" label="Up" icon="arrow-up" /><${K} k="down" label="Down" icon="arrow-down" />
          <${K} k="f11" label="F11" />
        </div>`}
    </div>`;
}

// ---------------- Media and volume, under every view ----------------

function MediaStrip() {
  const s = useStore();
  const vol = s.volume || {};
  const m = s.media || {};
  const playing = m.active && m.status === 'playing';
  const step = d => api('/api/pc/volume', { level: Math.max(0, Math.min(100, Math.round((vol.level ?? 50) + d))) }).catch(e => toast(e.message, true));
  return html`
    <div class="media-strip">
      <button class="icon-btn" aria-label="Previous" onClick=${() => act('/api/pc/media/prev', {})}><${Icon} name="skip-back" /></button>
      <button class="icon-btn big" aria-label=${playing ? 'Pause' : 'Play'} onClick=${() => act('/api/pc/media/toggle', {})}><${Icon} name=${playing ? 'pause' : 'play'} /></button>
      <button class="icon-btn" aria-label="Next" onClick=${() => act('/api/pc/media/next', {})}><${Icon} name="skip-forward" /></button>
      <span class="media-strip-gap"></span>
      <button class="icon-btn" aria-label="Volume down" onClick=${() => step(-4)}><${Icon} name="minus" /></button>
      <button class=${'icon-btn' + (vol.muted ? ' on' : '')} aria-label=${vol.muted ? 'Unmute' : 'Mute'} onClick=${() => act('/api/pc/volume', { muted: !vol.muted })}>
        <${Icon} name=${vol.muted ? 'volume-x' : 'volume-2'} /></button>
      <span class="num small muted vol-num">${vol.level != null ? Math.round(vol.level) : ''}</span>
      <button class="icon-btn" aria-label="Volume up" onClick=${() => step(4)}><${Icon} name="plus" /></button>
    </div>`;
}

// ---------------- Screen (mirror) ----------------

function ScreenView() {
  const s = useStore();
  const [mode, setMode] = useState(() => { try { return localStorage.getItem('hf-remote-mode') || 'tap'; } catch { return 'tap'; } });
  const [hd, setHd] = useState(false);
  const mirrorRef = useRef();
  const src = useScreen(mirrorRef, hd ? 1600 : 960, hd ? 900 : 550);
  const [ripples, setRipples] = useState([]);
  useEffect(() => { try { localStorage.setItem('hf-remote-mode', mode); } catch {} }, [mode]);

  const ripple = (x, y) => {
    const r = { id: Math.random(), x, y };
    setRipples(list => [...list, r]);
    setTimeout(() => setRipples(list => list.filter(i => i !== r)), 520);
  };

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

  return html`
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
          <button class="chip" aria-pressed=${mode === 'pad'} onClick=${() => setMode('pad')}><${Icon} name="move" size=${16} />Drag the pointer</button>
          <button class="chip" aria-pressed=${hd} onClick=${() => setHd(!hd)}>Sharper preview</button>
          <span class="muted small mirror-hint">${mode === 'tap' ? 'Hold to right-click. Two fingers scroll.' : 'Two-finger tap right-clicks.'}</span>
        </div>
        <div style="margin-top:14px"><${LiveKeyboard} /></div>
        <${MediaStrip} />
      </div>
      <div class="stack" style="gap:16px">
        <section class="panel"><${Keys} /></section>
        <section class="panel"><${NowPlaying} horizontal /></section>
      </div>
    </div>`;
}

// ---------------- Buttons ----------------

function Keys() {
  return html`
    <div class="dpad">
      <span></span><button class="key" onClick=${() => key('up')} aria-label="Up"><${Icon} name="arrow-up" /></button><span></span>
      <button class="key" onClick=${() => key('left')} aria-label="Left"><${Icon} name="arrow-left" /></button>
      <button class="key ok" onClick=${() => key('enter')} aria-label="OK (Enter)">OK</button>
      <button class="key" onClick=${() => key('right')} aria-label="Right"><${Icon} name="arrow-right" /></button>
      <span></span><button class="key" onClick=${() => key('down')} aria-label="Down"><${Icon} name="arrow-down" /></button><span></span>
    </div>
    <div class="keys" style="margin-top:16px">
      <button class="key" onClick=${() => key('alt+left')} aria-label="Back"><${Icon} name="undo-2" size=${18} />Back</button>
      <button class="key" onClick=${() => key('esc')}>Esc</button>
      <button class="key" onClick=${() => key('f')} aria-label="Full screen video (F)"><${Icon} name="maximize" size=${18} /></button>
      <button class="key" onClick=${() => key('f11')}>F11</button>
      <button class="key" onClick=${() => key('tab')}>Tab</button>
      <button class="key" onClick=${() => key('ctrl+w')}>Close tab</button>
      <button class="key" onClick=${() => key('alt+tab')}>Switch app</button>
      <button class="key" onClick=${() => key('win+d')}>Desktop</button>
    </div>`;
}

function ButtonsView() {
  return html`
    <div class="buttons-view">
      <section class="panel"><${Keys} /></section>
      <${MediaStrip} />
      <section class="panel"><${NowPlaying} horizontal /></section>
    </div>`;
}
