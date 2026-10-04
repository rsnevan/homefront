import { useState, useEffect, useRef } from 'preact/hooks';
import { html, Icon, useStore, api, act, toast, isOn, pct, lightName } from '../lib.mjs';
import { Plan, RoomSheet, SceneStrip, Toggle, Range } from '../components.mjs';

export function LightsView() {
  const s = useStore();
  const [room, setRoom] = useState(null);
  const lights = Object.values(s.entities).filter(e => e.entity_id.startsWith('light.'));
  const [setup, setSetup] = useState(() => new URLSearchParams(location.search).has('setup'));
  const owner = s.me?.role === 'owner';
  const liveRoom = room && (s.rooms || []).find(r => r.name === room.name);
  const cinema = s.cinema || {};

  return html`
    <div class="sec-head" style="margin-bottom:18px">
      <h1 class="h-page">Lights</h1>
      ${owner && lights.length > 0 && html`<button class="btn sm ghost" onClick=${() => setSetup(!setup)}>${setup ? 'Hide setup' : 'Add more lights'}</button>`}
    </div>

    ${(setup || (!lights.length && owner)) && s.snapshot && html`<${TuyaSetup} onDone=${() => setSetup(false)} />`}

    <div class="home-grid">
      <section class="span-8"><${Plan} onRoom=${setRoom} /></section>
      <section class="panel span-4 keep stack" style="align-content:start">
        <div class="row between">
          <div class="grow"><b style="font-weight:500">Follow what's playing</b>
            <div class="muted small">Dims ${(cinema.rooms || []).join(', ') || 'the TV room'} when a video plays on the HTPC, brings them up a little on pause and puts them back afterwards. Lights that are off stay off.</div></div>
          ${owner && html`<${Toggle} label="Follow what's playing" checked=${cinema.enabled} onChange=${v => api('/api/settings', { cinema: { ...(s.settingsCinema || {}), enabled: v, rooms: cinema.rooms, playingBrightness: 8, pausedBrightness: 35 } }).catch(e => toast(e.message, true))} />`}
        </div>
        <div class="row" style="gap:8px"><span class=${'dot' + (cinema.mode && cinema.mode !== 'idle' ? ' live' : '')}></span>
          <span class="small muted">${cinema.mode === 'playing' ? 'Dimmed for playback' : cinema.mode === 'paused' ? 'Paused, lights up a little' : 'Waiting for something to play'}</span></div>
        <div style="border-top:1px solid var(--line);padding-top:14px" class="stack">
          ${(s.rooms || []).map(r => {
            const ents = r.lights.map(id => s.entities[id]).filter(Boolean);
            const on = ents.filter(isOn);
            return html`<button class="row between" style="text-align:left;padding:6px 0" onClick=${() => setRoom(r)}>
              <span class="row" style="gap:12px"><${Icon} name=${r.icon || 'lamp'} /><span><b style="font-weight:500;display:block">${r.name}</b>
                <span class="muted small">${ents.length ? (on.length ? `${on.length} on, ${Math.round(on.reduce((a, e) => a + pct(e), 0) / on.length)}%` : 'Off') : 'No lights'}</span></span></span>
              <${Icon} name="chevron-right" size=${18} cls="faint" />
            </button>`;
          })}
        </div>
      </section>
      <section class="span-12"><${SceneStrip} /></section>
      ${owner && s.entities['input_boolean.wake_up_light'] && html`<section class="panel span-6"><${WakePanel} /></section>`}
    </div>
    ${liveRoom && html`<${RoomSheet} room=${liveRoom} onClose=${() => setRoom(null)} />`}`;
}

// Wake-up light: settings live in Home Assistant helpers so the automation keeps running even if homefront is down.
const DAYS = [['mon', 'Mon'], ['tue', 'Tue'], ['wed', 'Wed'], ['thu', 'Thu'], ['fri', 'Fri'], ['sat', 'Sat'], ['sun', 'Sun']];
function WakePanel() {
  const s = useStore();
  const on = s.entities['input_boolean.wake_up_light']?.state === 'on';
  const time = (s.entities['input_datetime.wake_up_time']?.state || '06:30:00').slice(0, 5);
  const days = (s.entities['input_text.wake_up_days']?.state || '').split(',').filter(Boolean);
  const fade = Math.round(+(s.entities['input_number.wake_up_fade_minutes']?.state || 15));
  const call = (domain, service, data, entity) => api('/api/ha/call', { domain, service, data, target: { entity_id: entity } }).catch(e => toast(e.message, true));
  const setDays = d => call('input_text', 'set_value', { value: DAYS.map(x => x[0]).filter(x => d.includes(x)).join(',') }, 'input_text.wake_up_days');
  const start = (() => { const [h, m] = time.split(':').map(Number); const t = h * 60 + m - fade; const w = (t + 1440) % 1440; return `${String(Math.floor(w / 60)).padStart(2, '0')}:${String(w % 60).padStart(2, '0')}`; })();
  return html`
    <div class="row between">
      <div class="grow"><b style="font-weight:500">Wake-up light</b>
        <div class="muted small">${on ? `The bedroom starts glowing at ${start} and is fully bright by ${time}.` : 'The bedroom fades from a dim warm glow to bright daylight before you get up.'}</div></div>
      <${Toggle} label="Wake-up light" checked=${on} onChange=${v => call('input_boolean', v ? 'turn_on' : 'turn_off', {}, 'input_boolean.wake_up_light').then(() => toast(v ? `Wake-up light on for ${time}` : 'Wake-up light off'))} />
    </div>
    <div class="form-grid" style="margin-top:14px">
      <div class="field"><label for="wt">Fully bright at</label>
        <input id="wt" class="input num" type="time" value=${time} onChange=${e => e.target.value && call('input_datetime', 'set_datetime', { time: e.target.value + ':00' }, 'input_datetime.wake_up_time')} /></div>
      <div class="field"><label for="wf">Fade over</label>
        <select id="wf" class="input" value=${fade} onChange=${e => call('input_number', 'set_value', { value: +e.target.value }, 'input_number.wake_up_fade_minutes')}>
          ${[5, 10, 15, 20, 30, 45].map(m => html`<option value=${m}>${m} minutes</option>`)}</select></div>
    </div>
    <div class="row wrap" style="gap:8px;margin-top:14px" role="group" aria-label="Days">
      ${DAYS.map(([k, n]) => html`<button class="chip" aria-pressed=${days.includes(k)} onClick=${() => setDays(days.includes(k) ? days.filter(x => x !== k) : [...days, k])}>${n}</button>`)}
    </div>`;
}

function TuyaSetup({ onDone }) {
  const s = useStore();
  const [code, setCode] = useState('');
  const [flow, setFlow] = useState(null);
  const [err, setErr] = useState(null);
  const [busy, setBusy] = useState(false);
  const poll = useRef();

  useEffect(() => () => clearTimeout(poll.current), []);
  useEffect(() => {
    if (flow?.status !== 'scan') return;
    poll.current = setTimeout(async () => {
      try {
        const r = await api('/api/setup/tuya/poll', { flowId: flow.flowId });
        if (r.status === 'done') toast('Lights connected');
        setFlow(r);
      } catch (e) { setErr(e.message); setFlow(null); }
    }, 4000);
    return () => clearTimeout(poll.current);
  }, [flow]);

  const start = async e => {
    e.preventDefault();
    setErr(null); setBusy(true);
    try {
      const r = await api('/api/setup/tuya/start', { userCode: code });
      if (r.status === 'error') setErr(r.error === 'already_configured' ? 'These lights are already connected.' : `Home Assistant stopped the setup (${r.error}).`);
      else if (r.status === 'code') setErr("Smart Life didn't accept that user code. Copy it again from the app.");
      else setFlow(r);
    } catch (e2) { setErr(/login_error|invalid/i.test(e2.message) ? "Smart Life didn't accept that user code. Copy it again from the app." : e2.message); }
    finally { setBusy(false); }
  };

  const count = Object.keys(s.entities).filter(k => k.startsWith('light.')).length;

  return html`
    <section class="panel" style="margin-bottom:16px">
      ${flow?.status === 'done' ? html`
        <div class="row between wrap">
          <div><b style="font-weight:500;font-size:18px">Lights connected</b>
            <div class="muted">${count ? `${count} ${count === 1 ? 'light' : 'lights'} found.` : 'They will appear here in a few seconds.'} Name a bulb after its room (like "Kitchen 1") in Smart Life and it lands in that room, or assign them in Settings.</div></div>
          <button class="btn" onClick=${onDone}>Done</button>
        </div>`
      : flow?.status === 'scan' ? html`
        <div class="row wrap" style="gap:28px;align-items:center">
          ${flow.qr && html`<img class="qr" src=${'/api/qr.svg?data=' + encodeURIComponent(flow.qr)} alt="QR code to scan with Smart Life" />`}
          <div class="grow" style="min-width:240px">
            <b style="font-weight:500;font-size:18px">Scan this with Smart Life</b>
            <ol class="steps" style="margin-top:14px">
              <li><span>In Smart Life, open <b>Me</b> and tap the <b>scan</b> icon at the top right.</span></li>
              <li><span>Point it at this code and tap <b>Confirm login</b>.</span></li>
              <li><span>Leave this page open. It finishes on its own.</span></li>
            </ol>
            <div class="row" style="margin-top:16px;gap:8px"><span class="dot live"></span><span class="small muted">Waiting for the scan</span></div>
          </div>
        </div>`
      : html`
        <div class="row wrap" style="gap:28px;align-items:flex-start">
          <div class="grow" style="min-width:260px">
            <b style="font-weight:500;font-size:18px">Connect your Tuya lights</b>
            <p class="muted" style="margin:6px 0 16px">Most Wi-Fi bulbs sold under local brands run on Tuya. Home Assistant links to them through the Smart Life app, once, and then they work from here without your phone.</p>
            <ol class="steps">
              <li><span>Install <b>Smart Life</b> and sign in. If your bulbs live in a brand's own app, add them in Smart Life with <b>+</b> then <b>Add device</b>.</span></li>
              <li><span>In Smart Life, go to <b>Me</b>, the settings cog, <b>Account and Security</b>, then <b>User Code</b>.</span></li>
              <li><span>Paste that code here. A QR code appears for you to scan.</span></li>
            </ol>
          </div>
          <form class="stack" style="min-width:260px;flex:1" onSubmit=${start}>
            <div class="field"><label for="uc">Smart Life user code</label>
              <input id="uc" class="input" value=${code} onInput=${e => setCode(e.target.value)} autocomplete="off" autocapitalize="off" spellcheck="false" placeholder="e.g. 9Hk2Lp" /></div>
            ${err && html`<div class="err">${err}</div>`}
            <button class="btn primary" disabled=${busy || code.trim().length < 4 || !s.ha?.connected}>${busy ? 'Starting…' : 'Show QR code'}</button>
            ${!s.ha?.connected && html`<div class="muted small">Home Assistant isn't connected yet.</div>`}
          </form>
        </div>`}
    </section>`;
}
