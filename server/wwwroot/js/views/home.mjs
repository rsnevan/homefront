import { useState, useEffect } from 'preact/hooks';
import { html, Icon, useStore, useNow, api, navigate, greeting, dateLong, hhmm, wx, prayerInfo, until, isOn } from '../lib.mjs';
import { Plan, RoomSheet, SceneStrip, NowPlaying, TvPanel, PcPanel, Launch, Weather, Prayer, Tile, ItemSheet, CameraPanel } from '../components.mjs';

let cachedShelf = null;

export function Home() {
  const s = useStore();
  const now = useNow(1000);
  const [room, setRoom] = useState(null);
  const [item, setItem] = useState(null);
  const [shelf, setShelf] = useState(cachedShelf);

  useEffect(() => {
    if (!s.jellyfin?.ok) return;
    api('/api/jf/home').then(h => {
      const seen = new Set();
      const list = [...(h.resume || []), ...(h.nextUp || [])].filter(i => !seen.has(i.seriesId || i.id) && seen.add(i.seriesId || i.id));
      cachedShelf = { cont: list, latest: [...(h.movies || []), ...(h.shows || [])].slice(0, 16) }; setShelf(cachedShelf);
    }).catch(() => setShelf({ cont: [], latest: [] }));
  }, [s.jellyfin?.ok, s.player?.status === 'stopped']);

  const w = s.weather?.now;
  const pi = prayerInfo(s.prayer, now);
  const lightsOn = Object.values(s.entities).filter(e => e.entity_id.startsWith('light.') && isOn(e)).length;
  const name = s.me?.role === 'guest' ? s.me.name : s.home?.owner;
  const liveRoom = room && (s.rooms || []).find(r => r.name === room.name);

  return html`
    <header class="home-head">
      <div>
        <h1 class="greet">${greeting(now)}${name ? `, ${name}` : ''}</h1>
        <div class="greet-sub">
          ${w && html`<span><${Icon} name=${wx(w.code, w.isDay).icon} size=${18} />${Math.round(w.temp)}° ${wx(w.code, w.isDay).text.toLowerCase()}</span>`}
          ${pi?.next && html`<span><${Icon} name="moon-star" size=${18} />${pi.next.name} ${pi.next.time}, ${pi.next.tomorrow ? 'tomorrow' : until(pi.mins)}</span>`}
          ${lightsOn > 0 && html`<span><${Icon} name="lightbulb" size=${18} />${lightsOn} ${lightsOn === 1 ? 'light' : 'lights'} on</span>`}
          ${s.cinema?.mode && s.cinema.mode !== 'idle' && html`<span><span class="dot live"></span>Lights following the movie</span>`}
          ${s.timer?.endsAt && html`<span><${Icon} name="timer" size=${18} />Off in ${Math.max(0, Math.ceil((s.timer.endsAt - now.getTime()) / 60000))} min</span>`}
        </div>
      </div>
      <div class="clock">${hhmm(now)}<small>${dateLong(now)}</small></div>
    </header>

    <div class="home-grid">
      <section class="span-8" aria-label="Home">
        <${Plan} onRoom=${setRoom} />
      </section>
      <section class="panel span-4 keep" aria-label="Now playing">
        <${NowPlaying} />
      </section>

      <section class="span-12" aria-label="Scenes"><${SceneStrip} /></section>

      <section class="panel span-4" aria-labelledby="h-tv"><div class="sec-head"><h2 class="h-sec" id="h-tv">TV</h2></div><${TvPanel} /></section>
      <section class="panel span-4" aria-labelledby="h-pc">
        <div class="sec-head"><h2 class="h-sec" id="h-pc">HTPC</h2><a class="link" href="/remote" onClick=${e => { e.preventDefault(); navigate('/remote'); }}>Remote</a></div>
        <${PcPanel} />
      </section>
      <section class="panel span-4" aria-labelledby="h-launch"><div class="sec-head"><h2 class="h-sec" id="h-launch">Open on the TV</h2></div><${Launch} /></section>

      ${s.cameras?.length > 0 && html`<section class="panel span-6" aria-labelledby="h-cam"><div class="sec-head"><h2 class="h-sec" id="h-cam">${s.cameras.length > 1 ? 'Cameras' : 'Camera'}</h2></div><${CameraPanel} /></section>`}

      ${s.jellyfin?.ok && html`
        <section class="span-12" aria-labelledby="h-cont" style="margin-top:12px">
          <div class="sec-head"><h2 class="h-sec" id="h-cont">${shelf?.cont?.length ? 'Continue watching' : 'Recently added'}</h2>
            <a class="link" href="/library" onClick=${e => { e.preventDefault(); navigate('/library'); }}>Library</a></div>
          ${!shelf ? html`<div class="shelf">${[1, 2, 3, 4].map(() => html`<div class="skeleton" style="aspect-ratio:16/9"></div>`)}</div>`
            : html`<div class="shelf">${(shelf.cont.length ? shelf.cont : shelf.latest).map(i => html`<${Tile} key=${i.id} item=${i} onOpen=${x => setItem(x.type === 'Episode' && !shelf.cont.length ? x.seriesId : x.id)} />`)}</div>`}
        </section>`}

      <section class="panel span-6" aria-labelledby="h-wx"><div class="sec-head"><h2 class="h-sec" id="h-wx">${s.weather?.place || 'Weather'}</h2></div><${Weather} /></section>
      <section class="panel span-6" aria-labelledby="h-pr"><div class="sec-head"><h2 class="h-sec" id="h-pr">Prayer times</h2></div><${Prayer} /></section>
    </div>

    ${liveRoom && html`<${RoomSheet} room=${liveRoom} onClose=${() => setRoom(null)} />`}
    ${item && html`<${ItemSheet} id=${item} onClose=${() => setItem(null)} />`}`;
}
