import { useState, useEffect, useRef } from 'preact/hooks';
import { html, Icon, useStore, api, act, jfImg, dur, epLabel, toast } from '../lib.mjs';
import { Tile, ItemSheet, NowPlaying } from '../components.mjs';

let cachedHome = null;

export function Library() {
  const s = useStore();
  const [tab, setTab] = useState('home');
  const [home, setHome] = useState(cachedHome);
  const [grid, setGrid] = useState(null);
  const [q, setQ] = useState('');
  const [results, setResults] = useState(null);
  const [open, setOpen] = useState(null);
  const searchTimer = useRef();

  useEffect(() => { if (s.jellyfin?.ok) api('/api/jf/home').then(h => { cachedHome = h; setHome(h); }).catch(e => toast(e.message, true)); }, [s.jellyfin?.ok]);
  useEffect(() => {
    if (tab === 'home') return;
    setGrid(null);
    api(`/api/jf/browse?type=${tab === 'shows' ? 'Series' : 'Movie'}&limit=200`).then(setGrid).catch(e => toast(e.message, true));
  }, [tab]);
  useEffect(() => {
    clearTimeout(searchTimer.current);
    if (q.trim().length < 2) { setResults(null); return; }
    searchTimer.current = setTimeout(() => api('/api/jf/search?q=' + encodeURIComponent(q.trim())).then(setResults), 250);
  }, [q]);

  if (!s.snapshot) return html`<div class="skeleton" style="height:360px;border-radius:var(--r-xl)"></div>`;
  if (!s.jellyfin?.ok) return html`
    <h1 class="h-page" style="margin-bottom:16px">Library</h1>
    <div class="panel empty"><b style="color:var(--text);font-weight:500">Jellyfin isn't connected</b><p style="margin:0">homefront couldn't reach Jellyfin on the HTPC. Check it's running at port 8096.</p></div>`;

  const hero = home?.resume?.[0] || home?.nextUp?.[0] || home?.movies?.[0];
  const heroImg = hero && (hero.hasBackdrop ? jfImg(hero.id, 'Backdrop', 1600) : hero.parentBackdropId ? jfImg(hero.parentBackdropId, 'Backdrop', 1600) : null);
  const openItem = i => setOpen(i.type === 'Season' ? i.seriesId : i.id);
  const playing = s.player?.status !== 'stopped';

  return html`
    ${tab === 'home' && !q && hero && html`
      <section class="lib-hero">
        ${heroImg && html`<img class="bg" src=${heroImg} alt="" />`}
        <div class="veil"></div>
        <div class="copy">
          <div style="color:rgba(230, 236, 244, .7);font-size:14px;margin-bottom:10px">${hero.position > 30 ? 'Pick up where you left off' : home.resume?.length ? '' : 'New in your library'}</div>
          <h2>${hero.type === 'Episode' ? hero.seriesName : hero.name}</h2>
          <p>${hero.type === 'Episode' ? `${epLabel(hero)}, ${hero.name}. ` : ''}${hero.overview || ''}</p>
          <div class="row wrap">
            <button class="btn primary" onClick=${() => act('/api/jf/play', { id: hero.id }, 'Starting on the TV')}><${Icon} name="play" />${hero.position > 30 ? `Resume from ${dur(hero.position)}` : 'Play on TV'}</button>
            <button class="btn" style="background:rgba(230, 236, 244, .12);color:#e6ecf4;border-color:rgba(230, 236, 244, .2)" onClick=${() => openItem(hero.type === 'Episode' ? { id: hero.seriesId } : hero)}>Details</button>
          </div>
        </div>
      </section>`}

    ${playing && html`<section class="panel" style="margin-bottom:24px"><${NowPlaying} horizontal /></section>`}

    <div class="lib-tabs">
      <button class="chip" aria-pressed=${tab === 'home'} onClick=${() => setTab('home')}>For you</button>
      <button class="chip" aria-pressed=${tab === 'movies'} onClick=${() => setTab('movies')}>Movies</button>
      <button class="chip" aria-pressed=${tab === 'shows'} onClick=${() => setTab('shows')}>Shows</button>
      <label class="search"><${Icon} name="search" size=${18} /><span class="sr">Search the library</span>
        <input class="input" type="search" placeholder="Search movies and shows" value=${q} onInput=${e => setQ(e.target.value)} /></label>
    </div>

    ${results ? html`
      <h2 class="h-sec" style="margin-bottom:14px">${results.length ? `Results for “${q.trim()}”` : `Nothing matches “${q.trim()}”`}</h2>
      <div class="grid-posters">${results.map(i => html`<${Tile} key=${i.id} item=${i} poster=${i.type !== 'Episode'} onOpen=${openItem} />`)}</div>`
    : tab === 'home' ? html`
      ${!home ? html`<div class="shelf">${[1, 2, 3, 4].map(() => html`<div class="skeleton" style="aspect-ratio:16/9"></div>`)}</div>` : html`
        <${Row} title="Continue watching" items=${home.resume} onOpen=${openItem} />
        <${Row} title="Next up" items=${home.nextUp} onOpen=${openItem} />
        <${Row} title="New movies" items=${home.movies} onOpen=${openItem} poster />
        <${Row} title="New episodes" items=${home.shows} onOpen=${i => openItem(i.type === 'Episode' ? { id: i.seriesId } : i)} poster />
        ${!home.resume?.length && !home.nextUp?.length && !home.movies?.length && !home.shows?.length && html`
          <div class="panel empty"><b style="color:var(--text);font-weight:500">The library is still filling up</b><p style="margin:0">Jellyfin may still be scanning. Titles show up here as they're found.</p></div>`}`}`
    : html`
      ${!grid ? html`<div class="grid-posters">${Array.from({ length: 12 }, () => html`<div class="skeleton" style="aspect-ratio:2/3"></div>`)}</div>`
        : grid.items.length === 0 ? html`<div class="panel empty">No ${tab} yet. If you just added some, Jellyfin may still be scanning.</div>`
        : html`<div class="grid-posters">${grid.items.map(i => html`<${Tile} key=${i.id} item=${i} poster onOpen=${openItem} />`)}</div>`}`}

    ${open && html`<${ItemSheet} id=${open} onClose=${() => setOpen(null)} />`}`;
}

function Row({ title, items, onOpen, poster }) {
  if (!items?.length) return null;
  return html`<section style="margin-bottom:30px">
    <div class="sec-head"><h2 class="h-sec">${title}</h2></div>
    <div class=${'shelf' + (poster ? ' posters' : '')}>${items.map(i => html`<${Tile} key=${i.id} item=${i} poster=${poster} onOpen=${onOpen} />`)}</div>
  </section>`;
}
